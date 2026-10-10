"""Filesystem operations shared by the UI and tests; no GUI dependencies."""
from __future__ import annotations

import errno
import json
import os
from pathlib import Path
import shutil
import tempfile
import uuid


class OrganizerError(Exception):
    """An operation the UI can explain to the user."""


def valid_name(name: str) -> str:
    if not isinstance(name, str) or not name or name != name.strip() or name.endswith("."):
        raise OrganizerError("名称不能为空，也不能以空格或句点结尾。")
    if len(name) > 120 or any(ord(c) < 32 or c in '<>:"/\\|?*' for c in name):
        raise OrganizerError('名称过长，或含有 Windows 不支持的字符：<>:"/\\|?*')
    reserved = {"CON", "PRN", "AUX", "NUL"}
    reserved.update(f"{prefix}{i}" for prefix in ("COM", "LPT") for i in range(1, 10))
    if name in {".", ".."} or name.split(".")[0].upper() in reserved:
        raise OrganizerError("这个名称是 Windows 保留名称，请换一个。")
    return name


def move_file(source: Path, target: Path) -> None:
    """Move regular files without overwriting another file, even on a name race."""
    if source.is_symlink() or not source.is_file():
        raise OrganizerError("只能整理普通文件。文件夹和符号链接请在资源管理器中处理。")
    if target.exists() or target.is_symlink():
        raise OrganizerError(f"目标已存在同名文件：{target.name}。请先改名。")
    # A hard link gives an exclusive destination and preserves the original bytes.
    try:
        os.link(source, target)
    except FileExistsError as exc:
        raise OrganizerError(f"目标已存在同名文件：{target.name}。") from exc
    except OSError as exc:
        if exc.errno not in {errno.EXDEV, errno.EPERM, errno.EACCES, errno.ENOTSUP}:
            raise
        # Cross-volume moves use exclusive creation, never shutil.move's overwrite.
        created = False
        try:
            with source.open("rb") as src, target.open("xb") as dst:
                created = True
                shutil.copyfileobj(src, dst)
                dst.flush()
                os.fsync(dst.fileno())
            shutil.copystat(source, target)
        except BaseException:
            if created:
                target.unlink(missing_ok=True)
            raise
    try:
        source.unlink()
    except BaseException:
        target.unlink(missing_ok=True)
        raise


