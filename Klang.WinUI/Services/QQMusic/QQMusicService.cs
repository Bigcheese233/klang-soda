using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Klang_WinUI.Models;
using Klang_WinUI.Services.Cloud;

namespace Klang_WinUI.Services.QQMusic;

/// <summary>QQ 音乐来源：扫码登录（QQ / 微信）、我的歌单、雷达推荐、搜索、取音源与缓存、歌词。</summary>
public sealed class QQMusicService : ICloudSource
{
    public const string SourceKey = "qqmusic";

    private static readonly Lazy<QQMusicService> _instance = new(() => new QQMusicService());
    public static QQMusicService Instance => _instance.Value;

    private static AppState State => StateService.Instance.State;

    private readonly QQMusicClient _client = new();
    private readonly string _cacheDir;

    /// <summary>音质：Key、标签、文件名前缀、扩展名。从高到低排列，取不到时依次降级。</summary>
    private static readonly (string Key, string Label, string Prefix, string Ext)[] QualityList =
    {
        ("lossless", L.T("无损 FLAC"), "F000", "flac"),
        ("higher", L.T("高品质 320k"), "M800", "mp3"),
        ("standard", L.T("标准 128k"), "M500", "mp3"),
    };

    // 歌曲 mid → 媒体文件 mid（拼音频文件名要用）。搜索 / 歌单结果里带着，重启后恢复的曲目再单独查
    private readonly ConcurrentDictionary<string, string> _mediaMid = new();
    private readonly Dictionary<string, Task<bool>> _prefetching = new();

    private static readonly Regex MidRx = new("^[0-9A-Za-z]{6,32}$");

    private QQMusicService()
    {
        _cacheDir = Path.Combine(StateService.ResolveDataDirectory(), "qqmusic_cache");
        Directory.CreateDirectory(_cacheDir);
        RestoreSession();
    }

    public string Key => SourceKey;
    public string DisplayName => L.T("QQ 音乐");
    public bool IsTrustedUrl(string? url) => NetGuard.IsQQMusicUrl(url);

    #region 登录

    public bool IsLoggedIn => _client.HasCredential;
    public string Nickname => string.IsNullOrEmpty(State.QQMusicNickname) ? L.T("QQ 音乐用户") : State.QQMusicNickname;
    public event Action? LoginChanged;

    public IReadOnlyList<(string Key, string Label)> QrModes { get; } = new[] { ("wx", L.T("微信")), ("qq", "QQ") };
    public string QrHintFor(string mode) => mode == "qq" ? L.T("用手机 QQ 扫码登录") : L.T("用微信扫码登录");

    public bool SupportsCookieLogin => false;
    public string CookieHint => "";
    public Task<(bool Ok, string Message)> LoginWithCookieAsync(string input) =>
        Task.FromResult((false, L.T("不支持 Cookie 登录")));

