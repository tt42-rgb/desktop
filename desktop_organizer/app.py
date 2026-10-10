"""Small, independent desktop panels using Python's built-in Tk GUI."""
from __future__ import annotations

import ctypes
import os
from pathlib import Path
import subprocess
import sys
import tkinter as tk
from tkinter import filedialog, messagebox, simpledialog, ttk

from .model import Organizer, OrganizerError

BG = "#f3f5fa"
INK = "#26334a"
BLUE = "#345fe8"


def desktop_path() -> Path:
    """Use Windows' known folder API, including redirected/OneDrive desktops."""
    if sys.platform == "win32":
        class GUID(ctypes.Structure):
            _fields_ = [("a", ctypes.c_ulong), ("b", ctypes.c_ushort),
                        ("c", ctypes.c_ushort), ("d", ctypes.c_ubyte * 8)]
        desktop_id = GUID(0xB4BFCC3A, 0xDB2C, 0x424C,
                          (ctypes.c_ubyte * 8)(0xB0, 0x29, 0x7F, 0xE9, 0x9A, 0x87, 0xC6, 0x41))
        result = ctypes.c_wchar_p()
        try:
            status = ctypes.windll.shell32.SHGetKnownFolderPath(
                ctypes.byref(desktop_id), 0, None, ctypes.byref(result))
            if status == 0:
                try:
                    return Path(result.value)
                finally:
                    ctypes.windll.ole32.CoTaskMemFree(ctypes.cast(result, ctypes.c_void_p))
        except OSError:
            pass
    return Path.home() / "Desktop"


def config_path() -> Path:
    base = Path(os.environ.get("APPDATA", str(Path.home() / ".config")))
    return base / "DesktopOrganizer" / "settings.json"


def open_path(path: Path, select: bool = False) -> None:
    if not path.exists():
        raise OrganizerError("文件已经不在原来的位置，请刷新后重试。")
    if sys.platform == "win32":
        if select:
            subprocess.Popen(["explorer.exe", "/select,", str(path)])
        else:
            os.startfile(str(path))
    else:
        subprocess.Popen(["open" if sys.platform == "darwin" else "xdg-open",
                          str(path.parent if select else path)])


