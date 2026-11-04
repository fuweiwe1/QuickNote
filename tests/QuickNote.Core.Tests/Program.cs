using System;
using System.IO;
using System.Linq;
using QuickNote.Core;

// Dependency-free integration checks against real temporary files, on Linux and Windows.
var passed = 0;
void Check(string name, Action<string> test)
{
    var directory = Path.Combine(Path.GetTempPath(), "QuickNote.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { test(directory); passed++; Console.WriteLine("PASS " + name); }
    finally { Directory.Delete(directory, recursive: true); }
}
void Assert(bool condition, string message = "Assertion failed")
{ if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

Check("First launch starts empty", path =>
{
    var loaded = new NotebookStore(path).Load();
    Assert(loaded.Notebook.Notes.Count == 0 && !loaded.RecoveredFromBackup);
    Assert(loaded.Notebook.Preferences.MonitorDevice is null);
});
Check("Unicode, multiline, spaces, timestamps and preferences survive restart", path =>
{
    var note = new Note { Text = "  灵感 💡\r\n第二行\nhttps://example.com  ", Color = 3 };
    var store = new NotebookStore(path);
    store.Save(new() { Notes = new() { note }, Preferences = new() { Left = -1200, Top = 42, MonitorDevice = "DISPLAY2" } });
    var loaded = new NotebookStore(path).Load().Notebook;
    Assert(loaded.Notes.Single() == note);
    Assert(loaded.Preferences.Left == -1200 && loaded.Preferences.MonitorDevice == "DISPLAY2");
});
Check("Images are independent copies, and preserve exact bytes", path =>
{
    var store = new NotebookStore(path);
    var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
    var id = Guid.NewGuid();
    var filename = store.WriteImage(id, bytes);
    var note = new Note { Id = id, Kind = NoteKind.Image, ImageFile = filename, Label = "截图.png" };
    store.Save(new() { Notes = new() { note } });
    bytes[0] = 0;
    var loaded = new NotebookStore(path).Load().Notebook.Notes.Single();
    Assert(File.ReadAllBytes(store.ImagePath(loaded.ImageFile!))[0] == 137);
    Throws<IOException>(() => store.WriteImage(id, new byte[] { 1 }));
});
Check("Reorder, edit, delete and restored snapshots survive restart", path =>
{
    var store = new NotebookStore(path);
    var a = new Note { Text = "A" };
    var b = new Note { Text = "B" };
    store.Save(new() { Notes = new() { a, b } });
    store.Save(new() { Notes = new() { b, a with { Text = "changed" } } });
    Assert(store.Load().Notebook.Notes[0].Id == b.Id);
    store.Save(new() { Notes = new() { b } });
    Assert(store.Load().Notebook.Notes.Count == 1);
    store.Save(new() { Notes = new() { b, a } });
    Assert(store.Load().Notebook.Notes[1].Id == a.Id);
});
Check("Truncated primary recovers backup and preserves damaged bytes", path =>
{
    var store = new NotebookStore(path);
    store.Save(new() { Notes = new() { new() { Text = "original" } } });
    store.Save(new() { Notes = new() { new() { Text = "newest" } } });
    File.WriteAllText(Path.Combine(path, "notebook.json"), "{truncated");
    var recovered = store.Load();
    Assert(recovered.RecoveredFromBackup && recovered.Notebook.Notes.Single().Text == "original");
    Assert(Directory.GetFiles(path, "*.damaged-*").Length == 1);
    Assert(store.Load().Notebook.Notes.Single().Text == "original");
});
Check("Missing primary recovers backup", path =>
{
    var store = new NotebookStore(path);
    store.Save(new()); store.Save(new());
    File.Delete(Path.Combine(path, "notebook.json"));
    Assert(store.Load().RecoveredFromBackup);
});
Check("Unrecoverable corruption never gets overwritten", path =>
{
    var manifest = Path.Combine(path, "notebook.json");
    File.WriteAllText(manifest, "broken data");
    Throws<InvalidDataException>(() => new NotebookStore(path).Load());
    Assert(File.ReadAllText(manifest) == "broken data");
});
Check("Invalid snapshots do not alter a saved notebook", path =>
{
    var store = new NotebookStore(path);
    store.Save(new() { Notes = new() { new() { Text = "safe" } } });
    Throws<InvalidDataException>(() => store.Save(new() { Notes = new() { new() { Text = new string('x', 100_001) } } }));
    var same = new Note();
    Throws<InvalidDataException>(() => store.Save(new() { Notes = new() { same, same } }));
    Throws<InvalidDataException>(() => store.Save(new() { Preferences = new() { Left = double.NaN } }));
    Throws<InvalidDataException>(() => store.Save(new() { Notes = new() { new() { Color = 10 } } }));
    Assert(store.Load().Notebook.Notes.Single().Text == "safe");
    Assert(!Directory.EnumerateFiles(path, "*.tmp").Any());
});
Check("Local image paths cannot escape their directory", path =>
{
    var store = new NotebookStore(path);
    foreach (var invalid in new[] { "../secret.png", "/tmp/secret.png", "C:\\secret.png", new string('a', 32) + ".jpg", "bad.png" })
        Throws<InvalidDataException>(() => store.ImagePath(invalid));
    Throws<InvalidDataException>(() => store.Save(new() { Notes = new() { new() { Kind = NoteKind.Image, ImageFile = "../secret.png" } } }));
});
Check("Image size limits reject empty and oversized input", path =>
{
    var store = new NotebookStore(path);
    Throws<InvalidDataException>(() => store.WriteImage(Guid.NewGuid(), Array.Empty<byte>()));
    Throws<InvalidDataException>(() => store.WriteImage(Guid.NewGuid(), new byte[NotebookStore.MaxImageBytes + 1]));
    Assert(!Directory.EnumerateFiles(store.ImageDirectory).Any());
});
Check("Image cleanup retains current and backup references", path =>
{
    var store = new NotebookStore(path);
    var old = store.WriteImage(Guid.NewGuid(), new byte[] { 1 });
    var current = store.WriteImage(Guid.NewGuid(), new byte[] { 2 });
    var orphan = store.WriteImage(Guid.NewGuid(), new byte[] { 3 });
    store.Save(new() { Notes = new() { new() { Kind = NoteKind.Image, ImageFile = old } } });
    var notebook = new Notebook { Notes = new() { new() { Kind = NoteKind.Image, ImageFile = current } } };
    store.Save(notebook);
    store.CollectUnusedImages(notebook);
    Assert(File.Exists(store.ImagePath(old)) && File.Exists(store.ImagePath(current)));
    Assert(!File.Exists(store.ImagePath(orphan)));
});
Check("Future data versions do not fall back to an older backup", path =>
{
    var store = new NotebookStore(path);
    store.Save(new()); store.Save(new());
    var manifest = Path.Combine(path, "notebook.json");
    File.WriteAllText(manifest, "{\"Version\":2,\"Notes\":[]}");
    Throws<NotSupportedException>(() => store.Load());
    Assert(File.ReadAllText(manifest).Contains("\"Version\":2"));
});
Check("Old notebooks ignore the retired floating-window preference", path =>
{
    var store = new NotebookStore(path);
    var manifest = Path.Combine(path, "notebook.json");
    var id = Guid.NewGuid();
    File.WriteAllText(manifest, $"{{\"Version\":1,\"Notes\":[{{\"Id\":\"{id}\",\"Text\":\"旧版便签\"}}],\"Preferences\":{{\"AlwaysOnTop\":true,\"Left\":100,\"Top\":30}}}}");
    var loaded = store.Load().Notebook;
    Assert(loaded.Notes.Single().Text == "旧版便签");
    store.Save(loaded);
    Assert(!File.ReadAllText(manifest).Contains("AlwaysOnTop"));
});
Check("A short clothesline leaves natural gaps", _ =>
{
    Assert(ClotheslineLayout.Positions(1920, 0).Length == 0);
    Assert(ClotheslineLayout.Positions(1920, 1).Single() == ClotheslineLayout.FirstCardLeft);
    var positions = ClotheslineLayout.Positions(1920, 3);
    Assert(positions[1] - positions[0] > ClotheslineLayout.CardWidth);
});
Check("Dense clotheslines overlap and keep the last card within the monitor", _ =>
{
    foreach (var width in new[] { 640.0, 800, 1280, 1536, 1920, 2560, 3840 })
    foreach (var count in new[] { 2, 10, 40, 200 })
    {
        var positions = ClotheslineLayout.Positions(width, count);
        Assert(positions.Zip(positions.Skip(1)).All(pair => pair.First < pair.Second));
        Assert(positions[0] >= ClotheslineLayout.NotebookLeft + ClotheslineLayout.NotebookWidth);
        Assert(positions[^1] + ClotheslineLayout.CardWidth <= width - ClotheslineLayout.RightInset + 0.001);
        if (count * (ClotheslineLayout.CardWidth + 18) > width)
            Assert(positions[1] - positions[0] < ClotheslineLayout.CardWidth);
    }
});
Check("Clothesline follows the same sag as its quadratic rope", _ =>
{
    Assert(ClotheslineLayout.RopeY(0, 1920) == 12 && ClotheslineLayout.RopeY(1920, 1920) == 12);
    Assert(ClotheslineLayout.RopeY(960, 1920) == 27);
    Throws<ArgumentOutOfRangeException>(() => ClotheslineLayout.Positions(double.NaN, 2));
    Throws<ArgumentOutOfRangeException>(() => ClotheslineLayout.Positions(1920, -1));
});
Check("Only the selected monitor top edge reveals a hidden rope", _ =>
{
    var dock = new EdgeDockState();
    var bounds = new DockBounds(-1920, -1080, 1920, 330);
    Assert(!dock.Update(-1800, -1000, bounds, 3, 0));
    Assert(!dock.Update(10, -1079, bounds, 3, 10));
    Assert(!dock.Update(-1800, -1081, bounds, 3, 20));
    Assert(dock.Update(-1800, -1079, bounds, 3, 30));
});
Check("Mouse departure has a grace period, and return cancels hiding", _ =>
{
    var dock = new EdgeDockState();
    var bounds = new DockBounds(0, 0, 1920, 330);
    dock.Reveal(0);
    Assert(dock.Update(600, 600, bounds, 3, 349));
    Assert(dock.Update(600, 100, bounds, 3, 350));
    Assert(dock.Update(600, 600, bounds, 3, 699));
    Assert(!dock.Update(600, 600, bounds, 3, 700));
    Assert(!dock.Update(600, 100, bounds, 3, 710));
    Assert(dock.Update(600, 0, bounds, 3, 720));
});
Check("Menus, dragging and details hold the dock open until all interactions end", _ =>
{
    var dock = new EdgeDockState();
    var bounds = new DockBounds(0, 0, 1920, 330);
    dock.Reveal(0); dock.Hold(); dock.Hold();
    Assert(dock.Update(600, 600, bounds, 3, 5000));
    dock.Release(5000);
    Assert(dock.Update(600, 600, bounds, 3, 9000));
    dock.Release(9000);
    Assert(dock.Update(600, 600, bounds, 3, 9349));
    Assert(!dock.Update(600, 600, bounds, 3, 9350));
});
Console.WriteLine($"\n{passed} storage, layout and edge docking checks passed.");
