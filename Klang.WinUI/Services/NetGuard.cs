using System;
using System.Linq;

namespace Klang_WinUI.Services;

/// <summary>
/// 对服务端返回的地址和字段做校验，避免把下载目标、缓存文件名完全交给响应内容决定。
/// </summary>
public static class NetGuard
{
    /// <summary>网易云的接口和 CDN 域名（音频流、封面都在这些域名下）。</summary>
    private static readonly string[] NeteaseHosts = { "126.net", "127.net", "163.com", "netease.com" };

    /// <summary>QQ 音乐的接口、音频 CDN 和封面图床域名。</summary>
    private static readonly string[] QQMusicHosts = { "qq.com", "gtimg.cn", "qpic.cn" };

    /// <summary>地址是 http / https，且主机属于 QQ 音乐（腾讯）的域名。</summary>
    public static bool IsQQMusicUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsQQMusicUri(uri);

    public static bool IsQQMusicUri(Uri? uri) =>
        uri != null
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && QQMusicHosts.Any(h => HostMatches(uri.Host, h));

    public static readonly string[] AudioExtensions = { "flac", "m4a", "mp3", "ogg" };

    /// <summary>地址是 http / https，且主机属于网易云的域名。</summary>
    public static bool IsNeteaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsNeteaseUri(uri);

    public static bool IsNeteaseUri(Uri? uri) =>
        uri != null
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && NeteaseHosts.Any(h => HostMatches(uri.Host, h));

    /// <summary>主机等于该域名，或是它的子域名。</summary>
    public static bool HostMatches(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把接口返回的音频类型规范成白名单里的扩展名；不认识的类型改看地址里的扩展名，都不行时返回 null。
    /// </summary>
    public static string? NormalizeAudioType(string? type, string? url)
    {
        var t = (type ?? "").Trim().ToLowerInvariant();
        if (t == "aac") t = "m4a";
        if (AudioExtensions.Contains(t)) return t;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var ext = System.IO.Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
            if (AudioExtensions.Contains(ext)) return ext;
        }
        return string.IsNullOrEmpty(t) ? "mp3" : null;
    }
}
