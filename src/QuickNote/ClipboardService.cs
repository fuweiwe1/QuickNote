using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using QuickNote.Core;

namespace QuickNote;

internal static class ClipboardService
{
    // Stay on WPF's STA dispatcher while retrying a clipboard locked by another app.
    internal static async Task<T> WithRetry<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (COMException) when (attempt < 4) { await Task.Delay(70 * (attempt + 1)); }
        }
    }

    internal static Note? Read(NotebookStore store, int color)
    {
        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image is not null) return FromImage(image, store, color, "剪贴板图片");
        }
        // Explorer's Copy operation exposes paths rather than bitmap data.
        if (Clipboard.ContainsFileDropList())
        {
            foreach (string? path in Clipboard.GetFileDropList())
                if (path is not null && IsImage(path)) return FromFile(path, store, color);
        }
        if (!Clipboard.ContainsText()) return null;
        var text = Clipboard.GetText(TextDataFormat.UnicodeText);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length > NotebookStore.MaxTextLength) throw new InvalidDataException("这段文字超过 10 万字，请分成几张便签。");
        return new Note { Kind = NoteKind.Text, Text = text, Color = color };
    }

    internal static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff";

    internal static Note FromFile(string path, NotebookStore store, int color)
    {
        if (new FileInfo(path).Length > NotebookStore.MaxImageBytes) throw new InvalidDataException("图片需要小于 32 MB。");
        using (var input = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if ((long)decoder.Frames[0].PixelWidth * decoder.Frames[0].PixelHeight > 32_000_000)
                throw new InvalidDataException("图片尺寸太大，请缩小到 3200 万像素以内。");
        }
        return FromImage(NoteCard.LoadImage(path), store, color, Path.GetFileName(path));
    }

    private static Note FromImage(BitmapSource image, NotebookStore store, int color, string label)
    {
        if ((long)image.PixelWidth * image.PixelHeight > 32_000_000)
            throw new InvalidDataException("图片尺寸太大，请缩小到 3200 万像素以内。");
        var note = new Note { Kind = NoteKind.Image, Color = color, Label = label };
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return note with { ImageFile = store.WriteImage(note.Id, stream.ToArray()) };
    }

    internal static void Copy(Note note, NotebookStore store)
    {
        if (note.Kind == NoteKind.Text) Clipboard.SetDataObject(note.Text, copy: true);
        else if (note.ImageFile is not null)
        {
            var image = NoteCard.LoadImage(store.ImagePath(note.ImageFile));
            var data = new DataObject();
            data.SetImage(image);
            // PNG preserves alpha for apps that understand the registered PNG format.
            using var png = new MemoryStream(File.ReadAllBytes(store.ImagePath(note.ImageFile)));
            data.SetData("PNG", png);
            Clipboard.SetDataObject(data, copy: true);
        }
    }
}