    private void RestoreSession()
    {
        try
        {
            if (string.IsNullOrEmpty(State.QQMusicCredentialEnc)) return;
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(State.QQMusicCredentialEnc), null, DataProtectionScope.CurrentUser);
            _client.Credential = JsonNode.Parse(Encoding.UTF8.GetString(plain)) as JsonObject;
        }
        catch
        {
            State.QQMusicCredentialEnc = "";   // 解密失败（换了账户 / 数据损坏）按未登录处理
        }
    }

    private void SaveSession()
    {
        try
        {
            if (_client.HasCredential)
            {
                var bytes = Encoding.UTF8.GetBytes(_client.Credential!.ToJsonString());
                State.QQMusicCredentialEnc = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            }
            else
            {
                State.QQMusicCredentialEnc = "";
                State.QQMusicNickname = "";
            }
            StateService.Instance.RequestSave();
        }
        catch { }
    }

    /// <summary>启动后校验：快到期就刷新凭证，顺便更新昵称。网络失败保留本地登录态，只在服务端明确拒绝刷新时登出。</summary>
    public async Task VerifyLoginAsync()
    {
        if (!_client.HasCredential) return;
        try
        {
            var expires = _client.KeyExpiresAt;
            if (expires == null || expires.Value - DateTime.UtcNow < TimeSpan.FromHours(12))
            {
                if (!await _client.RefreshAsync())
                {
                    // 还没过期就先继续用；已经过期又刷新不了，只能重新登录
                    if (expires != null && expires.Value <= DateTime.UtcNow) { Logout(); return; }
                }
                else SaveSession();
            }
            var nick = await _client.GetNicknameAsync();
            if (nick != null && nick != State.QQMusicNickname)
            {
                State.QQMusicNickname = nick;
                StateService.Instance.RequestSave();
            }
            LoginChanged?.Invoke();
        }
        catch { }
    }

    public async Task<(string Key, byte[] Png)> CreateQrAsync(string mode)
    {
        try
        {
            return mode == "qq" ? await _client.CreateQqQrAsync() : await _client.CreateWxQrAsync();
        }
        catch
        {
            return ("", Array.Empty<byte>());
        }
    }

    public async Task<(QrLoginState State, string Message)> PollQrAsync(string key)
    {
        try
        {
            var (status, msg) = await _client.PollQrAsync(key);
            switch (status)
            {
                case QQMusicClient.QrStatus.Waiting: return (QrLoginState.Waiting, msg);
                case QQMusicClient.QrStatus.Scanned: return (QrLoginState.Scanned, msg);
                case QQMusicClient.QrStatus.Expired: return (QrLoginState.Expired, msg);
                case QQMusicClient.QrStatus.Refused: return (QrLoginState.Error, L.T("已在手机上取消登录"));
                case QQMusicClient.QrStatus.Done:
                    SaveSession();
                    try { State.QQMusicNickname = await _client.GetNicknameAsync() ?? ""; } catch { }
                    StateService.Instance.RequestSave();
                    LoginChanged?.Invoke();
                    return (QrLoginState.Success, L.T("登录成功"));
                default: return (QrLoginState.Error, msg);
            }
        }
        catch (Exception ex)
        {
            return (QrLoginState.Error, ex.Message);
        }
    }

    public void Logout()
    {
        _client.Credential = null;
        SaveSession();
        LoginChanged?.Invoke();
    }

    #endregion

    #region 歌单 / 推荐 / 搜索

    public bool HasDailyRecommend => true;
    public string DailyRecommendTitle => L.T("雷达推荐");

    public async Task<List<CloudPlaylist>> GetUserPlaylistsAsync()
    {
        var list = new List<CloudPlaylist>();
        if (!IsLoggedIn) return list;
        var r = await _client.CgiAsync("music.musicasset.PlaylistBaseRead", "GetPlaylistByUin",
            new JsonObject { ["uin"] = _client.MusicId.ToString() });
        if ((int?)r?["code"] != 0) return list;
        foreach (var p in r?["data"]?["v_playlist"]?.AsArray() ?? new JsonArray())
        {
            long tid = (long?)p?["tid"] ?? 0;
            if (p == null || tid == 0) continue;
            list.Add(new CloudPlaylist
            {
                Id = tid.ToString(),
                Name = (string?)p["dirName"] ?? L.T("未命名歌单"),
                TrackCount = (int?)p["songNum"] ?? 0,
                IsLiked = (int?)p["dirId"] == 201
            });
        }
        return list;
    }

    public async Task<List<TrackItem>> GetPlaylistTracksAsync(string playlistId)
    {
        if (!long.TryParse(playlistId, System.Globalization.NumberStyles.None, null, out var tid)) return new();
        var songs = new List<JsonNode>();
        const int page = 200;
        for (int begin = 0; begin < 1000; begin += page)
        {
            var r = await _client.CgiAsync("music.srfDissInfo.DissInfo", "CgiGetDiss", new JsonObject
            {
                ["disstid"] = tid, ["dirid"] = 0, ["tag"] = 0, ["song_begin"] = begin, ["song_num"] = page,
                ["userinfo"] = 0, ["orderlist"] = 1, ["onlysonglist"] = 1,
            });
            if ((int?)r?["code"] != 0) break;
            var batch = r?["data"]?["songlist"]?.AsArray();
            if (batch == null || batch.Count == 0) break;
            songs.AddRange(batch.Where(s => s != null)!);
            int total = (int?)r?["data"]?["total_song_num"] ?? 0;
            if (songs.Count >= total || batch.Count < page) break;
        }
        return ParseSongs(songs);
    }

    public async Task<List<TrackItem>> GetDailyRecommendAsync()
    {
        var r = await _client.CgiAsync("music.recommend.TrackRelationServer", "GetRadarSong", new JsonObject
        {
            ["Page"] = 1, ["ReqType"] = 0, ["FavSongs"] = new JsonArray(), ["EntranceSongs"] = new JsonArray(),
        });
        if ((int?)r?["code"] != 0) return new();
        var songs = (r?["data"]?["VecSongs"]?.AsArray() ?? new JsonArray())
            .Select(v => v?["Track"]).Where(t => t != null).ToList();
        return ParseSongs(songs!);
    }

    public async Task<List<TrackItem>> SearchAsync(string keyword, int limit = 50)
    {
        var kw = (keyword ?? "").Trim();
        if (kw.Length == 0) return new();
        // 搜索接口偶尔返回 2001（风控 / 频率），稍等重试一两次
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var r = await _client.CgiAsync("music.search.SearchCgiService", "DoSearchForQQMusicDesktop", new JsonObject
            {
                ["num_per_page"] = Math.Clamp(limit, 1, 60), ["page_num"] = 1, ["query"] = kw, ["search_type"] = 0,
                ["searchid"] = Random.Shared.NextInt64(1, long.MaxValue).ToString(), ["grp"] = 1,
            }, desktop: true);
            int code = (int?)r?["code"] ?? -1;
            if (code == 0)
            {
                var list = r?["data"]?["body"]?["song"]?["list"]?.AsArray();
                return list == null ? new() : ParseSongs(list.Where(s => s != null)!);
            }
            await Task.Delay(400 * (attempt + 1));
        }
        return new();
    }

    private List<TrackItem> ParseSongs(IEnumerable<JsonNode> songs)
    {
        var tracks = new List<TrackItem>();
        int index = 1;
        foreach (var s in songs)
        {
            string mid = (string?)s["mid"] ?? "";
            if (!MidRx.IsMatch(mid)) continue;
            var mediaMid = (string?)s["file"]?["media_mid"];
            if (!string.IsNullOrEmpty(mediaMid) && MidRx.IsMatch(mediaMid)) _mediaMid[mid] = mediaMid;

            string artist = string.Join(" / ", (s["singer"]?.AsArray() ?? new JsonArray())
                .Select(a => (string?)a?["name"]).Where(n => !string.IsNullOrEmpty(n)));
            string albumMid = (string?)s["album"]?["mid"] ?? "";
            string singerMid = (string?)s["singer"]?.AsArray().FirstOrDefault()?["mid"] ?? "";
            // 现场版、翻唱等常常没有专辑，这时用歌手照片当封面
            string coverUrl = MidRx.IsMatch(albumMid) ? $"https://y.gtimg.cn/music/photo_new/T002R500x500M000{albumMid}.jpg"
                : MidRx.IsMatch(singerMid) ? $"https://y.gtimg.cn/music/photo_new/T001R500x500M000{singerMid}.jpg" : "";
            string title = (string?)s["name"] ?? (string?)s["title"] ?? L.T("未知曲目");
            tracks.Add(MakeTrack(index++, mid, title, artist.Length > 0 ? artist : "未知歌手",
                (string?)s["album"]?["name"] ?? (string?)s["album"]?["title"] ?? "", (int?)s["interval"] ?? 0, coverUrl));
        }
        return tracks;
    }

    private TrackItem MakeTrack(int index, string mid, string title, string artist, string album, double duration, string coverUrl)
    {
        var t = new TrackItem
        {
            Index = index,
            Id = SourceKey + "_" + mid,
            CloudSource = SourceKey,
            CloudId = mid,
            Title = title,
            Artist = artist,
            Album = album,
            Duration = duration,
            CoverUrl = coverUrl,
            FormatBadge = L.T("云端")
        };
        var cached = FindCachedAudio(mid);
        if (cached != null)
        {
            t.FilePath = cached;
            t.FormatBadge = L.T("已缓存");
        }
        var cover = CachedCoverPath(mid);
        t.CoverPath = File.Exists(cover) ? cover : null;
        return t;
    }

    public List<TrackItem> FromSnapshots(IEnumerable<OnlineTrackSnapshot> list)
    {
        var tracks = new List<TrackItem>();
        int index = 1;
        foreach (var s in list)
        {
            if (!MidRx.IsMatch(s.CloudId)) continue;
            tracks.Add(MakeTrack(index++, s.CloudId, s.Title, s.Artist, s.Album, s.Duration, IsTrustedUrl(s.CoverUrl) ? s.CoverUrl : ""));
        }
        return tracks;
    }

    #endregion

    #region 歌词

    public async Task<LyricBundle> GetLyricsAsync(string cloudId, CancellationToken ct = default)
    {
        if (!MidRx.IsMatch(cloudId)) return LyricBundle.Empty;
        try
        {
            // crypt = 0：直接返回 base64 的普通 LRC，不需要解密
            var r = await _client.CgiAsync("music.musichallSong.PlayLyricInfo", "GetPlayLyricInfo", new JsonObject
            {
                ["songMid"] = cloudId, ["crypt"] = 0, ["lrc_t"] = 0, ["qrc"] = 0, ["qrc_t"] = 0,
                ["roma"] = 1, ["roma_t"] = 0, ["trans"] = 1, ["trans_t"] = 0, ["type"] = 0,
            }, ct: ct);
            if ((int?)r?["code"] != 0) return LyricBundle.Empty;
            var d = r?["data"];
            return new LyricBundle(Decode((string?)d?["lyric"]), Decode((string?)d?["trans"]), Decode((string?)d?["roma"]));
        }
        catch (OperationCanceledException) { throw; }
        catch { return LyricBundle.Empty; }
    }

    private static string Decode(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return "";
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch { return ""; }
    }

    #endregion

    #region 音源与缓存

    public IReadOnlyList<(string Key, string Label)> Qualities => QualityList.Select(q => (q.Key, q.Label)).ToArray();

    public string PreferredQuality
    {
        get => QualityList.Any(q => q.Key == State.QQMusicQuality) ? State.QQMusicQuality : "lossless";
        set
        {
            State.QQMusicQuality = value;
            StateService.Instance.RequestSave();
        }
    }

    public event Action<TrackItem>? CoverCached;

    private string CachedCoverPath(string mid) => Path.Combine(_cacheDir, $"{mid}_cover.jpg");

    private string? FindCachedAudio(string mid)
    {
        try
        {
            var pref = $"{mid}_{PreferredQuality}.";
            return Directory.EnumerateFiles(_cacheDir, $"{mid}_*.*")
                .Where(f => NetGuard.AudioExtensions.Contains(Path.GetExtension(f).TrimStart('.').ToLowerInvariant())
                            && new FileInfo(f).Length > 10240)
                .OrderBy(f => Path.GetFileName(f).StartsWith(pref) ? 0 : 1)
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private string CachePath(string mid, (string Key, string Label, string Prefix, string Ext) q) =>
        Path.Combine(_cacheDir, $"{mid}_{q.Key}.{q.Ext}");

    public bool IsCached(TrackItem track)
    {
        if (!track.IsOnline) return true;
        var q = QualityList.FirstOrDefault(x => x.Key == PreferredQuality);
        var p = CachePath(track.CloudId, q);
        try { return File.Exists(p) && new FileInfo(p).Length > 10240; } catch { return false; }
    }

    public async Task<bool> ResolveAsync(TrackItem track, CancellationToken ct)
    {
        if (!track.IsOnline) return !string.IsNullOrEmpty(track.FilePath);
        Task<bool>? pending;
        lock (_prefetching) _prefetching.TryGetValue(track.CloudId, out pending);
        if (pending != null)
        {
            try { await pending.WaitAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return await ResolveCoreAsync(track, ct);
    }

    public void Prefetch(TrackItem track)
    {
        if (!track.IsOnline || IsCached(track)) return;
        string mid = track.CloudId;
        lock (_prefetching)
        {
            if (_prefetching.ContainsKey(mid)) return;
            _prefetching[mid] = Task.Run(async () =>
            {
                try { return await ResolveCoreAsync(track, CancellationToken.None); }
                catch { return false; }
                finally { lock (_prefetching) _prefetching.Remove(mid); }
            });
        }
    }

    private async Task<string> GetMediaMidAsync(string mid, CancellationToken ct)
    {
        if (_mediaMid.TryGetValue(mid, out var m)) return m;
        try
        {
            var r = await _client.CgiAsync("music.trackInfo.UniformRuleCtrl", "CgiGetTrackInfo", new JsonObject
            {
                ["ctx"] = 0, ["client"] = 1, ["types"] = new JsonArray(0), ["modify_stamp"] = new JsonArray(0), ["mids"] = new JsonArray(mid),
            }, ct: ct);
            var mm = (string?)r?["data"]?["tracks"]?.AsArray().FirstOrDefault()?["file"]?["media_mid"];
            if (!string.IsNullOrEmpty(mm) && MidRx.IsMatch(mm)) { _mediaMid[mid] = mm; return mm; }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return mid;
    }

    private async Task<bool> ResolveCoreAsync(TrackItem track, CancellationToken ct)
    {
        var mid = track.CloudId;
        if (!MidRx.IsMatch(mid)) return false;
        _ = CacheCoverAsync(track);

        int start = Math.Max(0, Array.FindIndex(QualityList, q => q.Key == PreferredQuality));
        var preferred = QualityList[start];
        var p0 = CachePath(mid, preferred);
        if (File.Exists(p0) && new FileInfo(p0).Length > 10240)
        {
            Touch(p0);
            Apply(track, p0, preferred.Key);
            return true;
        }

        var mediaMid = await GetMediaMidAsync(mid, ct);
        for (int i = start; i < QualityList.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var q = QualityList[i];
            var final = CachePath(mid, q);
            if (File.Exists(final) && new FileInfo(final).Length > 10240)
            {
                Touch(final);
                Apply(track, final, q.Key);
                return true;
            }
            string? url = await GetStreamUrlAsync(mid, mediaMid, q.Prefix, q.Ext, ct);
            if (url == null) continue;
            if (await DownloadAsync(url, final, ct))
            {
                Apply(track, final, q.Key);
                EnforceCacheQuota();
                return true;
            }
        }
        return false;
    }

    private async Task<string?> GetStreamUrlAsync(string mid, string mediaMid, string prefix, string ext, CancellationToken ct)
    {
        try
        {
            var r = await _client.CgiAsync("music.vkey.GetVkey", "UrlGetVkey", new JsonObject
            {
                ["uin"] = _client.MusicId.ToString(),
                ["filename"] = new JsonArray($"{prefix}{mediaMid}.{ext}"),
                ["guid"] = Guid.NewGuid().ToString("N"),
                ["songmid"] = new JsonArray(mid),
                ["songtype"] = new JsonArray(0),
                ["ctx"] = 0,
            }, ct: ct);
            if ((int?)r?["code"] != 0) return null;
            var purl = (string?)r?["data"]?["midurlinfo"]?.AsArray().FirstOrDefault()?["purl"] ?? "";
            if (purl.Length == 0) return null;
            var sip = r?["data"]?["sip"]?.AsArray().Select(x => (string?)x)
                .FirstOrDefault(s => s != null && s.StartsWith("https://") && NetGuard.IsQQMusicUrl(s));
            var url = (sip ?? "https://isure.stream.qqmusic.qq.com/") + purl;
            // 地址只认 QQ 音乐（腾讯）的域名
            return NetGuard.IsQQMusicUrl(url) ? url : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<bool> DownloadAsync(string url, string final, CancellationToken ct)
    {
        var tmp = final + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            using var dl = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var rsp = await dl.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!rsp.IsSuccessStatusCode) return false;
            if (!NetGuard.IsQQMusicUri(rsp.RequestMessage?.RequestUri)) return false;   // 跟随重定向后也必须还在腾讯域名下
            long? expected = rsp.Content.Headers.ContentLength;
            long written = 0;
            await using (var src = await rsp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[64 * 1024];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    written += n;
                }
            }
            if (written <= 10240 || (expected.HasValue && written != expected.Value)) return false;
            File.Move(tmp, final, true);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static void Apply(TrackItem t, string path, string quality)
    {
        t.FilePath = path;
        switch (quality)
        {
            case "lossless": t.FormatBadge = L.F("{0} 无损", "FLAC"); t.BitDepth = 16; t.SampleRate = 44100; t.Bitrate = 0; break;
            case "higher": t.FormatBadge = "MP3 320k"; t.BitDepth = 16; t.SampleRate = 44100; t.Bitrate = 320; break;
            default: t.FormatBadge = L.F("{0} 标准", "MP3"); t.BitDepth = 16; t.SampleRate = 44100; t.Bitrate = 128; break;
        }
    }

    private async Task CacheCoverAsync(TrackItem track)
    {
        try
        {
            var p = CachedCoverPath(track.CloudId);
            if (File.Exists(p) && new FileInfo(p).Length > 1024)
            {
                track.CoverPath = p;
                return;
            }
            if (!IsTrustedUrl(track.CoverUrl)) return;
            var bytes = await _client.Http.GetByteArrayAsync(track.CoverUrl);
            if (bytes.Length <= 1024) return;
            var tmp = p + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, p, true);
            track.CoverPath = p;
            CoverCached?.Invoke(track);
        }
        catch { }
    }

    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    public (int Count, long Bytes) GetCacheInfo()
    {
        try
        {
            var files = new DirectoryInfo(_cacheDir).EnumerateFiles().ToList();
            return (files.Count(f => f.Extension != ".jpg"), files.Sum(f => f.Length));
        }
        catch { return (0, 0); }
    }

    public (int Files, long Bytes) ClearCache()
    {
        int n = 0;
        long bytes = 0;
        try
        {
            foreach (var f in new DirectoryInfo(_cacheDir).EnumerateFiles())
            {
                try { long len = f.Length; f.Delete(); n++; bytes += len; } catch { }
            }
        }
        catch { }
        return (n, bytes);
    }

    private void EnforceCacheQuota()
    {
        try
        {
            long limit = Math.Max(256, State.NeteaseCacheLimitMb) * 1024L * 1024L;
            var audio = new DirectoryInfo(_cacheDir).EnumerateFiles()
                .Where(f => f.Extension is ".flac" or ".m4a" or ".mp3" or ".ogg")
                .OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = audio.Sum(f => f.Length);
            foreach (var f in audio)
            {
                if (total <= limit) break;
                try { total -= f.Length; f.Delete(); } catch { }
            }
        }
        catch { }
    }

    #endregion
}
