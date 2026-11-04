using System;
using System.IO;
using System.Threading;
using System.Windows;
using QuickNote.Core;

namespace QuickNote;

public partial class App : Application
{
    private Mutex? singleInstance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 1 && e.Args[0] == "--update-smoke-fail-startup")
        { Shutdown(1); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--apply-update")
        {
            try { await UpdateHelper.Run(e.Args[1]); Shutdown(0); }
            catch (Exception ex)
            {
                if (UpdateCoordinator.IsWorkspaceFile(e.Args[1], "plan.json"))
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(e.Args[1])!, "error.txt"), ex.Message);
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--update-smoke-startup")
        { UpdateCoordinator.MarkHealthy(e.Args[1]); Shutdown(0); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--update-parent-smoke")
        {
            try { await UpdateSmokeChecks.RunParent(e.Args[1]); Shutdown(0); }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(e.Args[1], "parent-error.txt"), ex.ToString());
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--updater-smoke")
        {
            try { await UpdateSmokeChecks.Run(e.Args[1]); Shutdown(0); }
            catch (Exception ex)
            {
                Directory.CreateDirectory(e.Args[1]);
                File.WriteAllText(Path.Combine(e.Args[1], "failure.txt"), ex.ToString());
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--smoke-test")
        {
            var output = e.Args.Length > 1 ? e.Args[1] : "smoke-results";
            try { await SmokeChecks.Run(output); Shutdown(0); }
            catch (Exception ex)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                Shutdown(1);
            }
            return;
        }
        singleInstance = new Mutex(true, @"Local\QuickNote.Clothesline.v1", out var first);
        if (!first)
        {
            NativeMethods.NotifyExistingInstance();
            Shutdown();
            return;
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickNote");
        try
        {
            var store = new NotebookStore(root);
            var loaded = store.Load();
            try { store.CollectUnusedImages(loaded.Notebook); }
            catch (IOException) { /* Retain old images if another process has them open. */ }
            var window = new MainWindow(store, loaded.Notebook);
            MainWindow = window;
            window.Show();
            if (e.Args.Length >= 2 && e.Args[0] == "--update-health") UpdateCoordinator.MarkHealthy(e.Args[1]);
            if (loaded.RecoveredFromBackup)
                MessageBox.Show(window, "上次保存的数据损坏，已从备份恢复。原文件也已保留在数据目录中。", "便签已恢复");
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开本地便签，原数据已保留。\n" + root + "\n\n" + ex.Message, "QuickNote 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
