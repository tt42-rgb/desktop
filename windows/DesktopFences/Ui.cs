using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DesktopFences;

public static class Ui
{
    public static Brush Color(byte a, byte r, byte g, byte b) => new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
    public static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 8) };
        button.Click += (_, _) => action();
        return button;
    }
    public static MenuItem Menu(string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        return item;
    }
    public static bool Confirm(string text) => MessageBox.Show(text, "桌面围栏 · 请确认",
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public static string? Prompt(string title, string text, string initial = "")
    {
        var window = new Window { Title = title, Width = 430, Height = 180,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false, Topmost = true, FontFamily = new FontFamily("Microsoft YaHei UI") };
        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 10) });
        var input = new TextBox { Text = initial, Padding = new Thickness(6) };
        body.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Button("取消", () => window.DialogResult = false));
        var ok = Button("确定", () => window.DialogResult = true); ok.IsDefault = true;
        buttons.Children.Add(ok); body.Children.Add(buttons); window.Content = body;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text : null;
    }
    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}

public sealed class Controller : IDisposable
{
    public Organizer Model { get; }
    public Dictionary<string, FenceWindow> Windows { get; } = new();
    private readonly HashSet<string> hidden = new();
    private bool allHidden;
    private bool disposed;
    private Window? manager;
    private StackPanel? sectionList;
    private TextBlock? status;
    private Forms.NotifyIcon? tray;
    private readonly DispatcherTimer timer;

    public Controller(Organizer model)
    {
        Model = model;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "桌面围栏", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开管理窗口", null, (_, _) => ShowManager());
        menu.Items.Add("新增分区", null, (_, _) => AddSection());
        menu.Items.Add("显示全部分区", null, (_, _) => ShowAll());
        menu.Items.Add("隐藏全部分区", null, (_, _) => HideAll());
        menu.Items.Add("退出（保留文件）", null, (_, _) => Exit());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowManager();
        ShowAll(); ShowManager(); timer.Start();
    }

