"""Real Tk interaction checks. Require Windows or an available DISPLAY."""
import os
from pathlib import Path
import sys
import tempfile
import tkinter as tk
import unittest
from unittest.mock import patch

from desktop_organizer.app import App
from desktop_organizer.model import Organizer


@unittest.skipUnless(sys.platform == "win32" or os.environ.get("DISPLAY"),
                     "GUI tests require a display")
class GuiTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        base = Path(self.temp.name)
        desktop = base / "Desktop"
        desktop.mkdir()
        self.model = Organizer(base / "settings.json", desktop)
        self.sid = self.model.add_section("工作")["id"]
        self.root = tk.Tk()
        self.app = App(self.root, self.model)
        self.root.update()

    def tearDown(self):
        for callback in self.root.tk.splitlist(self.root.tk.call("after", "info")):
            self.root.after_cancel(callback)
        self.root.destroy()

    def test_collapse_expand_updates_visible_panel_and_persistent_state(self):
        panel = self.app.panels[self.sid]
        panel.collapse()
        self.root.update()
        self.assertEqual(panel.body.winfo_manager(), "")
        self.assertTrue(Organizer(self.model.config, self.model.desktop).section(self.sid)["collapsed"])
        panel.collapse()
        self.root.update()
        self.assertEqual(panel.body.winfo_manager(), "pack")
        self.assertFalse(self.model.section(self.sid)["collapsed"])

    def test_canceling_creation_creates_nothing(self):
        with patch("desktop_organizer.app.simpledialog.askstring", return_value="图片"), \
                patch("desktop_organizer.app.messagebox.askyesno", return_value=False):
            self.app.add_section()
        self.assertFalse((self.model.desktop / "图片").exists())
        self.assertEqual(len(self.model.sections), 1)

    def test_canceling_file_move_keeps_original(self):
        source = self.model.desktop / "test.txt"
        source.write_text("unchanged")
        with patch("desktop_organizer.app.filedialog.askopenfilenames", return_value=(str(source),)), \
                patch("desktop_organizer.app.messagebox.askyesno", return_value=False):
            self.app.panels[self.sid].add_files()
        self.assertEqual(source.read_text(), "unchanged")
        self.assertEqual(self.model.history, [])

    def test_file_picker_move_refresh_and_undo_through_ui(self):
        source = self.model.desktop / "test.txt"
        source.write_text("keep me")
        panel = self.app.panels[self.sid]
        with patch("desktop_organizer.app.filedialog.askopenfilenames", return_value=(str(source),)), \
                patch("desktop_organizer.app.messagebox.askyesno", return_value=True):
            panel.add_files()
            self.root.update()
            self.assertFalse(source.exists())
            self.assertEqual(len(panel.tree.get_children()), 1)
            self.assertEqual(str(self.app.undo_button["state"]), "normal")
            self.app.undo()
            self.root.update()
        self.assertEqual(source.read_text(), "keep me")
        self.assertEqual(len(panel.tree.get_children()), 0)
        self.assertEqual(str(self.app.undo_button["state"]), "disabled")

    def test_rename_region_refreshes_panel_title_and_folder(self):
        with patch("desktop_organizer.app.simpledialog.askstring", return_value="资料"), \
                patch("desktop_organizer.app.messagebox.askyesno", return_value=True):
            self.app.rename_section(self.sid)
        panel = self.app.panels[self.sid]
        self.assertEqual(panel.title["text"], "资料")
        self.assertTrue((self.model.desktop / "资料").is_dir())
        self.assertFalse((self.model.desktop / "工作").exists())

    def test_hiding_last_panel_reopens_manager(self):
        self.app.hide_manager()
        self.root.update()
        self.assertEqual(self.root.state(), "withdrawn")
        self.app.panels[self.sid].hide()
        self.root.update()
        self.assertEqual(self.root.state(), "normal")


if __name__ == "__main__":
    unittest.main()
