using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickNote.Core;

namespace QuickNote;

public partial class MainWindow
{
    private async Task CheckUpdateCancellation(string output)
    {
        using var handler = new UpdateCheckFixture();
        using var http = new HttpClient(handler);
        var update = new UpdateWindow(false, () => new UpdateClient(http)) { Owner = this };
        update.Show();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var first = update.Check();
        update.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await first;
        SmokeChecks.Assert(update.IsVisible && update.ActionButton.IsEnabled && update.Status.Text.Contains("取消"),
            "Canceling a check closed the window or disabled retry");
        handler.Stalled = false;
        await update.Check();
        SmokeChecks.Assert(update.Status.Text.Contains("最新版本") && update.ActionButton.IsEnabled,
            "The canceled check poisoned the next check");
        var network = new UpdateNetworkWindow { Owner = update };
        network.Show();
        network.Address.Text = "http://127.0.0.1:7890";
        network.CustomProxy.IsChecked = true;
        SmokeChecks.Assert(network.SystemProxy.IsChecked == false && network.Address.IsEnabled, "Custom proxy selection is not exclusive");
        network.SystemProxy.IsChecked = true;
        SmokeChecks.Assert(network.CustomProxy.IsChecked == false && !network.Address.IsEnabled, "Cannot restore system proxy mode");
        SmokeChecks.Snapshot(network, Path.Combine(output, "09-update-network.png"));
        network.Close();
        update.Close();
    }
}

internal sealed class UpdateCheckFixture : HttpMessageHandler
{
    internal bool Stalled { get; set; } = true;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        if (Stalled) await Task.Delay(Timeout.Infinite, cancellation);
        var version = UpdateCoordinator.CurrentVersion.ToString();
        var json = request.RequestUri!.Host == "api.github.com"
            ? JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "v" + version, draft = false, prerelease = false })
            : JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, version });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(json) };
    }
}