class Panel:
    def __init__(self, app: "App", section: dict):
        self.app = app
        self.section_id = section["id"]
        self.window = tk.Toplevel(app.root)
        self.window.configure(bg=BG)
        self.window.resizable(False, False)
        if sys.platform == "win32":
            self.window.attributes("-toolwindow", True)
        self.window.protocol("WM_DELETE_WINDOW", self.hide)
        self.window.bind("<Escape>", lambda event: app.show_manager())
        self.header = tk.Frame(self.window, bg=BLUE, padx=8, pady=8)
        self.header.pack(fill="x")
        self.toggle = tk.Button(self.header, text="▾", command=self.collapse,
                                bg=BLUE, fg="white", relief="flat", width=2)
        self.toggle.pack(side="left")
        self.title = tk.Label(self.header, bg=BLUE, fg="white", anchor="w",
                              font=("Microsoft YaHei UI", 11, "bold"))
        self.title.pack(side="left", fill="x", expand=True)
        tk.Button(self.header, text="＋", command=self.add_files, bg=BLUE,
                  fg="white", relief="flat").pack(side="right")
        tk.Button(self.header, text="⋯", command=self.panel_menu, bg=BLUE,
                  fg="white", relief="flat").pack(side="right")
        for widget in (self.header, self.title):
            widget.bind("<ButtonPress-1>", self.drag_start)
            widget.bind("<B1-Motion>", self.drag)
            widget.bind("<ButtonRelease-1>", self.drag_end)
            widget.bind("<Double-Button-1>", lambda event: self.collapse())
        self.body = tk.Frame(self.window, bg=BG, padx=8, pady=8)
        self.body.pack(fill="both", expand=True)
        file_area = tk.Frame(self.body, bg=BG)
        file_area.pack(fill="both", expand=True)
        self.tree = ttk.Treeview(file_area, columns=("kind",), show="tree headings",
                                 selectmode="browse", height=8)
        self.tree.heading("#0", text="文件名称")
        self.tree.heading("kind", text="类型")
        self.tree.column("#0", width=207)
        self.tree.column("kind", width=52, stretch=False)
        scrollbar = ttk.Scrollbar(file_area, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=scrollbar.set)
        scrollbar.pack(side="right", fill="y")
        self.tree.pack(fill="both", expand=True)
        self.tree.bind("<Double-1>", lambda event: self.open_selected())
        self.tree.bind("<Button-3>", self.file_menu)
        self.tree.bind("<F2>", lambda event: self.rename_file())
        self.tree.bind("<Return>", lambda event: self.open_selected())
        self.note = tk.Label(self.body, text="双击打开 · 右键分类或改名", bg=BG,
                             fg="#657089", anchor="w", font=("Microsoft YaHei UI", 9))
        self.note.pack(fill="x", pady=(8, 0))
        self.paths: dict[str, Path] = {}
        self.drag_origin = None
        self.render_layout()
        self.refresh()

    @property
    def section(self):
        return self.app.model.section(self.section_id)

    def hide(self):
        self.window.withdraw()
        if all(panel.window.state() == "withdrawn" for panel in self.app.panels.values()):
            self.app.show_manager()

    def render_layout(self):
        section = self.section
        self.window.title(f"{section['name']} · 桌面分区")
        self.title.configure(text=section["name"])
        collapsed = section["collapsed"]
        self.toggle.configure(text="▸" if collapsed else "▾")
        if collapsed:
            self.body.pack_forget()
        elif not self.body.winfo_manager():
            self.body.pack(fill="both", expand=True)
        screen_w, screen_h = self.window.winfo_screenwidth(), self.window.winfo_screenheight()
        x = max(0, min(section["x"], screen_w - 300))
        y = max(0, min(section["y"], screen_h - (90 if collapsed else 380)))
        self.window.geometry(f"300x{50 if collapsed else 330}+{x}+{y}")

    def collapse(self):
        if self.app.action(lambda: self.app.model.update_layout(
                self.section_id, collapsed=not self.section["collapsed"])):
            self.render_layout()
            self.app.refresh_manager()

    def drag_start(self, event):
        self.drag_origin = (event.x_root, event.y_root,
                            self.window.winfo_x(), self.window.winfo_y())

    def drag(self, event):
        if self.drag_origin:
            px, py, wx, wy = self.drag_origin
            x, y = max(0, wx + event.x_root - px), max(0, wy + event.y_root - py)
            self.window.geometry(f"+{x}+{y}")

    def drag_end(self, event):
        if self.drag_origin:
            self.app.action(lambda: self.app.model.update_layout(
                self.section_id, x=self.window.winfo_x(), y=self.window.winfo_y()))
            self.drag_origin = None

    def refresh(self):
        selection = self.tree.selection()
        old_name = self.paths[selection[0]].name if selection and selection[0] in self.paths else None
        try:
            files = self.app.model.files(self.section_id)
        except (OSError, OrganizerError):
            self.note.configure(text="文件夹不可用，请从菜单检查位置", fg="#b02d36")
            self.tree.delete(*self.tree.get_children())
            self.paths.clear()
            return
        self.note.configure(text=f"{len(files)} 项 · 双击打开 · 右键分类或改名", fg="#657089")
        names = [(p.name, p.is_dir()) for p in files]
        existing = [(p.name, p.is_dir()) for p in self.paths.values()]
        if names == existing:
            return
        self.tree.delete(*self.tree.get_children())
        self.paths.clear()
        for index, path in enumerate(files):
            item = str(index)
            kind = "文件夹" if path.is_dir() else path.suffix.lstrip(".").upper() or "文件"
            self.tree.insert("", "end", iid=item, text=path.name, values=(kind,))
            self.paths[item] = path
            if path.name == old_name:
                self.tree.selection_set(item)

    def selected(self):
        selection = self.tree.selection()
        return self.paths.get(selection[0]) if selection else None

    def open_selected(self):
        path = self.selected()
        if path:
            self.app.action(lambda: open_path(path))

    def add_files(self):
        files = filedialog.askopenfilenames(parent=self.window, title="选择要归类的文件（可多选）",
                                           initialdir=str(self.app.model.desktop))
        if not files:
            return
        destination = self.app.model.folder(self.section_id)
        preview = "\n".join(Path(p).name for p in files[:8])
        if len(files) > 8:
            preview += f"\n……共 {len(files)} 个文件"
        if not messagebox.askyesno("确认移动文件", f"把以下文件移动到「{self.section['name']}」？"
                                    f"\n\n{preview}\n\n目标：{destination}"
                                    "\n这是移动，不是复制。可以在本次打开期间撤销。",
                                    parent=self.window):
            return
        successful = 0
        errors = []
        for name in files:
            try:
                self.app.model.classify(Path(name), self.section_id)
                successful += 1
            except (OSError, OrganizerError) as exc:
                errors.append(f"{Path(name).name}：{exc}")
        self.app.refresh_all()
        if errors:
            messagebox.showwarning("整理结果", f"已移动 {successful} 个文件。"
                                   "\n以下文件未移动：\n" + "\n".join(errors), parent=self.window)

    def rename_file(self):
        path = self.selected()
        if not path:
            return
        if path.is_dir():
            messagebox.showinfo("文件夹", "第一版只支持文件改名。文件夹请在资源管理器中改名。",
                                parent=self.window)
            return
        name = simpledialog.askstring("重命名文件", "输入新名称（请保留 .txt 等扩展名）：",
                                      initialvalue=path.name, parent=self.window)
        if name and name != path.name and messagebox.askyesno(
                "确认改名", f"把「{path.name}」改为「{name}」？", parent=self.window):
            if self.app.action(lambda: self.app.model.rename_file(self.section_id, path.name, name)):
                self.app.refresh_all()

    def classify_selected(self, section_id):
        path = self.selected()
        if path and messagebox.askyesno("确认分类", f"把「{path.name}」移动到"
                f"「{self.app.model.section(section_id)['name']}」？", parent=self.window):
            if self.app.action(lambda: self.app.model.classify(path, section_id)):
                self.app.refresh_all()

    def file_menu(self, event):
        row = self.tree.identify_row(event.y)
        if not row:
            return
        self.tree.selection_set(row)
        menu = tk.Menu(self.window, tearoff=False)
        menu.add_command(label="打开", command=self.open_selected)
        menu.add_command(label="在资源管理器中找到它",
                         command=lambda: self.app.action(lambda: open_path(self.selected(), True)))
        if self.selected().is_file():
            menu.add_separator()
            menu.add_command(label="重命名…", command=self.rename_file)
            targets = tk.Menu(menu, tearoff=False)
            for section in self.app.model.sections:
                if section["id"] != self.section_id:
                    targets.add_command(label=section["name"],
                                        command=lambda sid=section["id"]: self.classify_selected(sid))
            if targets.index("end") is not None:
                menu.add_cascade(label="移动到分区", menu=targets)
        try:
            menu.tk_popup(event.x_root, event.y_root)
        finally:
            menu.grab_release()

    def panel_menu(self):
        menu = tk.Menu(self.window, tearoff=False)
        menu.add_command(label="重命名分区…", command=lambda: self.app.rename_section(self.section_id))
        menu.add_command(label="打开分区文件夹", command=lambda: self.app.action(
            lambda: open_path(self.app.model.folder(self.section_id))))
        menu.add_command(label="撤销上次操作", command=self.app.undo)
        menu.add_separator()
        menu.add_command(label="新增分区…", command=self.app.add_section)
        menu.add_command(label="打开管理窗口", command=self.app.show_manager)
        menu.add_command(label="隐藏这个面板（不删除文件）", command=self.hide)
        try:
            menu.tk_popup(self.window.winfo_pointerx(), self.window.winfo_pointery())
        finally:
            menu.grab_release()


