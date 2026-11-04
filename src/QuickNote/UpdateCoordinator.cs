using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickNote.Core;

namespace QuickNote;

internal sealed record UpdatePlan(int ParentId, long ParentStarted, string Target, string Sha256, long Size, string Version, bool SmokeMode, bool FailStartup = false);

internal static class UpdateCoordinator
{
    internal static Version CurrentVersion => UpdateClient.Normalize(typeof(App).Assembly.GetName().Version!);
    internal static PackageFlavor? Flavor => Enum.TryParse<PackageFlavor>(typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "QuickNoteFlavor")?.Value, out var flavor) ? flavor : null;
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickNote", "updates");
    private static HttpClient? http;
    internal static HttpClient Http => http ??= CreateHttp(ProxyAddress);
    internal static UpdateClient Client => new(Http);
    internal static string BrowserDownload => $"https://github.com/{UpdateClient.Repository}/releases/latest/download/QuickNote-win-x64-{Flavor ?? PackageFlavor.Small}.exe";

    internal static string? ProxyAddress
    {
        get
        {
            try
            {
                var file = Path.Combine(Root, "proxy.json");
                return File.Exists(file) ? UpdateProxy.Normalize(JsonSerializer.Deserialize<string>(File.ReadAllText(file))) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
        }
    }

    private static HttpClient CreateHttp(string? proxy)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2), UseProxy = true };
        if (proxy is not null) handler.Proxy = new WebProxy(proxy);
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
    }

    internal static void SaveProxy(string? address)
    {
        var proxy = UpdateProxy.Normalize(address);
        var replacement = CreateHttp(proxy);
        try
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, "proxy.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(proxy));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch { replacement.Dispose(); throw; }
        var previous = http;
        http = replacement;
        previous?.Dispose();
    }

    internal static void WriteCheckLog(string detail)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "last-check.log"), DateTimeOffset.Now.ToString("O") + Environment.NewLine + detail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static string CreateWorkspace()
    {
        Directory.CreateDirectory(Root);
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) continue;
            try
            {
                if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-7)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var workspace = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    internal static bool IsWorkspaceFile(string path, string name)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        return Path.GetFileName(full) == name && Guid.TryParseExact(Path.GetFileName(directory), "N", out _) &&
            string.Equals(Path.GetDirectoryName(directory), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task StartHelper(string workspace, UpdatePackage package, bool smokeMode = false, bool failStartup = false)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || (!smokeMode && Flavor is null))
            throw new InvalidOperationException("请使用正式发布的精简版或免安装版进行更新。");
        var staged = Path.Combine(workspace, "package.exe");
        ExecutableUpdate.Verify(staged, package.Size, package.Sha256);
        using var current = Process.GetCurrentProcess();
        var plan = new UpdatePlan(current.Id, current.StartTime.ToUniversalTime().Ticks, executable,
            package.Sha256, package.Size, package.Version.ToString(), smokeMode, failStartup);
        var planFile = Path.Combine(workspace, "plan.json");
        File.WriteAllText(planFile, JsonSerializer.Serialize(plan));
        var helperFile = Path.Combine(workspace, "helper.exe");
        File.Copy(executable, helperFile);
        var start = new ProcessStartInfo(helperFile) { UseShellExecute = false, WorkingDirectory = workspace };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(planFile);
        using var helper = Process.Start(start) ?? throw new Win32Exception("无法启动更新程序。");
        if (smokeMode) File.AppendAllText(Path.Combine(workspace, "trace.txt"), "helper launched: " + helper.Id + "\n");
        for (var i = 0; i < 200; i++)
        {
            if (File.Exists(Path.Combine(workspace, "error.txt")))
                throw new IOException(File.ReadAllText(Path.Combine(workspace, "error.txt")));
            if (File.Exists(Path.Combine(workspace, "ready"))) return;
            if (helper.HasExited) throw new IOException("更新程序未能启动，当前版本保持运行。");
            await Task.Delay(100);
        }
        if (!helper.HasExited) { helper.Kill(); await helper.WaitForExitAsync(); }
        throw new IOException("更新程序准备超时，当前版本保持运行，请重试。");
    }

    internal static void MarkHealthy(string file)
    {
        if (!IsWorkspaceFile(file, "started.ok")) throw new InvalidDataException("无效的更新启动标记。");
        File.WriteAllText(file, "started");
    }
}
