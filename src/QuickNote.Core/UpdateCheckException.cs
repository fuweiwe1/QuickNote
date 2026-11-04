using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace QuickNote.Core;

public sealed record UpdateCheckIssue(string Source, Exception Error);
public sealed class UpdateCheckException : IOException
{
    public UpdateCheckIssue[] Issues { get; }
    public bool TimedOut => Issues.Any(issue => issue.Error is OperationCanceledException);
    public bool RateLimited => Issues.Any(issue => issue.Error is HttpRequestException request &&
        request.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests);
    public string Diagnostics => string.Join(Environment.NewLine, Issues.Select(issue =>
        issue.Source + ": " + issue.Error.GetType().Name + (issue.Error is HttpRequestException request
            ? " HTTP=" + request.StatusCode + " Kind=" + request.HttpRequestError : "")));

    public UpdateCheckException(UpdateCheckIssue[] issues) : base("暂时无法连接官方更新入口。") => Issues = issues;
    public string UserMessage => TimedOut
        ? "连接更新服务超时，已尝试两个官方入口并重试。请检查网络或系统代理后重试；也可用浏览器下载新版 EXE。"
        : RateLimited
            ? "更新服务暂时拒绝了请求，可能被限流或代理拦截。请稍后重试，或用浏览器下载新版 EXE。"
            : "两个官方更新入口均未能完成检查。请检查网络或系统代理后重试；当前版本可以继续使用。";
}
