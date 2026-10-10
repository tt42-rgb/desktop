import errno
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from desktop_organizer.model import Organizer, OrganizerError, move_file, valid_name


class OrganizerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.desktop = self.base / "Desktop"
        self.desktop.mkdir()
        self.config = self.base / "settings" / "settings.json"
        self.model = Organizer(self.config, self.desktop)

    def section(self, name="工作"):
        return self.model.add_section(name)["id"]

    def file(self, name="笔记.txt", content=b"important bytes"):
        path = self.desktop / name
        path.write_bytes(content)
        return path

    def test_initialization_does_not_create_config_or_move_files(self):
        source = self.file()
        Organizer(self.config, self.desktop)
        self.assertTrue(source.is_file())
        self.assertFalse(self.config.exists())
        self.assertEqual(self.model.sections, [])

    def test_create_section_and_restore_saved_layout(self):
        sid = self.section()
        self.model.update_layout(sid, collapsed=True, x=120, y=80)
        restored = Organizer(self.config, self.base)
        self.assertEqual(restored.desktop, self.desktop)
        self.assertEqual(restored.section(sid), self.model.section(sid))
        self.assertTrue(restored.folder(sid).is_dir())

    def test_existing_desktop_folder_not_adopted_or_deleted(self):
        folder = self.desktop / "工作"
        folder.mkdir()
        (folder / "keep.txt").write_text("keep")
        with self.assertRaises(FileExistsError):
            self.section()
        self.assertEqual((folder / "keep.txt").read_text(), "keep")
        self.assertEqual(self.model.sections, [])

    def test_duplicate_names_rejected_case_insensitively(self):
        self.section("Work")
        with self.assertRaises(OrganizerError):
            self.section("work")

    def test_classify_and_undo_preserve_contents(self):
        sid = self.section()
        source = self.file()
        target = self.model.classify(source, sid)
        self.assertFalse(source.exists())
        self.assertEqual(target.read_bytes(), b"important bytes")
        self.model.undo()
        self.assertTrue(source.exists())
        self.assertFalse(target.exists())
        self.assertEqual(source.read_bytes(), b"important bytes")

    def test_move_between_regions(self):
        work, photos = self.section(), self.section("图片")
        source = self.model.classify(self.file(), work)
        target = self.model.classify(source, photos)
        self.assertFalse(source.exists())
        self.assertEqual(target.parent, self.model.folder(photos))
        self.model.undo()
        self.assertTrue(source.exists())

    def test_name_collision_keeps_both_files_and_no_history_entry(self):
        sid = self.section()
        source = self.file()
        target = self.model.folder(sid) / source.name
        target.write_bytes(b"existing")
        with self.assertRaises(OrganizerError):
            self.model.classify(source, sid)
        self.assertEqual(source.read_bytes(), b"important bytes")
        self.assertEqual(target.read_bytes(), b"existing")
        self.assertEqual(self.model.history, [])

    def test_rename_file_and_undo(self):
        sid = self.section()
        source = self.model.classify(self.file(), sid)
        target = self.model.rename_file(sid, source.name, "新笔记.txt")
        self.assertEqual(target.read_bytes(), b"important bytes")
        self.assertFalse(source.exists())
        self.model.undo()
        self.assertTrue(source.exists())
        self.model.undo()
        self.assertTrue((self.desktop / "笔记.txt").exists())

    def test_rename_collision_preserves_files(self):
        sid = self.section()
        source = self.model.classify(self.file(), sid)
        other = self.model.folder(sid) / "other.txt"
        other.write_bytes(b"other")
        with self.assertRaises(OrganizerError):
            self.model.rename_file(sid, source.name, other.name)
        self.assertEqual(other.read_bytes(), b"other")
        self.assertTrue(source.exists())

    def test_rename_section_with_files_then_undo_entire_history(self):
        sid = self.section()
        source = self.file()
        self.model.classify(source, sid)
        self.model.rename_section(sid, "资料")
        self.assertTrue((self.desktop / "资料" / source.name).is_file())
        self.assertEqual(Organizer(self.config, self.desktop).section(sid)["name"], "资料")
        self.model.undo()
        self.assertTrue((self.desktop / "工作" / source.name).is_file())
        self.model.undo()
        self.assertTrue(source.is_file())

    def test_rename_section_collision_preserves_existing_folder(self):
        sid = self.section()
        other = self.desktop / "other"
        other.mkdir()
        with self.assertRaises(OrganizerError):
            self.model.rename_section(sid, "other")
        self.assertTrue(other.is_dir())
        self.assertEqual(self.model.section(sid)["name"], "工作")

    def test_undo_collision_keeps_moved_file_and_history(self):
        sid = self.section()
        source = self.file()
        target = self.model.classify(source, sid)
        source.write_bytes(b"new occupant")
        with self.assertRaises(OrganizerError):
            self.model.undo()
        self.assertEqual(source.read_bytes(), b"new occupant")
        self.assertEqual(target.read_bytes(), b"important bytes")
        self.assertEqual(len(self.model.history), 1)

    def test_undo_without_history_is_explained(self):
        with self.assertRaises(OrganizerError):
            self.model.undo()

    def test_history_is_session_only(self):
        self.model.classify(self.file(), self.section())
        restored = Organizer(self.config, self.desktop)
        self.assertEqual(restored.history, [])

    def test_same_section_move_is_rejected(self):
        sid = self.section()
        target = self.model.classify(self.file(), sid)
        with self.assertRaises(OrganizerError):
            self.model.classify(target, sid)
        self.assertTrue(target.is_file())

    def test_missing_section_folder_is_not_silently_recreated(self):
        sid = self.section()
        self.model.folder(sid).rmdir()
        with self.assertRaises(OrganizerError):
            self.model.files(sid)

    def test_directories_are_not_moved_as_files(self):
        sid = self.section()
        source = self.desktop / "source"
        source.mkdir()
        with self.assertRaises(OrganizerError):
            self.model.classify(source, sid)
        self.assertTrue(source.is_dir())

    def test_symbolic_links_are_not_followed(self):
        sid = self.section()
        source = self.file()
        link = self.desktop / "link.txt"
        try:
            link.symlink_to(source)
        except OSError:
            self.skipTest("OS does not permit symlinks")
        with self.assertRaises(OrganizerError):
            self.model.classify(link, sid)
        self.assertTrue(source.exists())

    def test_invalid_windows_names_rejected(self):
        for name in (None, 42, "", "..", "CON", "nul.txt", "COM1.txt", "LPT9", "a/b", "a\\b",
                     "foo.", " foo", "foo ", "x:y", "a\x00b", "a" * 121):
            with self.subTest(name=name), self.assertRaises(OrganizerError):
                valid_name(name)
        self.assertEqual(valid_name("学习资料"), "学习资料")

    def test_invalid_names_cannot_escape_section_for_rename(self):
        sid = self.section()
        with self.assertRaises(OrganizerError):
            self.model.rename_file(sid, "../outside.txt", "new.txt")

    def test_corrupt_config_not_overwritten(self):
        self.config.parent.mkdir()
        for text in ("bad json", "[]", '{"version":1,"desktop":"x","sections":[null]}'):
            self.config.write_text(text)
            with self.assertRaises(OrganizerError):
                Organizer(self.config, self.desktop)
            self.assertEqual(self.config.read_text(), text)

    def test_failed_save_rolls_back_new_section(self):
        with patch.object(self.model, "save", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                self.section()
        self.assertFalse((self.desktop / "工作").exists())
        self.assertEqual(self.model.sections, [])

    def test_failed_save_rolls_back_section_rename(self):
        sid = self.section()
        with patch.object(self.model, "save", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                self.model.rename_section(sid, "资料")
        self.assertEqual(self.model.section(sid)["name"], "工作")
        self.assertTrue((self.desktop / "工作").is_dir())
        self.assertFalse((self.desktop / "资料").exists())

    def test_failed_save_restores_layout(self):
        sid = self.section()
        original = self.model.section(sid).copy()
        with patch.object(self.model, "save", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                self.model.update_layout(sid, collapsed=True)
        self.assertEqual(self.model.section(sid), original)

    def test_cross_volume_move_fallback_preserves_bytes(self):
        source = self.file()
        target = self.desktop / "moved.txt"
        with patch("desktop_organizer.model.os.link", side_effect=OSError(errno.EXDEV, "cross-device")):
            move_file(source, target)
        self.assertFalse(source.exists())
        self.assertEqual(target.read_bytes(), b"important bytes")

    def test_destination_race_never_overwrites(self):
        source = self.file()
        target = self.desktop / "moved.txt"

        def race(*args):
            target.write_bytes(b"someone else's file")
            raise FileExistsError(errno.EEXIST, "already exists")

        with patch("desktop_organizer.model.os.link", side_effect=race):
            with self.assertRaises(OrganizerError):
                move_file(source, target)
        self.assertEqual(target.read_bytes(), b"someone else's file")
        self.assertTrue(source.is_file())

    def test_copy_failure_leaves_original_and_cleans_own_partial_copy(self):
        source = self.file()
        target = self.desktop / "moved.txt"
        with patch("desktop_organizer.model.os.link", side_effect=OSError(errno.EXDEV, "cross-device")), \
                patch("desktop_organizer.model.shutil.copyfileobj", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                move_file(source, target)
        self.assertTrue(source.is_file())
        self.assertFalse(target.exists())

    def test_cannot_unlink_source_rolls_back_destination(self):
        source = self.file()
        target = self.desktop / "moved.txt"
        unlink = Path.unlink

        def fail_source(path, *args, **kwargs):
            if path == source:
                raise PermissionError("locked by another process")
            return unlink(path, *args, **kwargs)

        with patch.object(Path, "unlink", fail_source):
            with self.assertRaises(PermissionError):
                move_file(source, target)
        self.assertTrue(source.is_file())
        self.assertFalse(target.exists())


if __name__ == "__main__":
    unittest.main()