class App:
    def __init__(self, root: tk.Tk, model: Organizer):
        self.root, self.model = root, model
        self.panels: dict[str, Panel] = {}
        root.title("桌面分区 · 管理")
        root.configure(bg=BG)
        root.geometry("660x480")
        root.minsize(580, 430)
        root.protocol("WM_DELETE_WINDOW", self.close)
        font = ("Microsoft YaHei UI", 10)
        root.option_add("*Font", font)
        style = ttk.Style(root)
        style.theme_use("clam")
        style.configure("Treeview", font=font, rowheight=30, background="white", fieldbackground="white")
        style.configure("Treeview.Heading", font=font)
        container = tk.Frame(root, bg=BG, padx=22, pady=20)
        container.pack(fill="both", expand=True)
        tk.Label(container, text="让桌面各归其位", bg=BG, fg=INK,
                 font=("Microsoft YaHei UI", 20, "bold")).pack(anchor="w")
        tk.Label(container, text="创建分区 → 添加文件 → 随时折叠或展开", bg=BG,
                 fg="#657089").pack(anchor="w", pady=(6, 12))
        self.location = tk.Label(container, text=f"桌面目录：{model.desktop}", anchor="w",
                                 bg=BG, fg="#657089", wraplength=600)
        self.location.pack(fill="x")
        toolbar = tk.Frame(container, bg=BG)
        toolbar.pack(fill="x", pady=14)
        ttk.Button(toolbar, text="＋ 新增分区", command=self.add_section).pack(side="left")
        ttk.Button(toolbar, text="显示所有分区", command=self.show_all).pack(side="left", padx=8)
        self.undo_button = ttk.Button(toolbar, text="撤销上次操作", command=self.undo)
        self.undo_button.pack(side="left")
        self.regions = ttk.Treeview(container, columns=("state",), show="tree headings", height=5)
        self.regions.heading("#0", text="分区名称（双击显示面板）")
        self.regions.heading("state", text="状态")
        self.regions.column("#0", width=400)
        self.regions.column("state", width=100)
        self.regions.pack(fill="both", expand=True)
        self.regions.bind("<Double-1>", self.show_selected)
        bottom = tk.Frame(container, bg=BG)
        bottom.pack(fill="x", pady=(12, 8))
        ttk.Button(bottom, text="重命名选中分区", command=self.rename_selected).pack(side="left")
        self.pin = tk.BooleanVar(value=False)
        ttk.Checkbutton(bottom, text="面板始终在最上层", variable=self.pin,
                        command=self.update_pin).pack(side="left", padx=8)
        ttk.Button(bottom, text="隐藏管理窗口", command=self.hide_manager).pack(side="right")
        tk.Label(container, text="拖动面板蓝色标题可调整位置。点 ▾ 折叠，点 ▸ 展开。\n"
                 "文件移动和改名会先请你确认；退出程序不会删除文件。",
                 bg=BG, fg="#657089", justify="left").pack(anchor="w")
        self.show_all()
        self.refresh_manager()
        root.after(3000, self.poll)

    def action(self, function):
        try:
            function()
            return True
        except (OSError, OrganizerError) as exc:
            messagebox.showerror("这一步没有完成", str(exc), parent=self.root)
            return False

    def add_section(self):
        if not self.model.desktop.is_dir():
            messagebox.showinfo("选择桌面目录", "没有找到桌面目录，请选择你要整理的文件夹。",
                                parent=self.root)
            selected = filedialog.askdirectory(parent=self.root, title="选择桌面目录", mustexist=True)
            if not selected:
                return
            if self.model.sections:
                messagebox.showerror("已有分区", "请先恢复原桌面目录，避免现有分区丢失关联。",
                                     parent=self.root)
                return
            self.model.desktop = Path(selected).resolve()
            self.location.configure(text=f"桌面目录：{self.model.desktop}")
        name = simpledialog.askstring("新增分区", "给分区起个名字，比如：工作、图片、待处理", parent=self.root)
        if not name:
            return
        if not messagebox.askyesno("确认新增分区", f"在桌面创建文件夹「{name}」，并显示对应面板？"
                                  "\n不会自动移动已有文件。", parent=self.root):
            return
        if self.action(lambda: self.model.add_section(name)):
            self.show_all()
            self.refresh_manager()

    def rename_section(self, section_id):
        section = self.model.section(section_id)
        name = simpledialog.askstring("重命名分区", "输入分区新名称：", initialvalue=section["name"],
                                      parent=self.root)
        if name and name != section["name"] and messagebox.askyesno("确认改名",
                f"把「{section['name']}」改为「{name}」？\n桌面上的对应文件夹也会改名。",
                parent=self.root):
            if self.action(lambda: self.model.rename_section(section_id, name)):
                self.refresh_all()

    def rename_selected(self):
        selection = self.regions.selection()
        if selection:
            self.rename_section(selection[0])

    def show_selected(self, event=None):
        selection = self.regions.selection()
        if selection:
            self.panels[selection[0]].window.deiconify()
            self.panels[selection[0]].window.lift()

    def show_manager(self):
        self.root.deiconify()
        self.root.lift()

    def hide_manager(self):
        if not self.panels:
            messagebox.showinfo("先创建分区", "先创建一个分区，再隐藏管理窗口。", parent=self.root)
            return
        self.show_all()
        self.root.withdraw()

    def show_all(self):
        for section in self.model.sections:
            if section["id"] not in self.panels:
                self.panels[section["id"]] = Panel(self, section)
            self.panels[section["id"]].window.deiconify()
        self.update_pin()

    def update_pin(self):
        for panel in self.panels.values():
            panel.window.attributes("-topmost", self.pin.get())

    def refresh_manager(self):
        selection = self.regions.selection()
        self.regions.delete(*self.regions.get_children())
        for section in self.model.sections:
            self.regions.insert("", "end", iid=section["id"], text=section["name"],
                                values=("已折叠" if section["collapsed"] else "已展开",))
        if selection and self.regions.exists(selection[0]):
            self.regions.selection_set(selection[0])
        self.undo_button.configure(state="normal" if self.model.history else "disabled")

    def refresh_all(self):
        for panel in self.panels.values():
            panel.render_layout()
            panel.refresh()
        self.refresh_manager()

    def undo(self):
        if not self.model.history:
            messagebox.showinfo("撤销", "本次打开期间还没有可以撤销的操作。", parent=self.root)
            return
        kind, source, target = self.model.history[-1]
        if messagebox.askyesno("确认撤销", f"将「{target.name}」恢复到：\n{source}\n"
                              "如果原位置已有同名文件，会停止撤销。", parent=self.root):
            if self.action(self.model.undo):
                self.refresh_all()

    def poll(self):
        for panel in self.panels.values():
            panel.refresh()
        self.root.after(3000, self.poll)

    def close(self):
        if messagebox.askyesno("退出桌面分区", "退出后面板会关闭，文件仍保留在桌面文件夹中。\n"
                              "本次撤销记录会清空。确定退出？", parent=self.root):
            self.root.destroy()


def main():
    mutex = None
    kernel = None
    if sys.platform == "win32":
        # Do not let two instances overwrite the same layout or move files concurrently.
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_wchar_p]
        kernel.CreateMutexW.restype = ctypes.c_void_p
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        mutex = kernel.CreateMutexW(None, False, "Local\\DesktopOrganizer_v1")
        error = ctypes.get_last_error()
        if not mutex:
            raise ctypes.WinError(error)
        if error == 183:  # ERROR_ALREADY_EXISTS
            kernel.CloseHandle(mutex)
            notice = tk.Tk()
            notice.withdraw()
            messagebox.showinfo("已经打开", "桌面分区已经在运行，请从现有面板的 ⋯ 菜单打开管理窗口。",
                                parent=notice)
            notice.destroy()
            return
        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except (AttributeError, OSError):
            pass
    root = tk.Tk()
    root.withdraw()
    try:
        model = Organizer(config_path(), desktop_path())
        App(root, model)
        root.deiconify()
        root.mainloop()
    except (OSError, OrganizerError) as exc:
        messagebox.showerror("无法启动", str(exc), parent=root)
        root.destroy()
    finally:
        if mutex:
            kernel.CloseHandle(mutex)


if __name__ == "__main__":
    main()
