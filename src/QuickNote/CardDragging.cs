using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickNote.Core;

namespace QuickNote;

public partial class MainWindow
{
    private Window? floatingCard;
    private NoteCard? draggedCard;
    private Point grabOffset;
    private IntPtr floatingHandle;

    private void BeginCardDrag(NoteCard card, Point offset)
    {
        draggedCard = card;
        grabOffset = offset;
        card.PrepareDrag();
        var dpi = VisualTreeHelper.GetDpi(card);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(card.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(card.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(card); bitmap.Freeze();
        floatingCard = new Window { Width = card.Width, Height = card.Height, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, Owner = this,
            IsHitTestVisible = false, Content = new Image { Source = bitmap, Stretch = Stretch.Fill } };
        floatingCard.SourceInitialized += (_, _) =>
        {
            floatingHandle = new WindowInteropHelper(floatingCard).Handle;
            var style = NativeMethods.GetWindowLongPtr(floatingHandle, -20).ToInt64();
            NativeMethods.SetWindowLongPtr(floatingHandle, -20, new IntPtr(style | 0x08000020L));
        };
        floatingCard.Show();
        card.Opacity = 0.18;
        MoveCardDrag(card);
    }

    private void MoveCardDrag(NoteCard card)
    {
        if (draggedCard != card || floatingCard is null || !NativeMethods.GetCursorPos(out var point)) return;
        MoveFloatingCard(point);
    }

    private void MoveFloatingCard(NativeMethods.NativePoint point)
    {
        var dpi = Math.Max(96, NativeMethods.GetDpiForWindow(floatingHandle)) / 96.0;
        NativeMethods.SetWindowPos(floatingHandle, new IntPtr(-1), (int)Math.Round(point.X - grabOffset.X * dpi),
            (int)Math.Round(point.Y - grabOffset.Y * dpi), (int)Math.Ceiling(ClotheslineLayout.CardWidth * dpi),
            (int)Math.Ceiling(ClotheslineLayout.CardHeight * dpi), 0x0010 | 0x0040);
    }

    private void EndCardDrag(NoteCard card, bool commit)
    {
        if (draggedCard != card) return;
        floatingCard?.Close(); floatingCard = null; draggedCard = null;
        card.Opacity = 1;
        if (commit && NativeMethods.GetCursorPos(out var point))
        {
            var left = (point.X - dockBounds.Left) / screenDpi - grabOffset.X;
            SaveCardPosition(card.Note.Id, left);
        }
        else RenderNotes();
    }

    private void SaveCardPosition(Guid id, double left)
    {
        var position = ClotheslineLayout.PositionForLeft(ActualWidth, left);
        var notes = notebook.Notes.Select(note => note.Id == id
            ? note with { RopePosition = position, MovedAt = DateTimeOffset.UtcNow } : note).ToList();
        Commit(notebook with { Notes = notes });
        RenderNotes();
    }
}
