using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using QuickNote.Core;

namespace QuickNote;

public partial class NoteWindow : Window
{
    private Note note;
    private readonly NotebookStore store;
    public Note? Result { get; private set; }
    internal Func<Note, bool>? SaveAction { get; set; }
    internal Func<MessageBoxResult>? CloseDecision { get; set; }
    internal bool HasUnsavedChanges => note.Kind == NoteKind.Text && Editor.Text != note.Text;
    private double zoom = 1;

    public NoteWindow(Note note, NotebookStore store)
    {
        this.note = note;
        this.store = store;
        InitializeComponent();
        Metadata.Text = note.CreatedAt.ToLocalTime().ToString("yyyy.MM.dd  HH:mm") + "  ·  仅保存在本机";
        Editor.Text = note.Text;
        if (note.Kind == NoteKind.Image && note.ImageFile is not null)
        {
            Editor.Visibility = Visibility.Collapsed;
            ImageScroll.Visibility = Visibility.Visible;
            FullImage.Source = NoteCard.LoadImage(store.ImagePath(note.ImageFile));
            FullImage.MaxWidth = 570;
            Heading.Text = note.Label;
            SaveButton.Visibility = Visibility.Collapsed;
            Status.Text = "Ctrl + 滚轮缩放 · Esc 关闭";
        }
        else Loaded += (_, _) => { Editor.Focus(); Editor.CaretIndex = Editor.Text.Length; };
        UpdateTitle();
        Editor.TextChanged += (_, _) =>
        {
            UpdateTitle();
            Status.Text = HasUnsavedChanges ? "尚未保存 · Ctrl+Enter 保存" : "Ctrl+Enter 保存 · Esc 关闭";
        };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        SaveChanges();
    }
    private bool SaveChanges()
    {
        if (string.IsNullOrWhiteSpace(Editor.Text)) { Status.Text = "先写一点内容吧"; return false; }
        var edited = note with { Text = Editor.Text };
        if (SaveAction?.Invoke(edited) != true)
        { Status.Text = "保存未完成，修改仍保留在这里。"; return false; }
        note = edited;
        Result = edited;
        UpdateTitle();
        Status.Text = "已保存 · 可以继续编辑或查看其他便签";
        return true;
    }
    private void UpdateTitle()
    {
        var text = note.Kind == NoteKind.Image ? note.Label : Editor.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length == 0) text = "新便签";
        if (text.Length > 24) text = text[..24] + "…";
        Title = (HasUnsavedChanges ? "* " : "") + text + " · QuickNote";
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!HasUnsavedChanges) return;
        var decision = CloseDecision?.Invoke() ?? MessageBox.Show(this, "保存这张便签的修改后关闭？", "便签尚未保存",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (decision is not (MessageBoxResult.Yes or MessageBoxResult.No) ||
            (decision == MessageBoxResult.Yes && !SaveChanges())) e.Cancel = true;
    }
    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            var current = note.Kind == NoteKind.Text ? note with { Text = Editor.Text } : note;
            await ClipboardService.WithRetry(() => { ClipboardService.Copy(current, store); return true; });
            Status.Text = "已复制，可以去粘贴了";
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or FileFormatException)
        { Status.Text = "复制失败，请稍后重试"; }
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && note.Kind == NoteKind.Text)
        { OnSave(this, new RoutedEventArgs()); e.Handled = true; }
    }
    private void OnImageWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        zoom = Math.Clamp(zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.2, 8);
        FullImage.MaxWidth = double.PositiveInfinity;
        FullImage.Width = 570 * zoom;
        e.Handled = true;
    }
}
