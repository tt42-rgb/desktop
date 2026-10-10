"""A packaged-executable check, isolated from the user's actual desktop."""
from __future__ import annotations

import json
from pathlib import Path
import tempfile
import tkinter as tk
import traceback

from .app import App, desktop_path
from .model import Organizer


def run(output: Path) -> int:
    result = {"ok": False, "checks": []}
    root = None
    try:
        actual_desktop = desktop_path()
        if not actual_desktop.is_absolute():
            raise RuntimeError("Desktop path is not absolute")
        result["checks"].append("desktop-path-query")
        with tempfile.TemporaryDirectory(prefix="desktop-organizer-test-") as directory:
            base = Path(directory)
            desktop = base / "测试桌面"
            desktop.mkdir()
            config = base / "设置.json"
            model = Organizer(config, desktop)
            sid = model.add_section("工作资料")["id"]
            source = desktop / "测试文件.txt"
            source.write_text("packaged executable test: 文件", encoding="utf-8")
            target = model.classify(source, sid)
            if source.exists() or target.read_text(encoding="utf-8") != "packaged executable test: 文件":
                raise RuntimeError("File classification failed")
            result["checks"].append("unicode-file-classification")

            root = tk.Tk()
            app = App(root, model)
            root.update()
            panel = app.panels[sid]
            if len(panel.tree.get_children()) != 1:
                raise RuntimeError("The GUI did not display the classified file")
            result["checks"].append("bundled-tk-gui")
            panel.collapse()
            root.update()
            if panel.body.winfo_manager() or not model.section(sid)["collapsed"]:
                raise RuntimeError("Collapse failed")
            panel.collapse()
            root.update()
            if panel.body.winfo_manager() != "pack":
                raise RuntimeError("Expand failed")
            result["checks"].append("collapse-expand")

            model.rename_file(sid, target.name, "新名称.txt")
            model.undo()
            model.undo()
            if not source.is_file() or target.exists():
                raise RuntimeError("Undo failed")
            if len(Organizer(config, desktop).sections) != 1:
                raise RuntimeError("Settings could not be restored")
            result["checks"].append("rename-undo-settings")
            result["ok"] = True
    except Exception:
        result["error"] = traceback.format_exc()
    finally:
        if root is not None:
            for callback in root.tk.splitlist(root.tk.call("after", "info")):
                root.after_cancel(callback)
            root.destroy()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    return 0 if result["ok"] else 1
