using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace DesktopFences;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test") return Smoke.Run(args[1]);
        using var mutex = new Mutex(true, @"Local\DesktopFences_v2", out bool created);
        if (!created)
        {
            MessageBox.Show("桌面围栏已经在运行。请双击任务栏右下角的围栏图标，打开管理窗口。", "桌面围栏");
            return 0;
        }
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Controller? controller = null;
        application.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show("这一步没有完成：\n" + e.Exception.Message, "桌面围栏");
            e.Handled = true;
        };
        try
        {
            var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DesktopFences", "settings.json");
            var model = new Organizer(config, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            controller = new Controller(model);
            controller.Start();
            application.Run();
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show("无法启动：\n" + e.Message + "\n原文件和设置不会被覆盖。", "桌面围栏");
            return 1;
        }
        finally { controller?.Dispose(); }
    }
}
