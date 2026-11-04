using System;
using System.IO;
using System.Security.Cryptography;

namespace QuickNote.Core;

/// <summary>Only executable files are replaced; the notebook directory is never an update target.</summary>
public static class ExecutableUpdate
{
    public static void VerifyWindowsImage(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        if (file.Length < 256 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("更新文件不是可用的 Windows 程序。");
        file.Position = 0x3C;
        var offset = reader.ReadUInt32();
        if (offset < 64 || offset > file.Length - 26) throw new InvalidDataException("更新程序文件头无效。");
        file.Position = offset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664)
            throw new InvalidDataException("更新程序不是 Windows x64 版本。");
        file.Position = offset + 24;
        if (reader.ReadUInt16() != 0x020B) throw new InvalidDataException("更新程序格式无效。");
    }

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
