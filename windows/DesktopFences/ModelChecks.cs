using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopFences;

public static class ModelChecks
{
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Refused(Action operation)
    {
        try { operation(); } catch (IOException) { return; }
        throw new InvalidOperationException("An unsafe operation was accepted");
    }
    public static List<string> Run()
    {
        var passed = new List<string>();
        void Test(string name, Action<Organizer, string> check)
        {
            var root = Path.Combine(Path.GetTempPath(), "fences-check-" + Guid.NewGuid().ToString("N"));
            var desktop = Path.Combine(root, "桌面"); Directory.CreateDirectory(desktop);
            try
            {
                var model = new Organizer(Path.Combine(root, "settings.json"), desktop);
                check(model, desktop); passed.Add(name);
            }
            finally { Directory.Delete(root, true); }
        }
        Test("startup-does-not-touch-files", (m, d) =>
        {
            File.WriteAllText(Path.Combine(d, "keep.txt"), "keep");
            var restored = new Organizer(m.ConfigPath, d);
            Require(!File.Exists(m.ConfigPath) && restored.Settings.Sections.Count == 0
                && File.ReadAllText(Path.Combine(d, "keep.txt")) == "keep", "Startup changed settings or files");
        });
        Test("save-and-restore-layout", (m, d) =>
        {
            var s = m.Add("项目"); s.Collapsed = true; s.Width = 425; s.Height = 380; s.X = 123; s.Y = 87; m.Save();
            var loaded = new Organizer(m.ConfigPath, d).Settings.Sections.Single();
            Require(loaded.Id == s.Id && loaded.Name == "项目" && loaded.Collapsed
                && loaded.Width == 425 && loaded.Height == 380 && loaded.X == 123 && loaded.Y == 87,
                "Layout was not restored");
            Require(Directory.Exists(loaded.Folder), "Folder was not restored");
        });
        Test("adopt-existing-folder-without-moving", (m, d) =>
        {
            var folder = Path.Combine(d, "原资料"); Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "test.txt"); File.WriteAllText(file, "keep");
            var section = m.Add("文档", folder);
            Require(m.Items(section).Single() == file && File.ReadAllText(file) == "keep", "Adoption moved content");
        });
        Test("duplicate-region-and-folder-rejected", (m, d) =>
        {
            var section = m.Add("Work");
            Refused(() => m.Add("work")); Refused(() => m.Add("Other", section.Folder));
        });
        Test("existing-folder-is-not-overwritten", (m, d) =>
        {
            var folder = Path.Combine(d, "项目"); Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "keep.txt"); File.WriteAllText(file, "keep");
            Refused(() => m.Add("项目")); Require(File.ReadAllText(file) == "keep", "Existing folder changed");
        });
        Test("file-move-and-undo", (m, d) =>
        {
            var s = m.Add("文档"); var source = Path.Combine(d, "笔记.txt"); File.WriteAllText(source, "bytes文件");
            var target = m.Classify(source, s);
            Require(!File.Exists(source) && File.ReadAllText(target) == "bytes文件", "Move lost file");
            m.Undo(); Require(File.ReadAllText(source) == "bytes文件" && !File.Exists(target), "Undo failed");
        });
        Test("folder-move-and-undo", (m, d) =>
        {
            var s = m.Add("项目"); var source = Path.Combine(d, "子文件夹"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "keep.txt"), "content");
            var target = m.Classify(source, s);
            Require(File.ReadAllText(Path.Combine(target, "keep.txt")) == "content", "Folder move lost contents");
            m.Undo(); Require(File.ReadAllText(Path.Combine(source, "keep.txt")) == "content", "Folder undo failed");
        });
        Test("shortcut-move-preserves-bytes", (m, d) =>
        {
            var s = m.Add("应用"); var source = Path.Combine(d, "测试.lnk");
            var bytes = new byte[] { 0, 10, 20, 255 }; File.WriteAllBytes(source, bytes);
            var target = m.Classify(source, s); Require(File.ReadAllBytes(target).SequenceEqual(bytes), "Shortcut changed");
        });
        Test("collisions-preserve-both-files", (m, d) =>
        {
            var s = m.Add("文档"); var source = Path.Combine(d, "same.txt"); File.WriteAllText(source, "original");
            var target = Path.Combine(s.Folder, "same.txt"); File.WriteAllText(target, "existing");
            Refused(() => m.Classify(source, s));
            Require(File.ReadAllText(source) == "original" && File.ReadAllText(target) == "existing"
                && m.History.Count == 0, "Collision changed files");
        });
        Test("rename-file-and-undo", (m, d) =>
        {
            var s = m.Add("文档"); var source = Path.Combine(s.Folder, "old.txt"); File.WriteAllText(source, "keep");
            var target = m.RenameItem(source, "新名称.txt"); Require(File.ReadAllText(target) == "keep", "Rename changed bytes");
            m.Undo(); Require(File.Exists(source) && !File.Exists(target), "Rename undo failed");
        });
        Test("rename-folder-and-undo", (m, d) =>
        {
            var source = Path.Combine(d, "before"); Directory.CreateDirectory(source);
            var target = m.RenameItem(source, "after"); Require(Directory.Exists(target), "Folder rename failed");
            m.Undo(); Require(Directory.Exists(source), "Folder rename undo failed");
        });
        Test("region-title-does-not-change-storage", (m, d) =>
        {
            var s = m.Add("项目"); var folder = s.Folder; m.RenameSection(s, "工作项目");
            Require(s.Name == "工作项目" && s.Folder == folder && Directory.Exists(folder), "Title changed folder location");
        });
        Test("undo-refuses-new-occupant", (m, d) =>
        {
            var s = m.Add("文档"); var source = Path.Combine(d, "test.txt"); File.WriteAllText(source, "first");
            var target = m.Classify(source, s); File.WriteAllText(source, "new");
            Refused(m.Undo); Require(File.ReadAllText(source) == "new" && File.ReadAllText(target) == "first"
                && m.History.Count == 1, "Undo overwrote a file");
        });
        Test("nested-folder-move-refused", (m, d) =>
        {
            var source = Path.Combine(d, "parent"); Directory.CreateDirectory(source);
            var child = Path.Combine(source, "child"); Directory.CreateDirectory(child);
            var s = m.Add("child", child); Refused(() => m.Classify(source, s));
            Require(Directory.Exists(source), "Nested move changed the source");
        });
        Test("windows-name-validation", (m, d) =>
        {
            foreach (var name in new[] { "", "..", "CON", "nul.txt", "COM1.txt", "bad/name", "trailing.", "a:b" })
                Refused(() => Organizer.ValidateName(name));
            Organizer.ValidateName("学习资料");
        });
        Test("corrupt-settings-kept", (m, d) =>
        {
            File.WriteAllText(m.ConfigPath, "bad json"); bool rejected = false;
            try { _ = new Organizer(m.ConfigPath, d); } catch (System.Text.Json.JsonException) { rejected = true; }
            Require(rejected && File.ReadAllText(m.ConfigPath) == "bad json", "Corrupt config was replaced");
        });
        Test("session-only-history", (m, d) =>
        {
            var s = m.Add("文档"); var source = Path.Combine(d, "test.txt"); File.WriteAllText(source, "keep"); m.Classify(source, s);
            Require(new Organizer(m.ConfigPath, d).History.Count == 0, "Old undo history persisted");
        });
        return passed;
    }
}
