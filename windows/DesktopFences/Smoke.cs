using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopFences;

public static class Smoke
{
    public static int Run(string output)
    {
        var checks = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "fences-ui-" + Guid.NewGuid().ToString("N"));
        Controller? controller = null; Window? parent = null; string? error = null;
        bool desktopAvailable = false;
        try
        {
            checks.AddRange(ModelChecks.Run());
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var desktop = Path.Combine(root, "测试桌面"); Directory.CreateDirectory(desktop);
            var model = new Organizer(Path.Combine(root, "settings.json"), desktop);
            var section = model.Add("图标分区");
            var file = Path.Combine(section.Folder, "测试文件.txt"); File.WriteAllText(file, "文件图标");
            Directory.CreateDirectory(Path.Combine(section.Folder, "项目文件夹"));
            controller = new Controller(model);
            var panel = controller.ShowSection(section, attach: false);
            Pump();
            Require(panel.ItemCount == 2, "The icon grid did not show files and folders"); checks.Add("icon-grid");
            Require(DesktopHost.Icon(file) != null, "Windows shell icons failed"); checks.Add("real-shell-icons");
            panel.Toggle(); Pump(); Require(section.Collapsed && panel.Height == 42, "Collapse failed");
            panel.Toggle(); Pump(); Require(!section.Collapsed && panel.Height == section.Height, "Expand failed");
            checks.Add("collapse-expand");
            section.Width = 420; section.Height = 380; panel.ApplyLayout(); model.Save(); Pump();
            var restored = new Organizer(model.ConfigPath, desktop).Settings.Sections[0];
            Require(panel.Width == 420 && restored.Width == 420 && restored.Height == 380, "Resize persistence failed");
            checks.Add("resize-persistence");
            Require(panel.AllowDrop && new DataObject(DataFormats.FileDrop, new[] { file }).GetDataPresent(DataFormats.FileDrop),
                "Native file drop format was not enabled"); checks.Add("file-drop-format");
            // CI may not run Explorer. Test Win32 child hosting against a real
            // temporary WPF window, and report Explorer availability separately.
            parent = new Window { Width = 700, Height = 550, Content = new Grid(), ShowInTaskbar = false };
            parent.Show(); Pump(); var parentHandle = new WindowInteropHelper(parent).Handle;
            Require(DesktopHost.Attach(panel.Handle, parentHandle) && DesktopHost.GetParent(panel.Handle) == parentHandle,
                "Desktop-style child window hosting failed");
            checks.Add("native-child-hosting");
            desktopAvailable = DesktopHost.FindDesktop() != IntPtr.Zero;
        }
        catch (Exception e) { error = e.ToString(); }
        finally
        {
            controller?.Dispose(); parent?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        var result = new { ok = error == null, checks, explorerDesktopAvailable = desktopAvailable, error };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return error == null ? 0 : 1;
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Pump() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
        DispatcherPriority.Background, new Action(() => { }));
}