    public bool Act(Action action)
    {
        try { action(); return true; }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException
            || e is System.ComponentModel.Win32Exception || e is System.Text.Json.JsonException)
        {
            MessageBox.Show("这一步没有完成：\n" + e.Message, "桌面围栏", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    public FenceWindow ShowSection(Section section, bool attach = true)
    {
        hidden.Remove(section.Id);
        if (!Windows.TryGetValue(section.Id, out var window) || !DesktopHost.IsWindow(window.Handle))
        {
            window = new FenceWindow(this, section, attach);
            Windows[section.Id] = window;
        }
        window.Show(); window.ApplyLayout(); window.RefreshItems();
        return window;
    }

    public void ShowAll()
    {
        allHidden = false; hidden.Clear();
        foreach (var section in Model.Settings.Sections) ShowSection(section);
        RefreshManager();
    }
    public void HideAll()
    {
        allHidden = true;
        foreach (var window in Windows.Values) window.Hide();
        RefreshManager();
    }
    public void HideSection(Section section)
    {
        hidden.Add(section.Id);
        if (Windows.TryGetValue(section.Id, out var window)) window.Hide();
    }
    public void Refresh()
    {
        if (disposed) return;
        foreach (var section in Model.Settings.Sections.ToArray())
        {
            if (allHidden || hidden.Contains(section.Id)) continue;
            var window = ShowSection(section);
            window.TryAttach();
        }
    }

    public void AddSection(string? existingFolder = null)
    {
        var name = Ui.Prompt(existingFolder == null ? "新增桌面分区" : "使用已有文件夹",
            "分区名称，例如：项目、文档、应用", existingFolder == null ? "" : Path.GetFileName(existingFolder));
        if (name == null) return;
        if (existingFolder == null && !Ui.Confirm($"在桌面创建「{name}」文件夹和图标分区？\n不会自动整理现有图标。")) return;
        Section? section = null;
        if (Act(() => section = Model.Add(name, existingFolder)))
        {
            allHidden = false; ShowSection(section!); RefreshManager();
        }
    }
    public void AdoptFolder()
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择一个已有文件夹，显示为桌面分区。不会移动里面的文件。",
            InitialDirectory = Model.Desktop, UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) AddSection(dialog.SelectedPath);
    }
    public void RenameSection(Section section)
    {
        var name = Ui.Prompt("重命名分区", "输入新分区名称（只修改标题，文件夹位置不变）：", section.Name);
        if (name != null && Act(() => Model.RenameSection(section, name)))
        {
            if (Windows.TryGetValue(section.Id, out var window)) window.ApplyLayout();
            RefreshManager();
        }
    }
    public void RenameItem(string path)
    {
        var name = Ui.Prompt("重命名文件或文件夹", "新名称（文件请保留扩展名）：", Path.GetFileName(path));
        if (name == null || name == Path.GetFileName(path)) return;
        if (Ui.Confirm($"把「{Path.GetFileName(path)}」改为「{name}」？")
            && Act(() => Model.RenameItem(path, name))) RefreshItems();
    }
    public void AddFiles(Section section)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true,
            Title = "选择文件或软件快捷方式；文件夹可以直接拖入", InitialDirectory = Model.Desktop };
        if (dialog.ShowDialog() == true) Drop(dialog.FileNames, section);
    }

    public void Drop(string[] paths, Section section)
    {
        paths = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !string.Equals(Path.GetDirectoryName(p), Path.GetFullPath(section.Folder), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (paths.Length == 0) return;
        var preview = string.Join("\n", paths.Take(6).Select(Path.GetFileName));
        if (paths.Length > 6) preview += $"\n……共 {paths.Length} 项";
        if (!Ui.Confirm($"把以下项目移动到「{section.Name}」？\n\n{preview}\n\n"
            + $"目标：{section.Folder}\n文件、文件夹、快捷方式会从原位置移入。可以在本次打开期间撤销。")) return;
        var errors = new List<string>(); int moved = 0;
        foreach (var source in paths)
        {
            try { Model.Classify(source, section); moved++; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            { errors.Add(Path.GetFileName(source) + "：" + e.Message); }
        }
        RefreshItems();
        if (errors.Count > 0) MessageBox.Show($"已移动 {moved} 项。以下项目未移动：\n" + string.Join("\n", errors), "整理结果");
    }
    public void Undo()
    {
        if (Model.History.Count == 0) { MessageBox.Show("本次打开期间还没有可撤销的操作。", "撤销"); return; }
        var entry = Model.History.Peek();
        if (Ui.Confirm($"把「{Path.GetFileName(entry.Destination)}」恢复到：\n{entry.Source}\n同名项目不会被覆盖。")
            && Act(Model.Undo)) RefreshItems();
    }
    public void RefreshItems()
    {
        foreach (var window in Windows.Values) window.RefreshItems(force: true);
        RefreshManager();
    }
    public ContextMenu SectionMenu(Section section)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Menu("＋ 添加文件", () => AddFiles(section)));
        menu.Items.Add(Ui.Menu(section.Collapsed ? "展开分区" : "折叠分区", () => Windows[section.Id].Toggle()));
        menu.Items.Add(Ui.Menu("重命名分区", () => RenameSection(section)));
        menu.Items.Add(Ui.Menu("打开对应文件夹", () => Act(() => Ui.Open(section.Folder))));
        menu.Items.Add(Ui.Menu("撤销上次操作", Undo));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Menu("＋ 新增分区", () => AddSection()));
        menu.Items.Add(Ui.Menu("打开管理窗口", ShowManager));
        menu.Items.Add(Ui.Menu("隐藏所有分区", HideAll));
        menu.Items.Add(Ui.Menu("隐藏这个分区（保留文件）", () => HideSection(section)));
        return menu;
    }
    public ContextMenu ItemMenu(string path, Section section)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Menu("打开", () => Act(() => Ui.Open(path))));
        menu.Items.Add(Ui.Menu("重命名", () => RenameItem(path)));
        menu.Items.Add(Ui.Menu("在资源管理器中找到", () => Act(() => Process.Start(
            new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }))));
        var targets = new MenuItem { Header = "移动到分区" };
        foreach (var target in Model.Settings.Sections.Where(s => s != section))
            targets.Items.Add(Ui.Menu(target.Name, () => Drop(new[] { path }, target)));
        if (targets.Items.Count > 0) menu.Items.Add(targets);
        return menu;
    }

    public void ShowManager()
    {
        if (manager == null)
        {
            manager = new Window { Title = "桌面围栏 · 管理", Width = 650, Height = 510, MinWidth = 560, MinHeight = 430,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Ui.Color(255, 243, 245, 249),
                FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 13 };
            var body = new DockPanel { Margin = new Thickness(24) };
            var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top);
            top.Children.Add(new TextBlock { Text = "给桌面图标分个区", FontSize = 24, FontWeight = FontWeights.Bold });
            top.Children.Add(new TextBlock { Text = "图标直接拖入 · 双击打开 · 箭头折叠 · 标题拖动 · 右下角缩放",
                Margin = new Thickness(0, 8, 0, 18), TextWrapping = TextWrapping.Wrap });
            var buttons = new WrapPanel();
            buttons.Children.Add(Ui.Button("＋ 新增分区", () => AddSection()));
            buttons.Children.Add(Ui.Button("使用已有文件夹", AdoptFolder));
            buttons.Children.Add(Ui.Button("显示全部", ShowAll));
            buttons.Children.Add(Ui.Button("隐藏全部", HideAll));
            buttons.Children.Add(Ui.Button("撤销上次操作", Undo));
            top.Children.Add(buttons); body.Children.Add(top);
            var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom);
            status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 12, 0, 10) };
            bottom.Children.Add(status);
            var footer = new WrapPanel();
            footer.Children.Add(Ui.Button("隐藏管理窗口", () => manager.Hide()));
            footer.Children.Add(Ui.Button("退出（保留文件）", Exit));
            bottom.Children.Add(footer); body.Children.Add(bottom);
            sectionList = new StackPanel();
            body.Children.Add(new ScrollViewer { Content = sectionList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            manager.Content = body;
            manager.Closing += (_, e) => { if (!disposed) { e.Cancel = true; manager.Hide(); } };
        }
        RefreshManager(); manager.Show(); manager.Activate();
    }
    public void RefreshManager()
    {
        if (sectionList == null || status == null) return;
        sectionList.Children.Clear();
        foreach (var section in Model.Settings.Sections)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var rename = Ui.Button("改名", () => RenameSection(section)); DockPanel.SetDock(rename, Dock.Right);
            var show = Ui.Button("显示", () => { allHidden = false; ShowSection(section); }); DockPanel.SetDock(show, Dock.Right);
            row.Children.Add(rename); row.Children.Add(show);
            row.Children.Add(new TextBlock { Text = section.Name + (section.Collapsed ? "  · 已折叠" : ""),
                VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            sectionList.Children.Add(row);
        }
        if (Model.Settings.Sections.Count == 0)
            sectionList.Children.Add(new TextBlock { Text = "先新增“项目”“文档”“应用”等分区，再拖入图标。\n"
                + "旧版已整理的文件夹，可以用“使用已有文件夹”直接显示。", TextWrapping = TextWrapping.Wrap });
        bool detached = Windows.Values.Any(w => !w.Attached);
        status.Text = (detached ? "桌面服务未就绪：暂时使用普通面板，恢复后会自动贴入桌面。\n" : "")
            + "关闭管理窗口会留在右下角托盘。分区操作不会删除文件。\n文件移动、改名会先确认；撤销记录只在本次打开期间有效。";
    }
    public void Exit()
    {
        if (Ui.Confirm("退出桌面围栏？\n分区面板会关闭，文件仍保留在对应文件夹中。\n本次撤销记录会清空。"))
        { Dispose(); Application.Current.Shutdown(); }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop();
        tray?.Dispose();
        foreach (var window in Windows.Values.ToArray()) window.Close();
        Windows.Clear(); manager?.Close();
    }
}

