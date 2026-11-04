using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickNote.Core;

namespace QuickNote;

internal static class SmokeChecks
{
    internal static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        var temporary = Path.Combine(Path.GetTempPath(), "QuickNote.UI." + Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            var store = new NotebookStore(temporary);
            window = new MainWindow(store, new Notebook(), automated: true);
            Application.Current.MainWindow = window;
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await window.CheckInteractions(output);
            File.WriteAllText(Path.Combine(output, "passed.txt"),
                "PASS native WPF startup and minimal canvas\nPASS edge reveal, delayed hide and minimize recovery\nPASS non-activating reveal and native topmost ordering\nPASS real clipboard text and bitmap capture\nPASS invalid content handling\nPASS persisted image\nPASS text and image copy\nPASS reorder and permanent delete\nPASS compact overlap layout at multiple widths\nPASS screenshots\n");
        }
        finally
        {
            window?.CloseForSmoke();
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
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
        SmokeChecks.Assert(Topmost && notebook.Notes.Count == 0 && !IsVisible, "Dock must start tucked away");
        var foreground = NativeMethods.GetForegroundWindow();
        var now = Environment.TickCount64;
        AdvancePointer(dockBounds.Left + 200, dockBounds.Top + 1, now);
        await Task.Delay(320);
        SmokeChecks.Assert(IsVisible && presented && Math.Abs(Slide.Y) < 0.1, "Top edge did not reveal the rope");
        SmokeChecks.Assert(NativeMethods.GetForegroundWindow() == foreground, "Hover reveal stole keyboard focus");
        SmokeChecks.Assert((NativeMethods.GetWindowLongPtr(handle, -20).ToInt64() & 0x8L) != 0, "Revealed dock is not topmost");
        AdvancePointer(dockBounds.Left + 200, dockBounds.Top + dockBounds.Height + 80, now + 349);
        SmokeChecks.Assert(presented, "Leave grace period was ignored");
        AdvancePointer(dockBounds.Left + 200, dockBounds.Top + dockBounds.Height + 80, now + 350);
        await Task.Delay(280);
        SmokeChecks.Assert(!IsVisible && !presented, "Leaving did not tuck the rope away");
        AdvancePointer(dockBounds.Left + 200, dockBounds.Top + 80, now + 700);
        SmokeChecks.Assert(!presented, "Invisible card area revealed the dock");
        AdvancePointer(dockBounds.Left + 200, dockBounds.Top + 1, now + 800);
        await Task.Delay(320);
        NativeMethods.ShowWindow(handle, 6);
        AdvancePointer(dockBounds.Left + 200, dockBounds.Top + 1, now + 1200);
        await Task.Delay(320);
        SmokeChecks.Assert(!NativeMethods.IsIconic(handle) && NativeMethods.IsWindowVisible(handle), "Edge reveal did not recover a minimized window");
        SmokeChecks.Snapshot(this, Path.Combine(output, "01-empty.png"));
        Clipboard.SetText("你好，周五下午可以安排沟通。\n\n我整理好资料后发你，有任何问题随时联系。", TextDataFormat.UnicodeText);
        await Capture();
        SmokeChecks.Assert(notebook.Notes.Count == 1, "Text capture failed");
        Clipboard.SetText("灵感，先放在这里。\n\n轻一点，慢一点，\n让琐碎也有自己的位置。", TextDataFormat.UnicodeText);
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
        var normal = new Window { Width = 500, Height = 260, ShowActivated = false, ShowInTaskbar = false,
            Content = new System.Windows.Controls.Border { Background = Brushes.SlateBlue } };
        normal.Show();
        try
        {
            var otherHandle = new WindowInteropHelper(normal).Handle;
            NativeMethods.SetWindowPos(otherHandle, IntPtr.Zero, (int)dockBounds.Left + 180, (int)dockBounds.Top + 60, 520, 260, 0x0010 | 0x0040);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var point = ((NoteCard)NotesPanel.Children[0]).PointToScreen(new Point(50, 80));
            var hit = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint { X = (int)point.X, Y = (int)point.Y });
            SmokeChecks.Assert(hit == handle, "A normal application window covered the revealed rope");
        }
        finally { normal.Close(); }
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
        var imageId = notebook.Notes[0].Id;
        Reorder(imageId, notebook.Notes[2].Id);
        SmokeChecks.Assert(notebook.Notes[2].Id == imageId, "Reorder failed");
        var removed = notebook.Notes[1].Id;
        Delete((NoteCard)NotesPanel.Children[1]);
        SmokeChecks.Assert(notebook.Notes.Count == 2 && store.Load().Notebook.Notes.All(n => n.Id != removed), "Delete did not persist");
        for (var i = 0; i < 16; i++)
            AddNote(new Note { Text = $"灵感 {i + 1}\n\n把一闪而过的想法，\n先轻轻夹在这里。", Color = i % 5 });
        var monitorWidth = Width;
        foreach (var width in new[] { 800.0, 1280, 1920, 2560 })
        {
            Width = width;
            UpdateLayout();
            ArrangeCards();
            var previous = -1.0;
            foreach (NoteCard card in NotesPanel.Children)
            {
                var x = Canvas.GetLeft(card);
                SmokeChecks.Assert(x >= previous && x + card.Width <= width - ClotheslineLayout.RightInset + 0.1, "Overlapping card left monitor bounds");
                previous = x;
            }
            SmokeChecks.Snapshot(this, Path.Combine(output, $"02-overlap-{width}.png"));
        }
        Width = monitorWidth;
        ApplyScreen();
        await Task.Delay(650);
        SmokeChecks.Snapshot(this, Path.Combine(output, "03-edge-clothesline.png"));
        var detail = new NoteWindow(notebook.Notes[0], store) { Owner = this };
        detail.Show();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        SmokeChecks.Snapshot(detail, Path.Combine(output, "04-detail.png"));
        detail.Close();
    }

    internal void CloseForSmoke() { exiting = true; Close(); }
}
