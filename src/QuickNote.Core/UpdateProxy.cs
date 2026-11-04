using System;

namespace QuickNote.Core;

public static class UpdateProxy
{
    public static string? Normalize(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks5") || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0 || uri.AbsolutePath is not ("" or "/") || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("请填写完整的 HTTP、HTTPS 或 SOCKS5 代理地址，例如 http://127.0.0.1:7890；地址中不要包含账号或密码。");
        return uri.AbsoluteUri;
    }
}