public sealed class FenceWindow : Window
{
    private readonly Controller controller;
    public Section Section { get; }
    private readonly bool embed;
    private IntPtr parent;
    public IntPtr Handle { get; private set; }
    public bool Attached => parent != IntPtr.Zero && DesktopHost.IsWindow(parent);
    private readonly TextBlock title;
    private readonly Button arrow;
    private readonly WrapPanel icons;
    private readonly ScrollViewer scroll;
    private readonly Thumb resize;
    private string[] displayed = Array.Empty<string>();
    private DesktopHost.NativePoint dragStart;
    private double initialX, initialY;
    private bool dragging;
    public int ItemCount => icons.Children.Count;

    public FenceWindow(Controller controller, Section section, bool embed = true)
    {
        this.controller = controller; Section = section; this.embed = embed;
        Title = section.Name + " · 桌面围栏";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12;
        Width = section.Width; Height = section.Collapsed ? 42 : section.Height;
        var frame = new Border { CornerRadius = new CornerRadius(10),
            Background = Ui.Color(175, 24, 36, 44), BorderBrush = Ui.Color(50, 255, 255, 255), BorderThickness = new Thickness(1) };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Background = Ui.Color(140, 15, 24, 32) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        title = new TextBlock { Text = section.Name, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
            FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.SizeAll };
        header.Children.Add(title);
        var more = HeaderButton("⋯"); Grid.SetColumn(more, 1); header.Children.Add(more);
        more.Click += (_, _) => { more.ContextMenu = controller.SectionMenu(section); more.ContextMenu.IsOpen = true; };
        arrow = HeaderButton("⌃"); Grid.SetColumn(arrow, 2); header.Children.Add(arrow);
        arrow.Click += (_, _) => Toggle();
        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Toggle(); return; }
            dragging = true; dragStart = DesktopHost.Cursor(); initialX = section.X; initialY = section.Y;
            title.CaptureMouse(); e.Handled = true;
        };
        title.MouseMove += (_, _) =>
        {
            if (!dragging) return;
            var point = DesktopHost.Cursor(); section.X = initialX + point.X - dragStart.X; section.Y = initialY + point.Y - dragStart.Y;
            ApplyLayout();
        };
        title.MouseLeftButtonUp += (_, _) =>
        {
            if (!dragging) return;
            dragging = false; title.ReleaseMouseCapture(); controller.Act(controller.Model.Save);
        };
        header.ContextMenuOpening += (_, _) => header.ContextMenu = controller.SectionMenu(section);
        header.ContextMenu = controller.SectionMenu(section);
        layout.Children.Add(header);
        icons = new WrapPanel { Margin = new Thickness(6, 8, 2, 16), Orientation = Orientation.Horizontal };
        scroll = new ScrollViewer { Content = icons, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        resize = new Thumb { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE, Opacity = .65 };
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetValue(TextBlock.TextProperty, "◢"); factory.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        resize.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = factory };
        Grid.SetRow(resize, 1); layout.Children.Add(resize);
        resize.DragDelta += (_, e) =>
        {
            section.Width = Math.Clamp(section.Width + e.HorizontalChange, 220, 1200);
            section.Height = Math.Clamp(section.Height + e.VerticalChange, 180, 1200); ApplyLayout();
        };
        resize.DragCompleted += (_, _) => controller.Act(controller.Model.Save);
        frame.Child = layout; Content = frame;
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None;
            frame.BorderBrush = Ui.Color(220, 154, 222, 160); e.Handled = true;
        };
        DragLeave += (_, _) => frame.BorderBrush = Ui.Color(50, 255, 255, 255);
        Drop += (_, e) =>
        {
            frame.BorderBrush = Ui.Color(50, 255, 255, 255);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) controller.Drop(paths, section);
            e.Handled = true;
        };
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            TryAttach(); ApplyLayout();
        };
    }
    private static Button HeaderButton(string text)
    {
        var button = new Button { Content = text, FontSize = 17, Foreground = Brushes.White,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.AppendChild(content);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = factory };
        return button;
    }
    public void TryAttach()
    {
        if (!embed || Handle == IntPtr.Zero || Attached) return;
        var candidate = DesktopHost.FindDesktop();
        if (DesktopHost.Attach(Handle, candidate)) { parent = candidate; ApplyLayout(); }
    }
    public void ApplyLayout()
    {
        title.Text = Section.Name; Title = Section.Name + " · 桌面围栏";
        arrow.Content = Section.Collapsed ? "⌄" : "⌃";
        scroll.Visibility = Section.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        resize.Visibility = Section.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        Width = Section.Width; Height = Section.Collapsed ? 42 : Section.Height;
        if (Handle != IntPtr.Zero)
            DesktopHost.Position(this, Attached ? parent : IntPtr.Zero, Section.X, Section.Y, Width, Height);
    }
    public void Toggle()
    {
        Section.Collapsed = !Section.Collapsed; ApplyLayout(); controller.Act(controller.Model.Save); controller.RefreshManager();
    }
    public void RefreshItems(bool force = false)
    {
        string[] paths;
        try { paths = controller.Model.Items(Section); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            icons.Children.Clear(); displayed = Array.Empty<string>();
            icons.Children.Add(new TextBlock { Text = "文件夹暂时不可用\n可从菜单检查位置", Foreground = Brushes.White,
                Margin = new Thickness(8), ToolTip = e.Message }); return;
        }
        if (!force && paths.SequenceEqual(displayed)) return;
        displayed = paths; icons.Children.Clear();
        foreach (var path in paths) icons.Children.Add(Item(path));
        if (paths.Length == 0)
            icons.Children.Add(new TextBlock { Text = "把桌面图标拖到这里\n文件、文件夹、软件快捷方式都可以", Foreground = Ui.Color(190, 255, 255, 255),
                Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap, Width = 235 });
    }
    private Button Item(string path)
    {
        var contents = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        contents.Children.Add(new Image { Source = DesktopHost.Icon(path), Width = 40, Height = 40, Margin = new Thickness(0, 4, 0, 5) });
        string label = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
        contents.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 36, FontSize = 11 });
        var button = new Button { Content = contents, Width = 84, Height = 96, Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1), Padding = new Thickness(3),
            Margin = new Thickness(1), ToolTip = path, VerticalContentAlignment = VerticalAlignment.Top };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.MarginProperty, new Thickness(3)); border.AppendChild(content);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        button.MouseEnter += (_, _) => button.Background = Ui.Color(55, 255, 255, 255);
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseDoubleClick += (_, e) => { controller.Act(() => Ui.Open(path)); e.Handled = true; };
        button.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) controller.Act(() => Ui.Open(path));
            if (e.Key == Key.F2) controller.RenameItem(path);
        };
        button.ContextMenuOpening += (_, _) => button.ContextMenu = controller.ItemMenu(path, Section);
        button.ContextMenu = controller.ItemMenu(path, Section);
        Point start = new(); bool pressed = false;
        button.PreviewMouseLeftButtonDown += (_, e) => { start = e.GetPosition(button); pressed = e.ClickCount == 1; };
        button.PreviewMouseLeftButtonUp += (_, _) => pressed = false;
        button.PreviewMouseMove += (_, e) =>
        {
            var current = e.GetPosition(button);
            if (!pressed || e.LeftButton != MouseButtonState.Pressed
                || (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)) return;
            pressed = false;
            DragDrop.DoDragDrop(button, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Move);
            controller.RefreshItems();
        };
        return button;
    }
}
