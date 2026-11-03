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
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer undoTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private Forms.NotifyIcon? tray;
    private HwndSource? source;
    private IntPtr handle;
    private bool exiting;
    private bool capturing;
    private bool folded;
    private bool ready;
    private (Note Note, int Index)? deleted;

    public MainWindow(NotebookStore store, Notebook notebook)
    {
        this.store = store;
        this.notebook = notebook;
        InitializeComponent();
        Width = Math.Min(1160, SystemParameters.WorkArea.Width - 24);
        Left = notebook.Preferences.Left ?? (SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2);
        Top = notebook.Preferences.Top ?? SystemParameters.WorkArea.Top + 28;
        ClampPosition();
        Topmost = notebook.Preferences.AlwaysOnTop;
        PinButton.Content = Topmost ? "置顶 ✓" : "置顶";
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        undoTimer.Tick += (_, _) => { undoTimer.Stop(); deleted = null; UndoButton.Visibility = Visibility.Collapsed; };
        PreviewKeyDown += OnWindowKeyDown;
        ready = true;
        RenderNotes();
        Loaded += (_, _) => ShowToast("先复制，再点草稿本 · Ctrl+Alt+空格 隐藏或呼出");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        handle = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(handle);
        source.AddHook(WindowMessage);
        // MOD_CONTROL | MOD_ALT | MOD_NOREPEAT
        var showRegistered = NativeMethods.RegisterHotKey(handle, 1, 0x4003, 0x20);
        var captureRegistered = NativeMethods.RegisterHotKey(handle, 2, 0x4003, 0x56);
        tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "QuickNote · 晾一会",
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示晾衣绳", null, (_, _) => Reveal());
        menu.Items.Add("夹上剪贴板内容", null, async (_, _) => await Capture());
        menu.Items.Add("新写一张便签", null, (_, _) => OpenBlank());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开本地数据目录", null, (_, _) => OpenDataFolder());
        menu.Items.Add("退出", null, (_, _) => Exit());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Reveal();
        if (!showRegistered || !captureRegistered)
            Dispatcher.BeginInvoke(() => ShowToast("部分快捷键被占用，仍可通过托盘和草稿本操作"));
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == NativeMethods.ShowMessage) { Reveal(); handled = true; }
        if (message == NativeMethods.WmHotkey)
        {
            if (wParam.ToInt32() == 1) { if (IsVisible) Hide(); else Reveal(); }
            if (wParam.ToInt32() == 2) _ = Capture();
            handled = true;
        }
        if (message == 0x007E) Dispatcher.BeginInvoke(ClampPosition); // Display configuration changed.
        return IntPtr.Zero;
    }

    private void Reveal()
    {
        Show();
        WindowState = WindowState.Normal;
        ClampPosition();
        Activate();
    }

    private void ClampPosition()
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var right = virtualLeft + SystemParameters.VirtualScreenWidth;
        var bottom = virtualTop + SystemParameters.VirtualScreenHeight;
        if (Left + Width < virtualLeft + 80 || Left > right - 80 || Top < virtualTop || Top > bottom - 80)
        {
            Left = SystemParameters.WorkArea.Left + Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2);
            Top = SystemParameters.WorkArea.Top + 28;
        }
    }

    private void RenderNotes(Guid? animate = null)
    {
        if (!ready) return;
        var offset = NotesScroll.HorizontalOffset;
        NotesPanel.Children.Clear();
        var query = SearchBox.Text.Trim();
        var matches = notebook.Notes.Where(n => query.Length == 0 ||
            n.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            n.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var note in matches)
        {
            var card = new NoteCard(note, store);
            card.CopyRequested += async c => await Copy(c.Note);
            card.DeleteRequested += Delete;
            card.OpenRequested += c => OpenEditor(c.Note);
            card.ReorderRequested += Reorder;
            NotesPanel.Children.Add(card);
            if (note.Id == animate) card.Loaded += (_, _) => card.AnimateIn();
        }
        CountLabel.Text = notebook.Notes.Count == 0 ? "  /  给灵感一个落脚点" : $"  /  {notebook.Notes.Count} 张便签";
        EmptyPanel.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = query.Length == 0 ? "让零散的东西，有处可放。" : "这根绳子上，还没找到。";
        EmptyDescription.Text = query.Length == 0 ? "复制一句话或一张图，点左边的草稿本。\n用时取下，用完撕掉。" : "换个关键词，或按 Esc 清空搜索。";
        NotesScroll.ScrollToHorizontalOffset(animate.HasValue ? 0 : offset);
    }

    // Assign state only after a durable save succeeds, so UI and disk cannot disagree.
    private bool Commit(Notebook next)
    {
        try { store.Save(next); notebook = next; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, "这次修改没有保存，便签保持原样。请检查磁盘空间和数据目录权限。\n\n" + ex.Message,
                "本地保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async void OnCapture(object sender, RoutedEventArgs e) => await Capture();
    private async Task Capture()
    {
        if (capturing) return;
        capturing = true;
        NotebookButton.IsEnabled = false;
        try
        {
            var note = await ClipboardService.WithRetry(() => ClipboardService.Read(store, notebook.Notes.Count % 5));
            Reveal();
            if (folded) SetFolded(false);
            if (note is null) { ShowToast("剪贴板里还没有文字或图片，先复制一下吧"); return; }
            if (AddNote(note)) ShowToast("夹好了，下次用时直接复制");
        }
        catch (Exception ex) when (IsContentError(ex))
        {
            Reveal();
            ShowToast("没能收录：" + FriendlyError(ex));
        }
        finally { capturing = false; NotebookButton.IsEnabled = true; }
    }

    private bool AddNote(Note note)
    {
        var notes = notebook.Notes.ToList();
        notes.Insert(0, note);
        if (!Commit(notebook with { Notes = notes })) return false;
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        RenderNotes(note.Id);
        return true;
    }

    private async Task Copy(Note note)
    {
        try
        {
            await ClipboardService.WithRetry(() => { ClipboardService.Copy(note, store); return true; });
            ShowToast(note.Kind == NoteKind.Image ? "图片已复制，去粘贴吧" : "文字已复制，去粘贴吧");
        }
        catch (Exception ex) when (IsContentError(ex))
        { ShowToast("复制失败：" + FriendlyError(ex)); }
    }

    private void Delete(NoteCard card)
    {
        var index = notebook.Notes.FindIndex(n => n.Id == card.Note.Id);
        if (index < 0) return;
        var notes = notebook.Notes.ToList();
        notes.RemoveAt(index);
        if (!Commit(notebook with { Notes = notes })) return;
        AnimateTear(card);
        deleted = (card.Note, index);
        undoTimer.Stop();
        undoTimer.Start();
        RenderNotes();
        ShowToast("已撕掉 · 8 秒内可撤销", undo: true);
    }

    private void AnimateTear(NoteCard card)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var position = card.TranslatePoint(new Point(), TearOverlay);
        var bitmap = new RenderTargetBitmap(190, 268, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(card);
        bitmap.Freeze();
        // Split the paper along a ragged seam; each half falls with a different rotation.
        var seam = new[] { new Point(0, 139), new Point(22, 133), new Point(40, 142), new Point(61, 135), new Point(81, 144), new Point(99, 136), new Point(121, 143), new Point(143, 134), new Point(164, 141), new Point(190, 134) };
        for (var half = 0; half < 2; half++)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(half == 0 ? new Point(0, 0) : new Point(0, 268), true, true);
                context.LineTo(new Point(190, half == 0 ? 0 : 268), true, false);
                for (var i = seam.Length - 1; i >= 0; i--) context.LineTo(seam[i], true, false);
            }
            var rotate = new RotateTransform();
            var translate = new TranslateTransform();
            var group = new TransformGroup();
            group.Children.Add(rotate);
            group.Children.Add(translate);
            var image = new Image { Source = bitmap, Width = 190, Height = 268, Clip = geometry,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(position.X, position.Y, 0, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = group };
            TearOverlay.Children.Add(image);
            var duration = TimeSpan.FromMilliseconds(500);
            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(half == 0 ? -30 : 38, duration));
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(half == 0 ? 14 : 88, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(half == 0 ? -14 : 22, duration));
            var fade = new DoubleAnimation(1, 0, duration) { BeginTime = TimeSpan.FromMilliseconds(40) };
            fade.Completed += (_, _) => TearOverlay.Children.Remove(image);
            image.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (deleted is not { } item) return;
        var notes = notebook.Notes.ToList();
        notes.Insert(Math.Min(item.Index, notes.Count), item.Note);
        if (!Commit(notebook with { Notes = notes })) return;
        deleted = null;
        undoTimer.Stop();
        RenderNotes(item.Note.Id);
        ShowToast("已重新夹回绳子上");
    }

    private void Reorder(Guid from, Guid to)
    {
        if (from == to) return;
        var notes = notebook.Notes.ToList();
        var fromIndex = notes.FindIndex(n => n.Id == from);
        var toIndex = notes.FindIndex(n => n.Id == to);
        if (fromIndex < 0 || toIndex < 0) return;
        var note = notes[fromIndex];
        notes.RemoveAt(fromIndex);
        notes.Insert(toIndex, note);
        if (Commit(notebook with { Notes = notes })) RenderNotes();
    }

    private void OpenEditor(Note note)
    {
        try
        {
            var dialog = new NoteWindow(note, store) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is not { } result) return;
            var notes = notebook.Notes.Select(n => n.Id == result.Id ? result : n).ToList();
            if (Commit(notebook with { Notes = notes })) { RenderNotes(); ShowToast("修改已保存"); }
        }
        catch (Exception ex) when (IsContentError(ex))
        { ShowToast("暂时无法打开：" + FriendlyError(ex)); }
    }

    private void OpenBlank()
    {
        Reveal();
        var dialog = new NoteWindow(new Note { Color = notebook.Notes.Count % 5 }, store) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
        {
            if (folded) SetFolded(false);
            if (AddNote(result)) ShowToast("新灵感已夹好");
        }
    }

    private void ShowToast(string text, bool undo = false)
    {
        ToastLabel.Text = text;
        Toast.Visibility = Visibility.Visible;
        UndoButton.Visibility = deleted.HasValue ? Visibility.Visible : Visibility.Collapsed;
        toastTimer.Stop();
        toastTimer.Interval = TimeSpan.FromSeconds(undo ? 8 : 4);
        toastTimer.Start();
    }

    private void OnMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        DragMove();
        ClampPosition();
        Commit(notebook with { Preferences = notebook.Preferences with { Left = Left, Top = Top } });
    }
    private void OnToggleTopmost(object sender, RoutedEventArgs e)
    {
        var next = !Topmost;
        if (!Commit(notebook with { Preferences = notebook.Preferences with { AlwaysOnTop = next } })) return;
        Topmost = next;
        PinButton.Content = next ? "置顶 ✓" : "置顶";
    }
    private void OnFold(object sender, RoutedEventArgs e) => SetFolded(!folded);
    private void SetFolded(bool value)
    {
        folded = value;
        LineArea.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        Height = value ? 108 : 426;
        FoldButton.Content = value ? "展开" : "收起";
    }
    private void OnHide(object sender, RoutedEventArgs e) => Hide();
    private void OnSearch(object sender, TextChangedEventArgs e) => RenderNotes();
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { SearchBox.Clear(); e.Handled = true; } }
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z && !SearchBox.IsKeyboardFocusWithin)
        { OnUndo(this, new RoutedEventArgs()); e.Handled = true; }
        if (e.Key == Key.Escape && !SearchBox.IsKeyboardFocusWithin) Hide();
    }
    private void OnNotesWheel(object sender, MouseWheelEventArgs e)
    { NotesScroll.ScrollToHorizontalOffset(NotesScroll.HorizontalOffset - e.Delta * 1.8); e.Handled = true; }
    private void OnScrollLeft(object sender, RoutedEventArgs e) => NotesScroll.ScrollToHorizontalOffset(NotesScroll.HorizontalOffset - 410);
    private void OnScrollRight(object sender, RoutedEventArgs e) => NotesScroll.ScrollToHorizontalOffset(NotesScroll.HorizontalOffset + 410);
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
        var added = 0;
        foreach (var path in files.Where(ClipboardService.IsImage))
        {
            try
            {
                if (AddNote(ClipboardService.FromFile(path, store, notebook.Notes.Count % 5))) added++;
                else break;
            }
            catch (Exception ex) when (IsContentError(ex))
            { ShowToast("图片未收录：" + FriendlyError(ex)); return; }
        }
        if (folded) SetFolded(false);
        ShowToast(added > 0 ? $"夹好了 {added} 张图片" : "请拖入 PNG、JPG、BMP 或其他可读取的图片");
    }

    private void OnMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        AddMenu(menu, "新写一张便签", OpenBlank);
        AddMenu(menu, "打开本地数据目录", OpenDataFolder);
        AddMenu(menu, "使用说明", () => MessageBox.Show(this,
            "先复制文字或图片，再点左侧草稿本。\n\n悬停放大，点“复制”取用；双击查看全文或编辑。\n拖动便签排序，滚轮横向翻看。\n点 × 撕掉，8 秒内可撤销（最近一张）。\n拖动左上角标题或绳子移动位置。\n\nCtrl+Alt+空格：隐藏 / 呼出\nCtrl+Alt+V：收录剪贴板\nCtrl+F：搜索  ·  Esc：清空搜索 / 隐藏\n\n全部内容只保存在本机。图片不依赖原文件。\n完整备份请复制整个 QuickNote 数据目录。", "晾一会 · 使用说明"));
        menu.Items.Add(new Separator());
        AddMenu(menu, "退出 QuickNote", Exit);
        menu.PlacementTarget = (Button)sender;
        menu.IsOpen = true;
    }
    private static void AddMenu(ContextMenu menu, string text, Action action)
    { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    private void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(store.ImageDirectory)!) { UseShellExecute = true }); }
        catch (Win32Exception ex) { ShowToast("无法打开目录：" + ex.Message); }
    }
    private static string FriendlyError(Exception ex) => ex is COMException ? "剪贴板正忙，请稍后重试" : ex.Message;
    private static bool IsContentError(Exception ex) => ex is COMException or IOException or
        UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or FileFormatException;
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!exiting) { e.Cancel = true; Hide(); }
    }
    private void Exit()
    {
        exiting = true;
        toastTimer.Stop();
        undoTimer.Stop();
        NativeMethods.UnregisterHotKey(handle, 1);
        NativeMethods.UnregisterHotKey(handle, 2);
        source?.RemoveHook(WindowMessage);
        tray?.Dispose();
        Close();
        Application.Current.Shutdown();
    }
}
