using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuickNote.Core;

public enum PackageFlavor { Small, Portable }
public sealed record UpdatePackage(Version Version, PackageFlavor Flavor, string Notes, Uri DownloadUri, long Size, string Sha256);

public sealed class UpdateClient
{
    public const string Repository = "fuweiwe1/QuickNote";
    public const long MaximumPackageSize = 256 * 1024 * 1024;
    private readonly HttpClient http;
    public UpdateClient(HttpClient http) => this.http = http;

    public async Task<UpdatePackage?> CheckAsync(Version current, PackageFlavor flavor, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("QuickNote/" + current);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var data = new MemoryStream();
        await using var input = await response.Content.ReadAsStreamAsync(cancellation);
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellation)) != 0)
        {
            if (data.Length + read > 1024 * 1024) throw new InvalidDataException("更新信息太大，请稍后再试。");
            data.Write(buffer, 0, read);
        }
        return ParseRelease(data.ToArray(), current, flavor);
    }

    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    public static UpdatePackage? ParseRelease(byte[] json, Version current, PackageFlavor flavor)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var parsed) || tag != "v" + Normalize(parsed))
            throw new InvalidDataException("更新版本号无法识别。");
        var version = Normalize(parsed);
        if (version <= Normalize(current)) return null;
        var name = $"QuickNote-win-x64-{flavor}.exe";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal))
                throw new InvalidDataException("更新文件尚未完成校验信息发布，请稍后重试。");
            var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!, UriKind.Absolute);
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            var package = new UpdatePackage(version, flavor, notes[..Math.Min(notes.Length, 16000)], uri,
                asset.GetProperty("size").GetInt64(), digest[7..]);
            Validate(package);
            return package;
        }
        throw new InvalidDataException("这个版本还没有适合当前应用的更新文件，请稍后重试。");
    }

    public static void Validate(UpdatePackage package)
    {
        var expected = $"https://github.com/{Repository}/releases/download/v{Normalize(package.Version)}/QuickNote-win-x64-{package.Flavor}.exe";
        if (!Enum.IsDefined(package.Flavor) || package.DownloadUri.AbsoluteUri != expected ||
            package.Size <= 0 || package.Size > MaximumPackageSize || !IsSha256(package.Sha256))
            throw new InvalidDataException("更新文件信息不完整或来源不正确。");
    }

    public static bool IsSha256(string value)
    {
        if (value.Length != 64) return false;
        foreach (var c in value) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    public async Task DownloadAsync(UpdatePackage package, string destination, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        Validate(package);
        var complete = false;
        var created = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, package.DownloadUri);
            request.Headers.UserAgent.ParseAdd("QuickNote/" + package.Version);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != package.Size)
                throw new InvalidDataException("下载文件大小不匹配，原程序未改变。");
            await using var input = await response.Content.ReadAsStreamAsync(cancellation);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                created = true;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellation)) != 0)
                {
                    total += read;
                    if (total > package.Size) throw new InvalidDataException("下载文件超出预期大小。");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    progress?.Report((double)total / package.Size);
                }
                if (total != package.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(package.Sha256)))
                    throw new InvalidDataException("更新文件校验失败，原程序未改变，请重试。");
                await output.FlushAsync(cancellation);
                output.Flush(flushToDisk: true);
            }
            complete = true;
        }
        finally { if (!complete && created && File.Exists(destination)) File.Delete(destination); }
    }
}
