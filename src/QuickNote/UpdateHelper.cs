using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using QuickNote.Core;

namespace QuickNote;

internal static class UpdateHelper
{
    internal static async Task Run(string planFile)
    {
        if (!UpdateCoordinator.IsWorkspaceFile(planFile, "plan.json")) throw new InvalidDataException("无效的更新计划路径。");
        var workspace = Path.GetDirectoryName(planFile)!;
        var staged = Path.Combine(workspace, "package.exe");
        var health = Path.Combine(workspace, "started.ok");
        var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planFile)) ?? throw new InvalidDataException("更新计划为空。");
        var applied = false;
        var released = false;
        Process? restarted = null;
        Window? progress = null;
        try
        {
            using var parent = Process.GetProcessById(plan.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStarted ||
                !string.Equals(Path.GetFullPath(parent.MainModule!.FileName), Path.GetFullPath(plan.Target), StringComparison.OrdinalIgnoreCase) ||
                !plan.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新目标与正在运行的程序不一致。");
            if (!Version.TryParse(plan.Version, out var version) || (!plan.SmokeMode && version <= UpdateCoordinator.CurrentVersion))
                throw new InvalidDataException("更新版本无效。");
            ExecutableUpdate.Verify(staged, plan.Size, plan.Sha256);
            var probe = Path.Combine(Path.GetDirectoryName(plan.Target)!, ".QuickNote-write-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
            if (!plan.SmokeMode)
            {
                progress = new Window { Title = "晾一会 · 更新", Width = 360, Height = 150, ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false, Topmost = true,
                    Content = new TextBlock { Text = "正在安装更新，请稍候…", FontSize = 16, Margin = new Thickness(28) } };
                progress.Show();
            }
            File.WriteAllText(Path.Combine(workspace, "ready"), "ready");
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90))) await parent.WaitForExitAsync(timeout.Token);
            released = true;
            for (var attempt = 0; ; attempt++)
            {
                try { ExecutableUpdate.Apply(staged, plan.Target, plan.Size, plan.Sha256); break; }
                catch (IOException) when (attempt < 15) { await Task.Delay(200); }
            }
            applied = true;
            var start = new ProcessStartInfo(plan.Target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.Target)! };
            start.ArgumentList.Add(plan.SmokeMode ? "--update-smoke-startup" : "--update-health");
            start.ArgumentList.Add(health);
            restarted = Process.Start(start) ?? throw new IOException("新版启动失败。");
            for (var i = 0; i < 200; i++)
            {
                if (File.Exists(health))
                {
                    File.WriteAllText(Path.Combine(workspace, "complete"), "complete");
                    return;
                }
                if (restarted.HasExited) throw new IOException("新版未能正常打开。");
                await Task.Delay(100);
            }
            throw new IOException("新版启动超时。");
        }
        catch (Exception ex)
        {
            if (restarted is { HasExited: false }) { restarted.Kill(); await restarted.WaitForExitAsync(); }
            var detail = ex.Message;
            if (released)
            {
                try
                {
                    if (applied) ExecutableUpdate.Rollback(plan.Target);
                    if (!plan.SmokeMode) Process.Start(new ProcessStartInfo(plan.Target) { UseShellExecute = false });
                    detail += "\n已恢复原版本，便签数据保持不变。";
                }
                catch (Exception restore) { detail += "\n恢复时发生错误：" + restore.Message + "\n旧程序保存在：" + plan.Target + ".previous"; }
            }
            File.WriteAllText(Path.Combine(workspace, "error.txt"), detail);
            if (released && !plan.SmokeMode) MessageBox.Show(detail, "更新未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { restarted?.Dispose(); progress?.Close(); }
    }
}
