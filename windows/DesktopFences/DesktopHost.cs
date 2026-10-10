using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopFences;

public static class DesktopHost
{
    private delegate bool EnumProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetStyle(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetStyle(IntPtr handle, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr handle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellInfo info, uint size, uint flags);

    [StructLayout(LayoutKind.Sequential)] public struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellInfo
    {
        public IntPtr Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    public static IntPtr FindDesktop()
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            var view = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero) return true;
            result = view;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    public static bool Attach(IntPtr handle, IntPtr parent)
    {
        if (parent == IntPtr.Zero || !IsWindow(parent)) return false;
        int original = GetStyle(handle, -16);
        // Child of Explorer's desktop view: follows the wallpaper, not other apps.
        SetStyle(handle, -16, (original & ~unchecked((int)0x80000000)) | 0x40000000);
        SetParent(handle, parent);
        if (GetParent(handle) != parent)
        {
            SetStyle(handle, -16, original);
            return false;
        }
        return true;
    }

    public static NativePoint Cursor() { GetCursorPos(out var point); return point; }

    public static void Position(Window window, IntPtr parent, double x, double y, double width, double height)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var point = new NativePoint { X = (int)x, Y = (int)y };
        if (parent != IntPtr.Zero) ScreenToClient(parent, ref point);
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, (int)(width * scale), (int)(height * scale), 0x0010 | 0x0040);
    }

    public static ImageSource? Icon(string path)
    {
        var info = new ShellInfo();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellInfo>(), 0x100 | 0x8000) == IntPtr.Zero)
            return null; // SHGFI_ICON | SHGFI_ADDOVERLAYS
        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); }
    }
}
