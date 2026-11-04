using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace QuickNote.Core;

/// <summary>Atomic local storage. A failed load must never silently replace someone's notes.</summary>
public sealed class NotebookStore
{
    public const int MaxTextLength = 100_000;
    public const int MaxImageBytes = 32 * 1024 * 1024;
    private readonly string root;
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };
    private string Manifest => Path.Combine(root, "notebook.json");
    private string Backup => Manifest + ".bak";
    public string ImageDirectory => Path.Combine(root, "images");

    public NotebookStore(string root)
    {
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(ImageDirectory);
    }

    public LoadResult Load()
    {
        if (!File.Exists(Manifest) && !File.Exists(Backup)) return new(new(), false);
        try { return new(Read(Manifest), false); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            if (!File.Exists(Backup))
                throw new InvalidDataException("便签数据无法读取。已保留原始文件，请勿删除数据目录。", ex);
            var restored = Read(Backup);
            if (File.Exists(Manifest))
                File.Move(Manifest, Manifest + ".damaged-" + Guid.NewGuid().ToString("N"));
            File.Copy(Backup, Manifest);
            return new(restored, true);
        }
    }

    public void Save(Notebook notebook)
    {
        Validate(notebook);
        var temporary = Manifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, notebook, json);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(Manifest)) File.Copy(Manifest, Backup, overwrite: true);
            File.Move(temporary, Manifest, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string WriteImage(Guid id, byte[] png)
    {
        if (png.Length == 0 || png.Length > MaxImageBytes)
            throw new InvalidDataException("图片需要小于 32 MB。");
        var name = id.ToString("N") + ".png";
        using var stream = new FileStream(ImagePath(name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(png);
        stream.Flush(flushToDisk: true);
        return name;
    }

    public string ImagePath(string name)
    {
        if (name.Length != 36 || !name.EndsWith(".png", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[..32], "N", out _))
            throw new InvalidDataException("无效的本地图片路径。");
        return Path.Combine(ImageDirectory, name);
    }

    // Keep images used by either the current file or its recovery backup.
    // Run only on startup, when no undo operation can still reference an image.
    public void CollectUnusedImages(Notebook current)
    {
        var keep = current.Notes.Select(n => n.ImageFile).OfType<string>().ToHashSet();
        if (File.Exists(Backup))
        {
            try { keep.UnionWith(Read(Backup).Notes.Select(n => n.ImageFile).OfType<string>()); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { return; }
        }
        foreach (var path in Directory.EnumerateFiles(ImageDirectory, "*.png"))
            if (!keep.Contains(Path.GetFileName(path))) File.Delete(path);
    }

    private Notebook Read(string path)
    {
        using var stream = File.OpenRead(path);
        var notebook = JsonSerializer.Deserialize<Notebook>(stream, json)
            ?? throw new InvalidDataException("便签文件为空。");
        Validate(notebook);
        return notebook;
    }

    private void Validate(Notebook notebook)
    {
        if (notebook.Version != 1)
            throw new NotSupportedException("便签由其他版本创建，请使用兼容的 QuickNote 版本打开。");
        if (notebook.Notes is null || notebook.Preferences is null)
            throw new InvalidDataException("无法识别的便签文件版本。");
        var ids = new HashSet<Guid>();
        foreach (var note in notebook.Notes)
        {
            if (note is null || note.Id == Guid.Empty || !ids.Add(note.Id) ||
                note.Text is null || note.Label is null || note.Text.Length > MaxTextLength ||
                note.Color is < 0 or > 4 || !Enum.IsDefined(note.Kind))
                throw new InvalidDataException("便签数据格式异常。");
            if (note.RopePosition is double position && (!double.IsFinite(position) || position < 0 || position > 1))
                throw new InvalidDataException("便签在绳子上的位置无效。");
            if (note.Kind == NoteKind.Image)
            {
                if (note.ImageFile is null) throw new InvalidDataException("图片便签缺少文件路径。");
                _ = ImagePath(note.ImageFile);
            }
        }
        var p = notebook.Preferences;
        if ((p.Left.HasValue && !double.IsFinite(p.Left.Value)) ||
            (p.Top.HasValue && !double.IsFinite(p.Top.Value)))
            throw new InvalidDataException("窗口位置无效。");
    }
}
