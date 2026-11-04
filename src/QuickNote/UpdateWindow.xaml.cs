using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using QuickNote.Core;

namespace QuickNote;

public partial class UpdateWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Func<UpdateClient> createClient;
    private CancellationTokenSource? operation;
    private UpdatePackage? available;
    private bool downloading;
    private bool installing;
    private bool readyToRestart;
    private bool checking;
    private bool canceledByUser;
    private int checkSequence;
    internal Func<bool>? BeforeInstall { get; set; }

    public UpdateWindow() : this(true) { }
    internal UpdateWindow(bool checkOnLoad, Func<UpdateClient>? createClient = null)
    {
        this.createClient = createClient ?? (() => UpdateCoordinator.Client);
        InitializeComponent();
        VersionLabel.Text = $"当前版本 {UpdateCoordinator.CurrentVersion} · " +
            (UpdateCoordinator.Flavor == PackageFlavor.Portable ? "免安装版" : UpdateCoordinator.Flavor == PackageFlavor.Small ? "精简版" : "开发版本");
        if (checkOnLoad) Loaded += async (_, _) => await Check();
        else { Status.Text = "更新将在这个窗口内完成，便签保存在原来的位置。"; ActionButton.IsEnabled = true; }
    }

    internal async Task Check()
    {
        if (checking || downloading || installing) return;
        checking = true;
        var sequence = ++checkSequence;
        canceledByUser = false;
        available = null;
        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        active.CancelAfter(TimeSpan.FromSeconds(45));
        operation = active;
        ActionButton.IsEnabled = false;
        ActionButton.Content = "重新检查";
        CancelButton.Content = "取消检查";
        NetworkButton.IsEnabled = false;
        BrowserButton.Visibility = Visibility.Collapsed;
        Status.Text = "正在检查更新…";
        try
        {
            var progress = new Progress<string>(text => { if (checking && checkSequence == sequence) Status.Text = text; });
            available = await createClient().CheckAsync(UpdateCoordinator.CurrentVersion,
                UpdateCoordinator.Flavor ?? PackageFlavor.Small, active.Token, progress);
            Status.Text = available is null ? "已经是最新版本。" : $"发现新版本 {available.Version}\n\n{available.Notes}\n\n下载完成后将自动重启，原有便签会保留。";
            ActionButton.Content = available is null ? "重新检查" : "下载并重启";
            ActionButton.IsEnabled = available is null || UpdateCoordinator.Flavor is not null;
            if (available is not null && UpdateCoordinator.Flavor is null) Status.Text += "\n开发版本请先发布再使用应用内更新。";
        }
        catch (OperationCanceledException)
        {
            Status.Text = canceledByUser || cancellation.IsCancellationRequested ? "检查已取消，可以重新检查。"
                : "检查超过最长等待时间，请检查网络或系统代理后重试。";
            BrowserButton.Visibility = Visibility.Visible;
        }
        catch (UpdateCheckException ex)
        {
            Status.Text = ex.UserMessage;
            BrowserButton.Visibility = Visibility.Visible;
            UpdateCoordinator.WriteCheckLog(ex.Diagnostics);
        }
        catch (Exception ex)
        {
            Status.Text = "更新信息暂时无法读取，可以重新检查或用浏览器下载。当前版本可以继续使用。";
            BrowserButton.Visibility = Visibility.Visible;
            UpdateCoordinator.WriteCheckLog(ex.GetType().Name);
        }
        finally
        {
            checking = false; operation = null;
            NetworkButton.IsEnabled = true;
            CancelButton.Content = "关闭";
            ActionButton.IsEnabled = available is null || UpdateCoordinator.Flavor is not null;
        }
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        if (available is null) { await Check(); return; }
        ActionButton.IsEnabled = false;
        downloading = true;
        canceledByUser = false;
        NetworkButton.IsEnabled = false;
        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        operation = active;
        CancelButton.Content = "取消下载";
        DownloadProgress.Visibility = Visibility.Visible;
        try
        {
            var workspace = UpdateCoordinator.CreateWorkspace();
            var progress = new Progress<double>(value => { DownloadProgress.Value = value * 100; Status.Text = $"正在下载 {available.Version}，{value:P0}"; });
            await createClient().DownloadAsync(available, Path.Combine(workspace, "package.exe"), progress, active.Token);
            downloading = false;
            if (BeforeInstall?.Invoke() == false)
            {
                Status.Text = "更新文件已下载。请先处理便签的未保存修改，再重试更新。";
                return;
            }
            installing = true;
            CancelButton.IsEnabled = false;
            Status.Text = "正在准备安装，完成后将自动重启…";
            await UpdateCoordinator.StartHelper(workspace, available);
            readyToRestart = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        { Status.Text = canceledByUser || cancellation.IsCancellationRequested ? "下载已取消，可以重试。当前版本保持运行。" : "下载连接超时，请重试。当前版本保持运行。"; }
        catch (Exception ex) { Status.Text = "更新未完成，当前版本保持运行。\n\n" + ex.Message; }
        finally
        {
            downloading = false; installing = false;
            NetworkButton.IsEnabled = true;
            operation = null;
            CancelButton.IsEnabled = true; CancelButton.Content = "关闭";
            ActionButton.IsEnabled = !cancellation.IsCancellationRequested;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    { if (checking || downloading) { canceledByUser = true; operation?.Cancel(); } else Close(); }
    private void OnBrowserDownload(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(UpdateCoordinator.BrowserDownload) { UseShellExecute = true }); }
        catch (Win32Exception) { Status.Text = "无法打开浏览器，请在浏览器中访问本项目的 Releases 下载页。"; }
    }
    private async void OnNetworkSettings(object sender, RoutedEventArgs e)
    {
        if (checking || downloading || installing) return;
        if (new UpdateNetworkWindow { Owner = this }.ShowDialog() == true) await Check();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (installing && !readyToRestart) { e.Cancel = true; return; }
        cancellation.Cancel();
    }
}
