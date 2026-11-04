using System;
using System.IO;
using System.Security.Cryptography;

namespace QuickNote.Core;

/// <summary>Only executable files are replaced; the notebook directory is never an update target.</summary>
public static class ExecutableUpdate
{
    public static void Verify(string path, long size, string sha256)
    {
        if (!UpdateClient.IsSha256(sha256) || size <= 0 || size > UpdateClient.MaximumPackageSize)
            throw new InvalidDataException("更新文件校验信息无效。");
        using var input = File.OpenRead(path);
        if (input.Length != size || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(input), Convert.FromHexString(sha256)))
            throw new InvalidDataException("更新文件校验失败，原程序未改变。");
    }

    public static void Apply(string staged, string target, long size, string sha256, Action<string>? trace = null)
    {
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target) ||
            Path.GetFullPath(staged).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("无法更新此程序路径。");
        trace?.Invoke("verifying staged file");
        Verify(staged, size, sha256);
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!, ".QuickNote-new-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            trace?.Invoke("copying replacement");
            using (var source = File.OpenRead(staged))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { source.CopyTo(output); output.Flush(flushToDisk: true); }
            trace?.Invoke("verifying replacement");
            Verify(temporary, size, sha256);
            trace?.Invoke("replacing executable");
            File.Replace(temporary, target, target + ".previous", ignoreMetadataErrors: true);
            trace?.Invoke("replacement committed");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Rollback(string target)
    {
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("无法恢复此程序路径。");
        File.Replace(target + ".previous", target, target + ".failed", ignoreMetadataErrors: true);
    }
}
