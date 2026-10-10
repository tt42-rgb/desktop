using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopFences;

public sealed class Section
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public double X { get; set; } = 80;
    public double Y { get; set; } = 100;
    public double Width { get; set; } = 300;
    public double Height { get; set; } = 340;
    public bool Collapsed { get; set; }
}

public sealed class Settings
{
    public int Version { get; set; } = 2;
    public List<Section> Sections { get; set; } = new();
}

public sealed record UndoEntry(string Source, string Destination);

public sealed class Organizer
{
    public string ConfigPath { get; }
    public string Desktop { get; }
    public Settings Settings { get; private set; } = new();
    public Stack<UndoEntry> History { get; } = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public Organizer(string configPath, string desktop)
    {
        ConfigPath = Path.GetFullPath(configPath);
        Desktop = Path.GetFullPath(desktop);
        if (File.Exists(ConfigPath))
        {
            Settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath))
                ?? throw new IOException("设置文件为空。为保护原设置，程序没有覆盖它。");
            if (Settings.Version != 2 || Settings.Sections == null)
                throw new IOException("设置版本不兼容。请保留设置文件并联系开发者。");
            var ids = new HashSet<string>();
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Settings.Sections)
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Id) || !ids.Add(s.Id))
                    throw new IOException("设置中有无效或重复的分区。");
                ValidateName(s.Name);
                if (string.IsNullOrWhiteSpace(s.Folder) || !Path.IsPathFullyQualified(s.Folder)
                    || !folders.Add(Path.GetFullPath(s.Folder)))
                    throw new IOException("设置中有无效或重复的文件夹。");
                if (!double.IsFinite(s.X) || !double.IsFinite(s.Y)
                    || !double.IsFinite(s.Width) || !double.IsFinite(s.Height))
                    throw new IOException("分区位置无效。");
                s.Width = Math.Clamp(s.Width, 220, 1200);
                s.Height = Math.Clamp(s.Height, 180, 1200);
            }
        }
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.')
            || name.Length > 120 || name.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0
            || name.Any(c => c < 32))
            throw new IOException("名称不能为空，不能含有 <>:\"/\\|?*，也不能以空格或句点结尾。");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem)
            || Enumerable.Range(1, 9).Any(n => stem == "COM" + n || stem == "LPT" + n))
            throw new IOException("这是 Windows 保留名称，请换一个。");
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, Settings, Json);
                stream.Flush(true);
            }
            File.Move(temporary, ConfigPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Section Add(string name, string? existingFolder = null)
    {
        ValidateName(name);
        if (Settings.Sections.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("已有这个名称的分区，请换一个名字。");
        var folder = Path.GetFullPath(existingFolder ?? Path.Combine(Desktop, name));
        if (Settings.Sections.Any(s => s.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("这个文件夹已经对应一个分区。");
        bool created = existingFolder == null;
        if (created)
        {
            if (Directory.Exists(folder) || File.Exists(folder))
                throw new IOException("已有同名文件夹。可用“使用已有文件夹”把它显示成分区。");
            Directory.CreateDirectory(folder);
        }
        else if (!Directory.Exists(folder)) throw new IOException("选中的文件夹不存在。");
        RejectLink(folder);
        int i = Settings.Sections.Count;
        var section = new Section { Name = name, Folder = folder,
            X = 80 + i % 3 * 325, Y = 100 + i / 3 * 365 };
        Settings.Sections.Add(section);
        try { Save(); }
        catch
        {
            Settings.Sections.Remove(section);
            if (created && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            throw;
        }
        return section;
    }

    public void RenameSection(Section section, string name)
    {
        ValidateName(name);
        if (Settings.Sections.Any(s => s != section && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("已有这个名称的分区。");
        string old = section.Name;
        section.Name = name;
        try { Save(); } catch { section.Name = old; throw; }
    }

    public string[] Items(Section section)
    {
        RejectLink(section.Folder);
        return Directory.GetFileSystemEntries(section.Folder)
            .OrderBy(p => !Directory.Exists(p)).ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void RejectLink(string path)
    {
        var attributes = File.GetAttributes(path);
        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path) : new FileInfo(path);
        // OneDrive placeholders are also reparse points, but are ordinary user
        // files. Reject actual symbolic links/junctions, not every reparse tag.
        if (info.LinkTarget != null)
            throw new IOException("为避免误操作，暂不整理符号链接或联接目录。普通 .lnk 快捷方式可以整理。");
    }

    private static void Move(string source, string destination)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("项目已在目标分区；只改大小写时，请先换一个名称。");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("目标已存在同名项目，不会覆盖：" + Path.GetFileName(destination));
        RejectLink(source);
        if (Directory.Exists(source))
        {
            if (destination.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new IOException("不能把文件夹移动到它自己里面。");
            Directory.Move(source, destination); // No overwrite; cross-volume directories fail safely.
        }
        else File.Move(source, destination, false);
    }

    public string Classify(string source, Section target)
    {
        RejectLink(target.Folder);
        var destination = Path.Combine(target.Folder, Path.GetFileName(source));
        Move(source, destination);
        History.Push(new UndoEntry(Path.GetFullPath(source), destination));
        return destination;
    }

    public string RenameItem(string source, string name)
    {
        ValidateName(name);
        var destination = Path.Combine(Path.GetDirectoryName(source)!, name);
        Move(source, destination);
        History.Push(new UndoEntry(source, destination));
        return destination;
    }

    public void Undo()
    {
        if (History.Count == 0) throw new IOException("本次打开期间还没有可撤销的操作。");
        var entry = History.Peek();
        Move(entry.Destination, entry.Source);
        History.Pop();
    }
}
