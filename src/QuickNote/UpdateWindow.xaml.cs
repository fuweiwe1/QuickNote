using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using QuickNote.Core;

namespace QuickNote;

public partial class UpdateWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private UpdatePackage? available;
    private bool downloading;
    private bool installing;
    private bool readyToRestart;

    public UpdateWindow() : this(true) { }
    internal UpdateWindow(bool checkOnLoad)
    {
        InitializeComponent();
        VersionLabel.Text = $"当前版本 {UpdateCoordinator.CurrentVersion} · " +
            (UpdateCoordinator.Flavor == PackageFlavor.Portable ? "免安装版" : UpdateCoordinator.Flavor == PackageFlavor.Small ? "精简版" : "开发版本");
        if (checkOnLoad) Loaded += async (_, _) => await Check();
        else { Status.Text = "更新将在这个窗口内完成，便签保存在原来的位置。"; ActionButton.IsEnabled = true; }
    }

    private async Task Check()
    {
        ActionButton.IsEnabled = false;
        Status.Text = "正在检查更新…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            available = await UpdateCoordinator.Client.CheckAsync(UpdateCoordinator.CurrentVersion,
                UpdateCoordinator.Flavor ?? PackageFlavor.Small, timeout.Token);
            Status.Text = available is null ? "已经是最新版本。" : $"发现新版本 {available.Version}\n\n{available.Notes}\n\n下载完成后将自动重启，原有便签会保留。";
            ActionButton.Content = available is null ? "重新检查" : "下载并重启";
            ActionButton.IsEnabled = available is null || UpdateCoordinator.Flavor is not null;
            if (available is not null && UpdateCoordinator.Flavor is null) Status.Text += "\n开发版本请先发布再使用应用内更新。";
        }
        catch (OperationCanceledException) { Status.Text = "检查已取消或超时，请稍后重试。"; ActionButton.IsEnabled = true; }
        catch (Exception) { Status.Text = "暂时无法检查更新，请确认能连接 GitHub 后重试。当前版本可以继续使用。"; ActionButton.IsEnabled = true; }
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        if (available is null) { await Check(); return; }
        ActionButton.IsEnabled = false;
        downloading = true;
        CancelButton.Content = "取消下载";
        DownloadProgress.Visibility = Visibility.Visible;
        try
        {
            var workspace = UpdateCoordinator.CreateWorkspace();
            var progress = new Progress<double>(value => { DownloadProgress.Value = value * 100; Status.Text = $"正在下载 {available.Version}，{value:P0}"; });
            await UpdateCoordinator.Client.DownloadAsync(available, Path.Combine(workspace, "package.exe"), progress, cancellation.Token);
            downloading = false;
            installing = true;
            CancelButton.IsEnabled = false;
            Status.Text = "正在准备安装，完成后将自动重启…";
            await UpdateCoordinator.StartHelper(workspace, available);
            readyToRestart = true;
            DialogResult = true;
        }
        catch (OperationCanceledException) { Status.Text = "下载已取消，当前版本保持运行。"; }
        catch (Exception ex) { Status.Text = "更新未完成，当前版本保持运行。\n\n" + ex.Message; }
        finally
        {
            downloading = false; installing = false;
            CancelButton.IsEnabled = true; CancelButton.Content = "关闭";
            ActionButton.IsEnabled = !cancellation.IsCancellationRequested;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    { cancellation.Cancel(); if (!downloading) Close(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (installing && !readyToRestart) { e.Cancel = true; return; }
        cancellation.Cancel();
    }
}