class Organizer:
    def __init__(self, config: Path, desktop: Path):
        self.config = Path(config)
        self.desktop = Path(desktop).resolve()
        self.sections: list[dict] = []
        self.history: list[tuple[str, Path, Path]] = []
        if self.config.exists():
            try:
                data = json.loads(self.config.read_text(encoding="utf-8"))
                if not isinstance(data, dict) or not isinstance(data.get("sections"), list):
                    raise ValueError("invalid configuration structure")
                if data.get("version") != 1:
                    raise ValueError("unsupported configuration version")
                self.desktop = Path(data["desktop"]).resolve()
                seen = set()
                for section in data["sections"]:
                    if not isinstance(section, dict):
                        raise ValueError("invalid section structure")
                    valid_name(section["name"])
                    if not isinstance(section["id"], str) or section["id"] in seen:
                        raise ValueError("duplicate/invalid section ID")
                    seen.add(section["id"])
                    if section["name"].casefold() in {
                        s["name"].casefold() for s in self.sections
                    }:
                        raise ValueError("duplicate section name")
                    self.sections.append({
                        "id": section["id"], "name": section["name"],
                        "x": int(section.get("x", 50)), "y": int(section.get("y", 80)),
                        "collapsed": bool(section.get("collapsed", False)),
                    })
            except (ValueError, KeyError, TypeError, OrganizerError) as exc:
                raise OrganizerError(
                    "设置文件无法读取。为保护原设置，程序已停止。"
                    f"\n设置文件：{self.config}\n原因：{exc}"
                ) from exc

    def save(self) -> None:
        self.config.parent.mkdir(parents=True, exist_ok=True)
        data = {"version": 1, "desktop": str(self.desktop), "sections": self.sections}
        handle, name = tempfile.mkstemp(dir=self.config.parent, suffix=".tmp")
        try:
            with os.fdopen(handle, "w", encoding="utf-8") as stream:
                json.dump(data, stream, ensure_ascii=False, indent=2)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(name, self.config)
        finally:
            Path(name).unlink(missing_ok=True)

    def section(self, section_id: str) -> dict:
        for section in self.sections:
            if section["id"] == section_id:
                return section
        raise OrganizerError("分区不存在，请刷新后重试。")

    def folder(self, section_id: str) -> Path:
        path = self.desktop / self.section(section_id)["name"]
        if path.is_symlink():
            raise OrganizerError("分区目录变成了符号链接，请检查目录后再试。")
        return path

    def files(self, section_id: str) -> list[Path]:
        folder = self.folder(section_id)
        if not folder.is_dir():
            raise OrganizerError(f"分区文件夹不存在：{folder}。请恢复文件夹后重试。")
        return sorted(folder.iterdir(), key=lambda p: (not p.is_dir(), p.name.casefold()))

    def add_section(self, name: str) -> dict:
        valid_name(name)
        if any(s["name"].casefold() == name.casefold() for s in self.sections):
            raise OrganizerError("已经有这个名字的分区了。")
        folder = self.desktop / name
        # Do not silently adopt a folder containing the user's existing files.
        folder.mkdir(exist_ok=False)
        index = len(self.sections)
        section = {"id": uuid.uuid4().hex, "name": name,
                   "x": 45 + (index % 4) * 315, "y": 90 + (index // 4) * 370,
                   "collapsed": False}
        self.sections.append(section)
        try:
            self.save()
        except BaseException:
            self.sections.remove(section)
            folder.rmdir()
            raise
        return section

    def rename_section(self, section_id: str, name: str) -> None:
        valid_name(name)
        section = self.section(section_id)
        old_name = section["name"]
        if name == old_name:
            return
        if any(s["name"].casefold() == name.casefold() for s in self.sections):
            raise OrganizerError("名称重复。只改大小写时，请先换一个名称。")
        source = self.folder(section_id)
        target = self.desktop / name
        if target.exists() or target.is_symlink():
            raise OrganizerError("桌面上已有这个名称的文件或文件夹，请换一个。")
        source.rename(target)
        section["name"] = name
        try:
            self.save()
        except BaseException:
            section["name"] = old_name
            target.rename(source)
            raise
        self.history.append(("folder", source, target))

    def update_layout(self, section_id: str, **values) -> None:
        if set(values) - {"x", "y", "collapsed"}:
            raise OrganizerError("无效的分区设置。")
        section = self.section(section_id)
        previous = section.copy()
        section.update(values)
        try:
            self.save()
        except BaseException:
            section.clear()
            section.update(previous)
            raise

    def classify(self, source: Path, section_id: str) -> Path:
        source = Path(source).absolute()
        target = self.folder(section_id) / source.name
        if source == target:
            raise OrganizerError("这个文件已经在该分区里了。")
        move_file(source, target)
        self.history.append(("file", source, target))
        return target

    def rename_file(self, section_id: str, filename: str, name: str) -> Path:
        valid_name(filename)
        valid_name(name)
        source = self.folder(section_id) / filename
        target = source.with_name(name)
        if source == target:
            return source
        move_file(source, target)
        self.history.append(("file", source, target))
        return target

    def undo(self) -> None:
        if not self.history:
            raise OrganizerError("本次打开期间，还没有可以撤销的操作。")
        kind, source, target = self.history[-1]
        if kind == "file":
            move_file(target, source)
        else:
            if source.exists() or source.is_symlink():
                raise OrganizerError("原来的文件夹名称已被占用，无法撤销。")
            section = next((s for s in self.sections if s["name"] == target.name), None)
            if section is None:
                raise OrganizerError("分区已发生变化，无法撤销。")
            target.rename(source)
            section["name"] = source.name
            try:
                self.save()
            except BaseException:
                section["name"] = target.name
                source.rename(target)
                raise
        self.history.pop()
