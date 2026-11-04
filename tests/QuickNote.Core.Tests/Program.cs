using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    Assert(dock.Update(600, 600, bounds, 3, 199));
    Assert(dock.Update(600, 100, bounds, 3, 200));
    Assert(dock.Update(600, 600, bounds, 3, 399));
    Assert(!dock.Update(600, 600, bounds, 3, 400));
    Assert(!dock.Update(600, 100, bounds, 3, 410));
    Assert(dock.Update(600, 0, bounds, 3, 420));
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
    Assert(dock.Update(600, 600, bounds, 3, 9199));
    Assert(!dock.Update(600, 600, bounds, 3, 9200));
});

var payload = Encoding.UTF8.GetBytes("MZ test update payload");
var digest = Convert.ToHexString(SHA256.HashData(payload));
UpdatePackage Package(PackageFlavor flavor = PackageFlavor.Small) => new(new Version(0, 4, 0), flavor, "新版说明",
    new Uri($"https://github.com/fuweiwe1/QuickNote/releases/download/v0.4.0/QuickNote-win-x64-{flavor}.exe"), payload.Length, digest);
byte[] ReleaseJson(bool prerelease = false, bool draft = false, string? hash = null, string? url = null) => JsonSerializer.SerializeToUtf8Bytes(new
{
    tag_name = "v0.4.0", prerelease, draft, body = "更新说明",
    assets = new[] { new { name = "QuickNote-win-x64-Small.exe", size = payload.Length, digest = "sha256:" + (hash ?? digest),
        browser_download_url = url ?? Package().DownloadUri.AbsoluteUri } }
});
Check("Stable release selection preserves flavor and normalizes assembly revisions", _ =>
{
    var package = UpdateClient.ParseRelease(ReleaseJson(), new Version(0, 3, 0, 0), PackageFlavor.Small)!;
    Assert(package.Version == new Version(0, 4, 0) && package.Flavor == PackageFlavor.Small);
    Assert(UpdateClient.ParseRelease(ReleaseJson(), new Version(0, 4, 0, 0), PackageFlavor.Small) is null);
    Assert(UpdateClient.ParseRelease(ReleaseJson(), new Version(0, 5, 0), PackageFlavor.Small) is null);
    Assert(UpdateClient.ParseRelease(ReleaseJson(prerelease: true), new Version(0, 3, 0), PackageFlavor.Small) is null);
    Assert(UpdateClient.ParseRelease(ReleaseJson(draft: true), new Version(0, 3, 0), PackageFlavor.Small) is null);
    Throws<InvalidDataException>(() => UpdateClient.ParseRelease(ReleaseJson(), new Version(0, 3, 0), PackageFlavor.Portable));
});
Check("Update metadata rejects unrelated hosts, missing hashes and oversized executables", _ =>
{
    Throws<InvalidDataException>(() => UpdateClient.ParseRelease(ReleaseJson(url: "https://example.com/fake.exe"), new Version(0, 3, 0), PackageFlavor.Small));
    Throws<InvalidDataException>(() => UpdateClient.ParseRelease(ReleaseJson(hash: "bad"), new Version(0, 3, 0), PackageFlavor.Small));
    Throws<InvalidDataException>(() => UpdateClient.Validate(Package() with { Size = UpdateClient.MaximumPackageSize + 1 }));
    Throws<InvalidDataException>(() => UpdateClient.Validate(Package() with { DownloadUri = new Uri(Package().DownloadUri.AbsoluteUri.Replace("https:", "http:")) }));
});
Check("Release checks handle unavailable, current and newer releases without credentials", _ =>
{
    using var client = new HttpClient(new FixtureHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ReleaseJson()) }));
    var update = new UpdateClient(client).CheckAsync(new Version(0, 3, 0), PackageFlavor.Small).GetAwaiter().GetResult();
    Assert(update is not null);
    using var empty = new HttpClient(new FixtureHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
    Assert(new UpdateClient(empty).CheckAsync(new Version(0, 3, 0), PackageFlavor.Small).GetAwaiter().GetResult() is null);
});
Check("Downloaded update is persisted only when size and hash match", path =>
{
    using var http = new HttpClient(new FixtureHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
    var client = new UpdateClient(http);
    var file = Path.Combine(path, "package.exe");
    client.DownloadAsync(Package(), file).GetAwaiter().GetResult();
    Assert(File.ReadAllBytes(file).SequenceEqual(payload));
    Throws<IOException>(() => client.DownloadAsync(Package(), file).GetAwaiter().GetResult());
    Assert(File.ReadAllBytes(file).SequenceEqual(payload));
});
Check("Failed or canceled downloads never replace existing programs or leave partial files", path =>
{
    var target = Path.Combine(path, "QuickNote.exe");
    File.WriteAllText(target, "old version");
    using var http = new HttpClient(new FixtureHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
    var client = new UpdateClient(http);
    var staged = Path.Combine(path, "partial.exe");
    Throws<InvalidDataException>(() => client.DownloadAsync(Package() with { Sha256 = new string('0', 64) }, staged).GetAwaiter().GetResult());
    Assert(!File.Exists(staged));
    Throws<InvalidDataException>(() => client.DownloadAsync(Package() with { Size = payload.Length + 1 }, staged).GetAwaiter().GetResult());
    Assert(!File.Exists(staged));
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Throws<OperationCanceledException>(() => client.DownloadAsync(Package(), staged, cancellation: cancellation.Token).GetAwaiter().GetResult());
    Assert(File.ReadAllText(target) == "old version" && !File.Exists(staged));
});
Check("Executable replacement keeps the previous version and supports rollback", path =>
{
    var target = Path.Combine(path, "QuickNote.exe");
    var staged = Path.Combine(path, "package.exe");
    File.WriteAllText(target, "old version");
    File.WriteAllBytes(staged, payload);
    File.WriteAllText(Path.Combine(path, "notebook.json"), "笔记保留");
    ExecutableUpdate.Apply(staged, target, payload.Length, digest);
    Assert(File.ReadAllBytes(target).SequenceEqual(payload) && File.ReadAllText(target + ".previous") == "old version");
    ExecutableUpdate.Rollback(target);
    Assert(File.ReadAllText(target) == "old version" && File.ReadAllText(Path.Combine(path, "notebook.json")) == "笔记保留");
    Assert(File.ReadAllBytes(target + ".failed").SequenceEqual(payload));
    ExecutableUpdate.Apply(staged, target, payload.Length, digest);
    ExecutableUpdate.Apply(staged, target, payload.Length, digest);
    Assert(!Directory.EnumerateFiles(path, ".QuickNote-new-*").Any());
});
Check("Cancellation after partial download removes the incomplete executable", path =>
{
    using var http = new HttpClient(new FixtureHttp(_ =>
    {
        var content = new StreamContent(new CancelAfterFirstRead(payload));
        content.Headers.ContentLength = payload.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }));
    var staged = Path.Combine(path, "package.exe");
    Throws<OperationCanceledException>(() => new UpdateClient(http).DownloadAsync(Package(), staged).GetAwaiter().GetResult());
    Assert(!File.Exists(staged));
});
Check("A corrupted staged executable leaves the installed version untouched", path =>
{
    var target = Path.Combine(path, "QuickNote.exe");
    var staged = Path.Combine(path, "package.exe");
    File.WriteAllText(target, "old version");
    File.WriteAllText(staged, "corrupt update");
    Throws<InvalidDataException>(() => ExecutableUpdate.Apply(staged, target, payload.Length, digest));
    Assert(File.ReadAllText(target) == "old version" && !File.Exists(target + ".previous"));
});
Check("Free card positions survive restart and resize without moving other cards", path =>
{
    var store = new NotebookStore(path);
    var a = new Note { Text = "自由定位", RopePosition = 0.72, MovedAt = DateTimeOffset.UtcNow };
    var b = new Note { Text = "自动排布" };
    store.Save(new() { Notes = new() { a, b } });
    var notes = store.Load().Notebook.Notes;
    Assert(notes[0].RopePosition == 0.72 && notes[0].MovedAt == a.MovedAt);
    foreach (var width in new[] { 800.0, 1280, 1920, 2560 })
    {
        var positions = ClotheslineLayout.Positions(width, notes);
        Assert(Math.Abs(ClotheslineLayout.PositionForLeft(width, positions[0]) - 0.72) < 0.00001);
        Assert(positions[1] == ClotheslineLayout.Positions(width, 2)[1]);
        Assert(positions.All(x => x >= ClotheslineLayout.FirstCardLeft && x + ClotheslineLayout.CardWidth <= width - ClotheslineLayout.RightInset + 0.00001));
    }
    Assert(ClotheslineLayout.PositionForLeft(1280, -500) == 0);
    Assert(ClotheslineLayout.PositionForLeft(1280, 50000) == 1);
    Throws<InvalidDataException>(() => store.Save(new() { Notes = new() { a with { RopePosition = double.NaN } } }));
    Throws<InvalidDataException>(() => store.Save(new() { Notes = new() { a with { RopePosition = 1.1 } } }));
    Assert(store.Load().Notebook.Notes[0].RopePosition == 0.72);
});
Console.WriteLine($"\n{passed} storage, layout, docking, dragging and update checks passed.");

sealed class FixtureHttp : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> response;
    public FixtureHttp(Func<HttpRequestMessage, HttpResponseMessage> response) => this.response = response;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
}

sealed class CancelAfterFirstRead : MemoryStream
{
    private bool read;
    public CancelAfterFirstRead(byte[] bytes) : base(bytes) { }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (read) return ValueTask.FromCanceled<int>(new CancellationToken(canceled: true));
        read = true;
        return base.ReadAsync(buffer[..1], cancellationToken);
    }
}
