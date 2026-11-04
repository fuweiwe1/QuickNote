using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickNote.Core;
using Forms = System.Windows.Forms;

namespace QuickNote;

public partial class MainWindow : Window
{
    private readonly NotebookStore store;
    private Notebook notebook;
    private readonly DispatcherTimer edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly EdgeDockState dock = new();
    private readonly bool automated;
    private Forms.NotifyIcon? tray;
    private HwndSource? source;
    private IntPtr handle;
    private bool exiting;
    private bool capturing;
    private bool ready;
    private bool positioning;
    private bool dockInitialized;
    private bool presented;
    private int transition;
    private DockBounds dockBounds;
    private double screenDpi = 1;
    private double arrangedWidth = -1;

    public MainWindow(NotebookStore store, Notebook notebook) : this(store, notebook, false) { }
    internal MainWindow(NotebookStore store, Notebook notebook, bool automated)
    {
        this.store = store;
        this.notebook = notebook;
        this.automated = automated;
        InitializeComponent();
        Root.Opacity = 0;
        ready = true;
        RenderNotes();
        edgeTimer.Tick += (_, _) => PollEdge();
        Loaded += (_, _) =>
        {
            if (dockInitialized) return;
            dockInitialized = true;
            ApplyScreen();
            Hide();
            Root.Opacity = 1;
            Slide.Y = -Height;
            if (!automated) edgeTimer.Start();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        handle = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(handle);
        source.AddHook(WindowMessage);
        var captureRegistered = NativeMethods.RegisterHotKey(handle, 2, 0x4003, 0x56);
        tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "QuickNote · 晾一会", Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("展开晾衣绳", null, (_, _) => Reveal());
        menu.Items.Add("夹上剪贴板内容", null, async (_, _) => await Capture());
        menu.Items.Add("新写一张便签", null, (_, _) => OpenBlank());
        var monitors = new Forms.ToolStripMenuItem("放到显示器");
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var item = new Forms.ToolStripMenuItem(screen.DeviceName + (screen.Primary ? "（主屏）" : ""));
            item.Click += (_, _) => SelectMonitor(screen.DeviceName);
            monitors.DropDownItems.Add(item);
        }
        menu.Items.Add(monitors);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("检查更新…", null, (_, _) => OpenUpdater());
        menu.Items.Add("打开本地数据目录", null, (_, _) => OpenDataFolder());
        menu.Items.Add("退出", null, (_, _) => Exit());
        tray.ContextMenuStrip = menu;
        menu.Opening += (_, _) => dock.Hold();
        menu.Closed += (_, _) => dock.Release(Environment.TickCount64);
        tray.DoubleClick += (_, _) => Reveal();
        if (!captureRegistered) NotifyError("收录快捷键被占用，仍可点击草稿本或使用托盘菜单。");
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == NativeMethods.ShowMessage) { Reveal(); handled = true; }
        if (message == NativeMethods.WmHotkey && wParam.ToInt32() == 2) { _ = Capture(); handled = true; }
        if (message == 0x0112 && (wParam.ToInt64() & 0xFFF0) == 0xF020) handled = true;
        if (message is 0x007E or 0x02E0) Dispatcher.BeginInvoke(ApplyScreen);
        return IntPtr.Zero;
    }

    private void Reveal()
    {
        dock.Reveal(Environment.TickCount64);
        PresentDock(true, restore: true);
    }

    private void ApplyScreen()
    {
        if (positioning || exiting || handle == IntPtr.Zero) return;
        positioning = true;
        try
        {
            var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == notebook.Preferences.MonitorDevice)
                ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
            screenDpi = Math.Max(96, NativeMethods.GetDpiForWindow(handle)) / 96.0;
            var desiredWidth = screen.Bounds.Width / screenDpi;
            if (Math.Abs(Width - desiredWidth) > 0.1) Width = desiredWidth;
            dockBounds = new DockBounds(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, Math.Ceiling(Height * screenDpi));
            if (!NativeMethods.SetWindowPos(handle, new IntPtr(-1), screen.Bounds.Left, screen.Bounds.Top,
                    screen.Bounds.Width, (int)dockBounds.Height, 0x0010)) throw new Win32Exception();
            ArrangeCards();
        }
        catch (Win32Exception ex) { NotifyError("暂时无法调整显示位置：" + ex.Message); }
        finally { positioning = false; }
    }

    private void PollEdge()
    {
        if (!NativeMethods.GetCursorPos(out var point)) return;
        AdvancePointer(point.X, point.Y, Environment.TickCount64);
    }

    private void AdvancePointer(double x, double y, long now)
    {
        var expanded = dock.Update(x, y, dockBounds, 3 * screenDpi, now);
        if (expanded != presented) PresentDock(expanded);
        else if (expanded && (!NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle)))
            PresentDock(true, restore: true);
    }

    private void PresentDock(bool expanded, bool restore = false)
    {
        if (expanded == presented && !restore) return;
        presented = expanded;
        var current = ++transition;
        if (expanded)
        {
            WindowState = WindowState.Normal;
            if (!IsVisible) Show();
            ApplyScreen();
            NativeMethods.ShowWindow(handle, 4); // Show without taking keyboard focus.
        }
        var destination = expanded ? 0 : -Height;
        if (!SystemParameters.ClientAreaAnimation)
        {
            Slide.BeginAnimation(TranslateTransform.YProperty, null);
            Slide.Y = destination;
            if (!expanded) Hide();
            return;
        }
        var animation = new DoubleAnimation(destination, TimeSpan.FromMilliseconds(expanded ? 260 : 220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        animation.Completed += (_, _) => { if (current == transition && !presented) Hide(); };
        Slide.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private void SelectMonitor(string device)
    {
        if (Commit(notebook with { Preferences = notebook.Preferences with { MonitorDevice = device } })) { ApplyScreen(); Reveal(); }
    }

    private void OnLineSizeChanged(object sender, SizeChangedEventArgs e) => ArrangeCards();
    private void ArrangeCards()
    {
        if (!ready || Rope is null) return;
        var width = Math.Max(1, ActualWidth > 0 ? ActualWidth : Width);
        ArrangeCardsAtWidth(width);
    }

    private void ArrangeCardsAtWidth(double width)
    {
        if (Math.Abs(arrangedWidth - width) < 0.1) return;
        arrangedWidth = width;
        Rope.Data = new PathGeometry(new[] { new PathFigure(new Point(0, 12),
            new PathSegment[] { new QuadraticBezierSegment(new Point(width / 2, 42), new Point(width, 12), true) }, false) });
        Canvas.SetLeft(RightAnchor, width - 7);
        Canvas.SetTop(NotebookHost, ClotheslineLayout.RopeY(78, width) - 6);
        var positions = ClotheslineLayout.Positions(width, notebook.Notes);
        var layers = notebook.Notes.Select((note, index) => (note, index)).OrderBy(item => item.note.MovedAt ?? DateTimeOffset.MinValue)
            .Select((item, rank) => (item.note.Id, rank)).ToDictionary(item => item.Id, item => item.rank);
        for (var i = 0; i < positions.Length; i++)
        {
            var card = (NoteCard)NotesPanel.Children[i];
            Canvas.SetLeft(card, positions[i]);
            Canvas.SetTop(card, ClotheslineLayout.RopeY(positions[i] + card.Width / 2, width) - 6);
            card.RestingZIndex = layers[card.Note.Id];
            Panel.SetZIndex(card, card.IsMouseOver ? int.MaxValue : card.RestingZIndex);
        }
    }

    private void RenderNotes(Guid? animate = null)
    {
        if (!ready) return;
        arrangedWidth = -1;
        NotesPanel.Children.Clear();
        foreach (var note in notebook.Notes)
        {
            var card = new NoteCard(note, store);
            card.CopyRequested += async c => await Copy(c.Note, c);
            card.DeleteRequested += Delete;
            card.OpenRequested += c => OpenEditor(c.Note);
            card.DragStarted += BeginCardDrag;
            card.DragMoved += MoveCardDrag;
            card.DragFinished += EndCardDrag;
            card.InteractionChanged += held => { if (held) dock.Hold(); else dock.Release(Environment.TickCount64); };
            NotesPanel.Children.Add(card);
            if (note.Id == animate) card.Loaded += (_, _) => card.AnimateIn();
        }
        ArrangeCards();
    }

    private bool Commit(Notebook next)
    {
        try { store.Save(next); notebook = next; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show("这次修改没有保存，便签保持原样。请检查磁盘空间和数据目录权限。\n\n" + ex.Message,
                "本地保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async void OnCapture(object sender, RoutedEventArgs e) => await Capture();
    private async Task Capture()
    {
        if (capturing) return;
        capturing = true;
        dock.Hold();
        NotebookButton.IsEnabled = false;
        try
        {
            var note = await ClipboardService.WithRetry(() => ClipboardService.Read(store, notebook.Notes.Count % 5));
            Reveal();
            if (note is null) { NotifyError("剪贴板里还没有文字或图片，先复制一下吧。"); return; }
            AddNote(note);
        }
        catch (Exception ex) when (IsContentError(ex)) { NotifyError("没能收录：" + FriendlyError(ex)); }
        finally { capturing = false; NotebookButton.IsEnabled = true; dock.Release(Environment.TickCount64); }
    }

    private bool AddNote(Note note)
    {
        var notes = notebook.Notes.ToList();
        notes.Insert(0, note);
        if (!Commit(notebook with { Notes = notes })) return false;
        RenderNotes(note.Id);
        return true;
    }

    private async Task Copy(Note note, NoteCard? card = null)
    {
        dock.Hold();
        try
        {
            await ClipboardService.WithRetry(() => { ClipboardService.Copy(note, store); return true; });
            if (card is not null) await card.ShowCopied();
        }
        catch (Exception ex) when (IsContentError(ex)) { NotifyError("复制失败：" + FriendlyError(ex)); }
        finally { dock.Release(Environment.TickCount64); }
    }

    private void Delete(NoteCard card)
    {
        var notes = notebook.Notes.Where(n => n.Id != card.Note.Id).ToList();
        if (notes.Count == notebook.Notes.Count || !Commit(notebook with { Notes = notes })) return;
        AnimateTear(card);
        RenderNotes();
    }

    private void AnimateTear(NoteCard card)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var position = card.TranslatePoint(new Point(), TearOverlay);
        var width = card.Width;
        var height = card.Height;
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(card);
        bitmap.Freeze();
        for (var half = 0; half < 2; half++)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(0, half == 0 ? 0 : height), true, true);
                context.LineTo(new Point(width, half == 0 ? 0 : height), true, false);
                for (var i = 9; i >= 0; i--)
                    context.LineTo(new Point(width * i / 9, height * 0.54 + (i % 2 == 0 ? -3 : 3)), true, false);
            }
            var rotate = new RotateTransform();
            var translate = new TranslateTransform();
            var group = new TransformGroup();
            group.Children.Add(rotate); group.Children.Add(translate);
            var image = new Image { Source = bitmap, Width = width, Height = height, Clip = geometry,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(position.X, position.Y, 0, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = group };
            TearOverlay.Children.Add(image);
            var duration = TimeSpan.FromMilliseconds(500);
            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(half == 0 ? -24 : 30, duration));
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(half == 0 ? 14 : 70, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(half == 0 ? -14 : 22, duration));
            var fade = new DoubleAnimation(1, 0, duration) { BeginTime = TimeSpan.FromMilliseconds(40) };
            fade.Completed += (_, _) => TearOverlay.Children.Remove(image);
            image.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void OpenEditor(Note note)
    {
        dock.Hold();
        try
        {
            var dialog = new NoteWindow(note, store) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is not { } result) return;
            var notes = notebook.Notes.Select(n => n.Id == result.Id ? result : n).ToList();
            if (Commit(notebook with { Notes = notes })) RenderNotes();
        }
        catch (Exception ex) when (IsContentError(ex)) { NotifyError("暂时无法打开：" + FriendlyError(ex)); }
        finally { dock.Release(Environment.TickCount64); }
    }

    private void OpenBlank()
    {
        Reveal();
        dock.Hold();
        try
        {
            var dialog = new NoteWindow(new Note { Color = notebook.Notes.Count % 5 }, store) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result is { } result) AddNote(result);
        }
        finally { dock.Release(Environment.TickCount64); }
    }

    private void NotifyError(string text) => tray?.ShowBalloonTip(5000, "晾一会", text, Forms.ToolTipIcon.Info);
    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("QuickNote/NoteId")) return;
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        foreach (var path in files.Where(ClipboardService.IsImage))
        {
            try { if (!AddNote(ClipboardService.FromFile(path, store, notebook.Notes.Count % 5))) break; }
            catch (Exception ex) when (IsContentError(ex)) { NotifyError("图片未收录：" + FriendlyError(ex)); return; }
        }
    }

    private void OnRopeMenu(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu();
        dock.Hold();
        menu.Closed += (_, _) => dock.Release(Environment.TickCount64);
        AddMenu(menu, "新写一张便签", OpenBlank);
        var monitors = new MenuItem { Header = "放到显示器" };
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var item = new MenuItem { Header = screen.DeviceName + (screen.Primary ? "（主屏）" : ""),
                IsChecked = screen.DeviceName == (notebook.Preferences.MonitorDevice ?? Forms.Screen.PrimaryScreen?.DeviceName) };
            item.Click += (_, _) => SelectMonitor(screen.DeviceName);
            monitors.Items.Add(item);
        }
        menu.Items.Add(monitors);
        AddMenu(menu, "检查更新…", OpenUpdater);
        AddMenu(menu, "打开本地数据目录", OpenDataFolder);
        menu.Items.Add(new Separator());
        AddMenu(menu, "退出 QuickNote", Exit);
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }
    private static void AddMenu(ContextMenu menu, string text, Action action)
    { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    private void OpenUpdater()
    {
        Reveal();
        dock.Hold();
        var restart = false;
        try { restart = new UpdateWindow { Owner = this }.ShowDialog() == true; }
        finally { dock.Release(Environment.TickCount64); }
        if (restart) Exit();
    }
    private void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(store.ImageDirectory)!) { UseShellExecute = true }); }
        catch (Win32Exception ex) { NotifyError("无法打开目录：" + ex.Message); }
    }
    private static string FriendlyError(Exception ex) => ex is COMException ? "剪贴板正忙，请稍后重试" : ex.Message;
    private static bool IsContentError(Exception ex) => ex is COMException or IOException or
        UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or FileFormatException;
    private void OnClosing(object? sender, CancelEventArgs e) { if (!exiting) e.Cancel = true; }
    private void OnClosed(object? sender, EventArgs e)
    {
        edgeTimer.Stop();
        floatingCard?.Close();
        NativeMethods.UnregisterHotKey(handle, 2);
        if (source is { IsDisposed: false }) source.RemoveHook(WindowMessage);
        tray?.Dispose();
    }
    private void Exit() { exiting = true; Close(); Application.Current.Shutdown(); }
}
