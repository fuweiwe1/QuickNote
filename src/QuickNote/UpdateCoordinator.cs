using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickNote.Core;

namespace QuickNote;

internal sealed record UpdatePlan(int ParentId, long ParentStarted, string Target, string Sha256, long Size, string Version, bool SmokeMode);

internal static class UpdateCoordinator
{
    internal static Version CurrentVersion => UpdateClient.Normalize(typeof(App).Assembly.GetName().Version!);
    internal static PackageFlavor? Flavor => Enum.TryParse<PackageFlavor>(typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "QuickNoteFlavor")?.Value, out var flavor) ? flavor : null;
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickNote", "updates");
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    internal static UpdateClient Client => new(Http);

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

    internal static async Task StartHelper(string workspace, UpdatePackage package, bool smokeMode = false)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || (!smokeMode && Flavor is null))
            throw new InvalidOperationException("请使用正式发布的精简版或免安装版进行更新。");
        var staged = Path.Combine(workspace, "package.exe");
        ExecutableUpdate.Verify(staged, package.Size, package.Sha256);
        using var current = Process.GetCurrentProcess();
        var plan = new UpdatePlan(current.Id, current.StartTime.ToUniversalTime().Ticks, executable,
            package.Sha256, package.Size, package.Version.ToString(), smokeMode);
        var planFile = Path.Combine(workspace, "plan.json");
        File.WriteAllText(planFile, JsonSerializer.Serialize(plan));
        var helperFile = Path.Combine(workspace, "helper.exe");
        File.Copy(executable, helperFile);
        var start = new ProcessStartInfo(helperFile) { UseShellExecute = false, WorkingDirectory = workspace };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(planFile);
        using var helper = Process.Start(start) ?? throw new Win32Exception("无法启动更新程序。");
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
