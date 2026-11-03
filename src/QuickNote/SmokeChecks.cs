using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickNote.Core;

namespace QuickNote;

// Explicit developer-only mode: uses a temporary notebook, never the user's data directory.
internal static class SmokeChecks
{
    internal static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        var temporary = Path.Combine(Path.GetTempPath(), "QuickNote.UI." + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new NotebookStore(temporary);
            var window = new MainWindow(store, new Notebook());
            Application.Current.MainWindow = window;
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await window.CheckInteractions(output);
            File.WriteAllText(Path.Combine(output, "passed.txt"),
                "PASS native WPF startup and resources\nPASS real clipboard text and bitmap capture\nPASS oversized text and corrupt image handling\nPASS persisted image\nPASS text and image copy\nPASS search\nPASS reorder\nPASS delete and undo\nPASS fold and reveal\nPASS populated and detail screenshots\n");
            window.CloseForSmoke();
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }

    internal static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static void Snapshot(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(105, 128, 117)), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
            context.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

public partial class MainWindow
{
    internal async Task CheckInteractions(string output)
    {
        SmokeChecks.Assert(Topmost && notebook.Notes.Count == 0, "Unexpected initial state");
        SmokeChecks.Snapshot(this, Path.Combine(output, "01-empty.png"));
        Clipboard.SetText("你好，周五下午可以安排沟通。\n\n我整理好资料后发你，有任何问题随时联系。", TextDataFormat.UnicodeText);
        await Capture();
        SmokeChecks.Assert(notebook.Notes.Count == 1, "Text capture failed");
        Clipboard.SetText("灵感，先放在这里。\n\n给灵感留一条晾衣绳：\n轻一点，慢一点，\n让琐碎也有自己的位置。", TextDataFormat.UnicodeText);
        await Capture();
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(219, 230, 221), Color.FromRgb(163, 188, 169), 90), null, new Rect(0, 0, 480, 320));
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(247, 230, 180)), null, new Point(350, 86), 37, 37);
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(119, 150, 125)), null, new Point(100, 370), 260, 210);
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(76, 112, 91)), null, new Point(410, 390), 310, 210);
        }
        var bitmap = new RenderTargetBitmap(480, 320, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        Clipboard.SetImage(bitmap);
        await Capture();
        SmokeChecks.Assert(notebook.Notes.Count == 3 && notebook.Notes[0].Kind == NoteKind.Image, "Image capture failed");
        SmokeChecks.Assert(File.Exists(store.ImagePath(notebook.Notes[0].ImageFile!)), "Image not saved");
        Clipboard.SetText(new string('x', NotebookStore.MaxTextLength + 1));
        await Capture();
        SmokeChecks.Assert(notebook.Notes.Count == 3, "Oversized text changed the notebook");
        var corruptImage = Path.Combine(Path.GetDirectoryName(store.ImageDirectory)!, "corrupt.png");
        File.WriteAllText(corruptImage, "This is not an image");
        Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { corruptImage });
        await Capture();
        SmokeChecks.Assert(notebook.Notes.Count == 3, "Corrupt image changed the notebook");
        await Copy(notebook.Notes[2]);
        SmokeChecks.Assert(Clipboard.GetText().StartsWith("你好"), "Text copy failed");
        await Copy(notebook.Notes[0]);
        SmokeChecks.Assert(Clipboard.ContainsImage(), "Bitmap copy failed");
        SearchBox.Text = "不存在的关键词";
        SmokeChecks.Assert(NotesPanel.Children.Count == 0 && EmptyPanel.IsVisible, "Search empty state failed");
        SearchBox.Text = "周五";
        SmokeChecks.Assert(NotesPanel.Children.Count == 1, "Text search failed");
        SearchBox.Clear();
        var imageId = notebook.Notes[0].Id;
        Reorder(imageId, notebook.Notes[2].Id);
        SmokeChecks.Assert(notebook.Notes[2].Id == imageId, "Reorder failed");
        Delete((NoteCard)NotesPanel.Children[1]);
        SmokeChecks.Assert(notebook.Notes.Count == 2, "Delete failed");
        OnUndo(this, new RoutedEventArgs());
        SmokeChecks.Assert(notebook.Notes.Count == 3 && store.Load().Notebook.Notes.Count == 3, "Undo did not persist");
        SetFolded(true);
        SmokeChecks.Assert(Height == 108, "Fold failed");
        SetFolded(false);
        Hide(); Reveal();
        await Task.Delay(800);
        Toast.Visibility = Visibility.Collapsed;
        SmokeChecks.Snapshot(this, Path.Combine(output, "02-clothesline.png"));
        var detail = new NoteWindow(notebook.Notes[0], store) { Owner = this };
        detail.Show();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        SmokeChecks.Snapshot(detail, Path.Combine(output, "03-detail.png"));
        detail.Close();
    }

    internal void CloseForSmoke()
    {
        exiting = true;
        toastTimer.Stop(); undoTimer.Stop();
        NativeMethods.UnregisterHotKey(handle, 1);
        NativeMethods.UnregisterHotKey(handle, 2);
        tray?.Dispose();
        Close();
    }
}
