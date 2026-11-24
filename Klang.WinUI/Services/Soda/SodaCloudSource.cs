using Klang_WinUI.Models;
using Klang_WinUI.Services.Cloud;

namespace Klang_WinUI.Services.Soda;

/// <summary>
/// 把汽水接入上游统一的播放、预缓冲、歌词和队列快照接口。
/// 不加入 CloudSources.All：汽水继续使用独立侧栏和官方 WebView2 登录，
/// 不能被通用云端面板的二维码轮询和 Cookie 输入流程调用。
/// </summary>
internal sealed class SodaCloudSource : ICloudSource
{
    public const string SourceKey = "soda";
    public static SodaCloudSource Instance { get; } = new();
    private SodaCloudSource() { }
    private static SodaService Service => SodaService.Instance;
    private static NotSupportedException SidebarOnly() => new("请从左侧汽水音乐入口使用此功能");

    public string Key => SourceKey;
    public string DisplayName => L.T("汽水音乐");
    public bool IsLoggedIn => Service.IsLoggedIn;
    public string Nickname => Service.Nickname;
    public event Action? LoginChanged
    {
        add => Service.LoginChanged += value;
        remove => Service.LoginChanged -= value;
    }
    public event Action<TrackItem>? CoverCached
    {
        add => Service.CoverCached += value;
        remove => Service.CoverCached -= value;
    }
    public Task VerifyLoginAsync() => Service.VerifyLoginAsync();
    public void Logout() => Service.Logout();
    public Task<bool> ResolveAsync(TrackItem track, CancellationToken ct) => Service.ResolveAsync(track, ct);
    public bool IsCached(TrackItem track) => Service.IsCached(track);
    public void Prefetch(TrackItem track) => Service.Prefetch(track);

    public async Task<LyricBundle> GetLyricsAsync(string cloudId, CancellationToken ct = default)
    {
        // 歌词失败不应阻断可播放的音频，也不留下并行任务的未观察异常。
        try
        {
            var lyrics = await Service.GetLyricsAsync(cloudId, ct);
            return new(lyrics.Lrc, lyrics.Trans, lyrics.Roma);
        }
        catch { return LyricBundle.Empty; }
    }

    public bool IsTrustedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && SodaHttp.IsMediaHostAllowed(uri.Host);

    public List<TrackItem> FromSnapshots(IEnumerable<OnlineTrackSnapshot> list) => list
        .Where(s => s.Source == SourceKey && s.CloudId.Length is > 0 and <= 128
            && s.CloudId.All(c => c is >= '0' and <= '9'))
        .Select(s => new TrackItem
        {
            Id = "soda_" + s.CloudId, SodaId = s.CloudId,
            Title = s.Title, Artist = s.Artist, Album = s.Album,
            Duration = s.Duration, CoverUrl = IsTrustedUrl(s.CoverUrl) ? s.CoverUrl : "",
            FormatBadge = "",
        }).ToList();

    // 以下能力只属于通用云端面板。汽水的模式、收藏与登录由 SodaPanel 提供。
    public IReadOnlyList<(string Key, string Label)> QrModes => Array.Empty<(string, string)>();
    public string QrHintFor(string mode) => L.T("请从左侧汽水音乐入口使用此功能");
    public Task<(string Key, byte[] Png)> CreateQrAsync(string mode) => throw SidebarOnly();
    public Task<(QrLoginState State, string Message)> PollQrAsync(string key) => throw SidebarOnly();
    public bool SupportsCookieLogin => false;
    public string CookieHint => "";
    public Task<(bool Ok, string Message)> LoginWithCookieAsync(string input) => throw SidebarOnly();
    public bool HasDailyRecommend => false;
    public string DailyRecommendTitle => L.T("推荐");
    public Task<List<TrackItem>> GetDailyRecommendAsync() => throw SidebarOnly();
    public Task<List<CloudPlaylist>> GetUserPlaylistsAsync() => throw SidebarOnly();
    public Task<List<TrackItem>> GetPlaylistTracksAsync(string playlistId) => throw SidebarOnly();
    public Task<List<TrackItem>> SearchAsync(string keyword, int limit = 50) => throw SidebarOnly();
    public IReadOnlyList<(string Key, string Label)> Qualities => Array.Empty<(string, string)>();
    public string PreferredQuality { get => "auto"; set => throw SidebarOnly(); }
    public (int Count, long Bytes) GetCacheInfo() => throw SidebarOnly();
    public (int Files, long Bytes) ClearCache() => throw SidebarOnly();
}
