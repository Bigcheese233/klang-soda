using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Klang_WinUI.Models;
using Klang_WinUI.Services.Cloud;
using QRCoder;

namespace Klang_WinUI.Services.Netease;

/// <summary>网易云业务层（对应 Qt 版 netease_service）：登录、歌单、搜索、取音源与缓存、歌词。</summary>
public sealed class NeteaseService : ICloudSource
{
    private static readonly Lazy<NeteaseService> _instance = new(() => new NeteaseService());
    public static NeteaseService Instance => _instance.Value;

    public string Key => CloudSources.NeteaseKey;
    public string DisplayName => L.T("网易云音乐");
    public IReadOnlyList<(string Key, string Label)> QrModes { get; } = new[] { ("app", "") };
    public string QrHintFor(string mode) => L.T("用网易云音乐 App 扫码登录");
    public string DailyRecommendTitle => L.T("每日推荐");
    public bool SupportsCookieLogin => true;
    public string CookieHint => L.T("粘贴 MUSIC_U 或整段 Cookie");
    public bool HasDailyRecommend => true;
    public bool IsTrustedUrl(string? url) => NetGuard.IsNeteaseUrl(url);

    public IReadOnlyList<(string Key, string Label)> Qualities => QualityList;

    private static readonly (string Key, string Label)[] QualityList =
    {
        ("standard", L.T("标准 128k")),
        ("higher", L.T("极高 320k")),
        ("lossless", L.T("无损 FLAC")),
        ("hires", L.T("Hi-Res 无损")),
        ("jymaster", L.T("超清母带")),
    };

    private static readonly string[] LevelOrder = { "jymaster", "hires", "lossless", "higher", "standard" };

    private readonly NeteaseClient _client = new();
    private readonly string _cacheDir;
    private static AppState State => StateService.Instance.State;

    public bool IsLoggedIn { get; private set; }
    public string Nickname => State.NeteaseNickname;
    public long UserId => State.NeteaseUserId;
    public string AvatarUrl => State.NeteaseAvatarUrl;

    /// <summary>登录状态变化（UI 线程之外的线程也可能触发，订阅方自行切线程）。</summary>
    public event Action? LoginChanged;

    public string PreferredQuality
    {
        get => QualityList.Any(q => q.Key == State.NeteaseQuality) ? State.NeteaseQuality : "lossless";
        set
        {
            State.NeteaseQuality = value;
            StateService.Instance.RequestSave();
        }
    }

    private NeteaseService()
    {
        _cacheDir = Path.Combine(
            StateService.ResolveDataDirectory(), "netease_cache");
        Directory.CreateDirectory(_cacheDir);
        RestoreSession();
    }

    #region 会话持久化（DPAPI 加密 Cookie）

    private void RestoreSession()
    {
        try
        {
            if (string.IsNullOrEmpty(State.NeteaseCookiesEnc)) return;
            var blob = Convert.FromBase64String(State.NeteaseCookiesEnc);
            var plain = ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain));
            if (dict != null && dict.ContainsKey("MUSIC_U"))
            {
                _client.SetCookies(dict);
                IsLoggedIn = State.NeteaseUserId != 0;
            }
        }
        catch
        {
            // 解密失败（换了账户 / 数据损坏）按未登录处理
            State.NeteaseCookiesEnc = "";
        }
    }

    private void SaveSession()
    {
        try
        {
            if (IsLoggedIn)
            {
                var json = JsonSerializer.Serialize(_client.GetCookies());
                var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
                State.NeteaseCookiesEnc = Convert.ToBase64String(blob);
            }
            else
            {
                State.NeteaseCookiesEnc = "";
                State.NeteaseUserId = 0;
                State.NeteaseNickname = "";
                State.NeteaseAvatarUrl = "";
            }
            StateService.Instance.RequestSave();
        }
        catch { }
    }

    #endregion

    #region 登录

    /// <summary>启动后静默校验登录状态；网络失败时保留本地登录态，只在服务端明确拒绝时登出。</summary>
    public async Task VerifyLoginAsync()
    {
        if (!_client.HasMusicU) return;
        try
        {
            var res = await _client.WeapiAsync("/weapi/w/nuser/account/get");
            int code = (int?)res?["code"] ?? 0;
            var profile = res?["profile"];
            if (code == 200 && profile != null)
            {
                ApplyProfile(profile, res?["account"]);
                SaveSession();
                LoginChanged?.Invoke();
            }
            else if (IsLoggedIn && code is 301 or 400 or 401 or 403)
            {
                Logout();
            }
        }
        catch { }
    }

    private void ApplyProfile(JsonNode profile, JsonNode? account)
    {
        State.NeteaseUserId = (long?)profile["userId"] ?? (long?)account?["id"] ?? 0;
        State.NeteaseNickname = (string?)profile["nickname"] ?? L.T("网易云用户");
        State.NeteaseAvatarUrl = (string?)profile["avatarUrl"] ?? "";
        IsLoggedIn = State.NeteaseUserId != 0;
    }

    /// <summary>申请二维码；返回 unikey 与 PNG 字节。</summary>
    public async Task<(string Key, byte[] Png)> CreateQrAsync(string mode)
    {
        if (!IsLoggedIn) _client.ResetCookies();
        var res = await _client.WeapiAsync("/weapi/login/qrcode/unikey", new JsonObject { ["type"] = 1 });
        var key = (string?)res?["unikey"] ?? "";
        if (key.Length == 0) return ("", Array.Empty<byte>());

        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode($"https://music.163.com/login?codekey={key}", QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8,
            new byte[] { 0x18, 0x1B, 0x1E, 0xFF }, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        return (key, png);
    }

    /// <summary>轮询扫码状态。网易云的返回码：800 过期，801 等待扫码，802 待确认，803 成功。</summary>
    public async Task<(QrLoginState State, string Message)> PollQrAsync(string key)
    {
        try
        {
            var res = await _client.WeapiAsync("/weapi/login/qrcode/client/login",
                new JsonObject { ["key"] = key, ["type"] = 1 });
            int code = (int?)res?["code"] ?? -1;
            string msg = (string?)res?["message"] ?? "";
            switch (code)
            {
                case 801: return (QrLoginState.Waiting, msg);
                case 802: return (QrLoginState.Scanned, msg);
                case 800: return (QrLoginState.Expired, msg);
                case 803:
                    var raw = (string?)res?["cookie"] ?? "";
                    if (raw.Length > 0) _client.SetCookies(NeteaseClient.ParseCookieString(raw));
                    if (!await FinishLoginAsync()) return (QrLoginState.Error, L.T("登录校验失败"));
                    return (QrLoginState.Success, L.T("登录成功"));
                default: return (QrLoginState.Error, msg);
            }
        }
        catch (Exception ex)
        {
            return (QrLoginState.Error, ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> LoginWithCookieAsync(string input)
    {
        var raw = (input ?? "").Trim();
        if (raw.Length == 0) return (false, L.T("请输入 Cookie 或 MUSIC_U"));
        if (!raw.Contains('=')) raw = "MUSIC_U=" + raw;
        var dict = NeteaseClient.ParseCookieString(raw);
        if (!dict.ContainsKey("MUSIC_U")) return (false, L.T("未找到 MUSIC_U"));

        _client.ResetCookies();
        _client.SetCookies(dict);
        try
        {
            return await FinishLoginAsync() ? (true, L.T("登录成功")) : (false, L.T("Cookie 无效或已过期"));
        }
        catch (Exception ex)
        {
            return (false, L.T("验证失败：") + ex.Message);
        }
    }

    private async Task<bool> FinishLoginAsync()
    {
        var res = await _client.WeapiAsync("/weapi/w/nuser/account/get");
        if ((int?)res?["code"] == 200 && res?["profile"] != null)
        {
            ApplyProfile(res["profile"]!, res["account"]);
            SaveSession();
            LoginChanged?.Invoke();
            return true;
        }
        return false;
    }

    public void Logout()
    {
        _client.ResetCookies();
        IsLoggedIn = false;
        SaveSession();
        LoginChanged?.Invoke();
    }

    #endregion

    #region 歌单 / 推荐 / 搜索

    public async Task<List<CloudPlaylist>> GetUserPlaylistsAsync()
    {
        var list = new List<CloudPlaylist>();
        if (!IsLoggedIn) return list;
        var res = await _client.WeapiAsync("/weapi/user/playlist", new JsonObject
        {
            ["offset"] = "0", ["limit"] = "200", ["uid"] = State.NeteaseUserId.ToString()
        });
        if ((int?)res?["code"] != 200) return list;
        foreach (var pl in res!["playlist"]?.AsArray() ?? new JsonArray())
        {
            if (pl == null) continue;
            long id = (long?)pl["id"] ?? 0;
            if (id == 0) continue;
            list.Add(new CloudPlaylist
            {
                Id = id.ToString(),
                Name = (string?)pl["name"] ?? L.T("未命名歌单"),
                TrackCount = (int?)pl["trackCount"] ?? 0,
                IsLiked = (int?)pl["specialType"] == 5
            });
        }
        return list;
    }

    public async Task<List<TrackItem>> GetDailyRecommendAsync()
    {
        var res = await _client.WeapiAsync("/weapi/v3/discovery/recommend/songs");
        if ((int?)res?["code"] != 200) return new();
        return ParseSongs(res!["data"]?["dailySongs"]?.AsArray());
    }

    public async Task<List<TrackItem>> GetPlaylistTracksAsync(string playlistId)
    {
        if (!long.TryParse(playlistId, System.Globalization.NumberStyles.None, null, out var plId)) return new();
        var res = await _client.WeapiAsync("/weapi/v6/playlist/detail", new JsonObject
        {
            ["id"] = plId.ToString(), ["offset"] = "0", ["total"] = "true", ["limit"] = "1000"
        });
        if ((int?)res?["code"] != 200) return new();
        var pl = res!["playlist"];
        var ids = (pl?["trackIds"]?.AsArray() ?? new JsonArray())
            .Select(t => (long?)t?["id"] ?? 0).Where(i => i != 0).Take(1000).ToList();

        var songs = new JsonArray();
        for (int i = 0; i < ids.Count; i += 100)
        {
            var detail = await GetTrackDetailAsync(ids.Skip(i).Take(100));
            foreach (var s in detail?["songs"]?.AsArray() ?? new JsonArray())
                songs.Add(s?.DeepClone());
        }
        return songs.Count > 0 ? ParseSongs(songs) : ParseSongs(pl?["tracks"]?.AsArray());
    }

    private Task<JsonNode?> GetTrackDetailAsync(IEnumerable<long> ids)
    {
        var c = new JsonArray();
        foreach (var id in ids) c.Add(new JsonObject { ["id"] = id.ToString() });
        return _client.WeapiAsync("/weapi/v3/song/detail", new JsonObject { ["c"] = c.ToJsonString() });
    }

    public async Task<List<TrackItem>> SearchAsync(string keyword, int limit = 50)
    {
        var kw = (keyword ?? "").Trim();
        if (kw.Length == 0) return new();
        var res = await _client.WeapiAsync("/weapi/search/get", new JsonObject
        {
            ["s"] = kw, ["type"] = "1", ["limit"] = limit.ToString(), ["offset"] = "0"
        });
        if ((int?)res?["code"] != 200) return new();
        var songs = res!["result"]?["songs"]?.AsArray();
        if (songs == null || songs.Count == 0) return new();

        // 搜索结果缺少专辑封面，补一次详情
        try
        {
            var ids = songs.Select(s => (long?)s?["id"] ?? 0).Where(i => i != 0).ToList();
            var detail = await GetTrackDetailAsync(ids);
            var map = (detail?["songs"]?.AsArray() ?? new JsonArray())
                .Where(d => d != null).ToDictionary(d => (long?)d!["id"] ?? 0, d => d!);
            foreach (var s in songs)
            {
                if (s is JsonObject obj && map.TryGetValue((long?)obj["id"] ?? 0, out var d))
                {
                    if (d["al"] != null) obj["al"] = d["al"]!.DeepClone();
                    if (d["ar"] != null) obj["ar"] = d["ar"]!.DeepClone();
                    if (d["dt"] != null) obj["dt"] = d["dt"]!.DeepClone();
                }
            }
        }
        catch { }
        return ParseSongs(songs);
    }

    private List<TrackItem> ParseSongs(JsonArray? songs)
    {
        var tracks = new List<TrackItem>();
        if (songs == null) return tracks;
        int index = 1;
        foreach (var s in songs)
        {
            if (s == null) continue;
            string id = ((long?)s["id"] ?? 0).ToString();
            if (id == "0") continue;

            var artists = (s["ar"] ?? s["artists"])?.AsArray() ?? new JsonArray();
            string artist = string.Join(" / ", artists
                .Select(a => (string?)a?["name"]).Where(n => !string.IsNullOrEmpty(n)));
            var album = s["al"] ?? s["album"];
            double ms = (double?)(s["dt"] ?? s["duration"]) ?? 0;

            var t = MakeTrack(index++, id, (string?)s["name"] ?? L.T("未知曲目"),
                artist.Length > 0 ? artist : "未知歌手", (string?)album?["name"] ?? "", ms / 1000.0,
                (string?)album?["picUrl"] ?? (string?)s["picUrl"] ?? "");
            // 歌曲详情 / 歌单 / 推荐里是 mv，搜索结果里是 mvid；没有 MV 时是 0
            long mv = (long?)(s["mv"] ?? s["mvid"]) ?? 0;
            t.MvId = mv.ToString();
            tracks.Add(t);
        }
        return tracks;
    }

    private TrackItem MakeTrack(int index, string id, string title, string artist, string album, double duration, string coverUrl)
    {
        var t = new TrackItem
        {
            Index = index,
            Id = "netease_" + id,   // 和早期版本保持一致：歌词偏移、自定义封面等都按这个 Id 记录
            CloudSource = Key,
            CloudId = id,
            Title = title,
            Artist = artist,
            Album = album,
            Duration = duration,
            CoverUrl = coverUrl,
            FormatBadge = L.T("云端")
        };
        var cached = FindCachedAudio(id);
        if (cached != null)
        {
            t.FilePath = cached;
            t.FormatBadge = L.T("已缓存");
        }
        var cover = CachedCoverPath(id);
        t.CoverPath = File.Exists(cover) ? cover : null;
        return t;
    }

    /// <summary>从快照还原云端曲目；快照里的 id 不是纯数字时跳过。</summary>
    public List<TrackItem> FromSnapshots(IEnumerable<OnlineTrackSnapshot> list)
    {
        var tracks = new List<TrackItem>();
        int index = 1;
        foreach (var s in list)
        {
            if (!long.TryParse(s.CloudId, System.Globalization.NumberStyles.None, null, out var n) || n == 0) continue;
            tracks.Add(MakeTrack(index++, s.CloudId, s.Title, s.Artist, s.Album, s.Duration, s.CoverUrl));
        }
        return tracks;
    }

    #endregion

    #region 音源解析与缓存

    private string CachedCoverPath(string id) => Path.Combine(_cacheDir, $"{id}_cover.jpg");

    private string? FindCachedAudio(string id)
    {
        try
        {
            var pref = $"{id}_{PreferredQuality}.";
            return Directory.EnumerateFiles(_cacheDir, $"{id}_*.*")
                .Where(f => new[] { ".flac", ".m4a", ".mp3", ".ogg" }.Contains(Path.GetExtension(f).ToLowerInvariant())
                            && new FileInfo(f).Length > 10240)
                .OrderBy(f => Path.GetFileName(f).StartsWith(pref) ? 0 : 1)
                .ThenByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private async Task<(string Url, string Type, int Br, string Level)?> GetStreamInfoAsync(
        string id, string quality, CancellationToken ct)
    {
        int start = Array.IndexOf(LevelOrder, quality);
        var levels = start >= 0 ? LevelOrder.Skip(start).ToList() : new List<string> { "lossless", "higher", "standard" };
        foreach (var lvl in levels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var res = await _client.WeapiAsync("/weapi/song/enhance/player/url/v1", new JsonObject
                {
                    ["ids"] = new JsonArray(id),
                    ["level"] = lvl,
                    ["encodeType"] = "aac"
                }, ct);
                var item = res?["data"]?.AsArray().FirstOrDefault();
                var url = (string?)item?["url"] ?? "";
                // 地址只认网易云的域名；类型和音质会拼进缓存文件名，只接受白名单里的值
                if ((int?)res?["code"] == 200 && NetGuard.IsNeteaseUrl(url)
                    && NetGuard.NormalizeAudioType((string?)item?["type"], url) is { } type)
                {
                    // 接口还会返回 jyeffect、sky 等不在列表里的音质名，只要求是短的小写字母 / 数字
                    var level = (string?)item?["level"];
                    if (level == null || level.Length is 0 or > 16 || !level.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9'))
                        level = lvl;
                    return (url, type, (int?)item?["br"] ?? 0, level);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 为云端曲目取得可播放的本地文件（下载并缓存）。成功后 track.FilePath / CoverPath / 音质信息已更新。
    /// </summary>
    public async Task<bool> ResolveAsync(TrackItem track, CancellationToken ct)
    {
        if (!track.IsOnline) return !string.IsNullOrEmpty(track.FilePath);

        // 这首歌正在预缓冲：等它下完直接用，不再另开一次下载
        Task<bool>? pending;
        lock (_prefetching) _prefetching.TryGetValue(track.CloudId, out pending);
        if (pending != null)
        {
            try { await pending.WaitAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { }
            // 下完后走下面的常规流程：命中缓存，立即返回，并把路径 / 音质写到这个曲目对象上
        }
        return await ResolveCoreAsync(track, ct);
    }

    private readonly Dictionary<string, Task<bool>> _prefetching = new();

    /// <summary>这首云端曲目的音源是否已在本地缓存（按当前首选音质）。</summary>
    public bool IsCached(TrackItem track)
    {
        if (!track.IsOnline) return true;
        string quality = PreferredQuality;
        foreach (var ext in new[] { "flac", "m4a", "mp3", "ogg" })
        {
            var p = Path.Combine(_cacheDir, $"{track.CloudId}_{quality}.{ext}");
            try { if (File.Exists(p) && new FileInfo(p).Length > 10240) return true; } catch { }
        }
        return false;
    }

    /// <summary>
    /// 预缓冲：在后台把下一首云端曲目下载到缓存，切过去时即可直接播放。
    /// 已缓存或正在下载时什么都不做；下载不随切歌取消（下完就留在缓存里）。
    /// </summary>
    public void Prefetch(TrackItem track)
    {
        if (!track.IsOnline || string.IsNullOrEmpty(track.CloudId) || IsCached(track)) return;
        string id = track.CloudId;
        lock (_prefetching)
        {
            if (_prefetching.ContainsKey(id)) return;
            var task = Task.Run(async () =>
            {
                try { return await ResolveCoreAsync(track, CancellationToken.None); }
                catch { return false; }
                finally { lock (_prefetching) _prefetching.Remove(id); }
            });
            _prefetching[id] = task;
        }
    }

    private async Task<bool> ResolveCoreAsync(TrackItem track, CancellationToken ct)
    {
        var id = track.CloudId;
        // id 会拼进缓存文件名，只接受纯数字（也可能来自保存的状态文件）
        if (!long.TryParse(id, System.Globalization.NumberStyles.None, null, out _)) return false;

        // 封面先行（失败不影响播放）
        _ = CacheCoverAsync(track);

        string quality = PreferredQuality;
        foreach (var ext in NetGuard.AudioExtensions)
        {
            var p = Path.Combine(_cacheDir, $"{id}_{quality}.{ext}");
            if (File.Exists(p) && new FileInfo(p).Length > 10240)
            {
                Touch(p);
                Apply(track, p, quality, ext, 0);
                return true;
            }
        }

        var info = await GetStreamInfoAsync(id, quality, ct);
        if (info == null) return false;
        var (url, type, br, level) = info.Value;

        var final = Path.Combine(_cacheDir, $"{id}_{level}.{type}");
        if (File.Exists(final) && new FileInfo(final).Length > 10240)
        {
            Touch(final);
            Apply(track, final, level, type, br);
            return true;
        }

        var tmp = final + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
            using var dl = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var rsp = await dl.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!rsp.IsSuccessStatusCode) return false;
            // 跟随重定向后也必须还在网易云的域名下
            if (!NetGuard.IsNeteaseUri(rsp.RequestMessage?.RequestUri)) return false;
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
            // 被截断的下载不能当成有效缓存
            if (written <= 10240 || (expected.HasValue && written != expected.Value)) return false;
            File.Move(tmp, final, true);
            Apply(track, final, level, type, br);
            EnforceCacheQuota();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static void Apply(TrackItem t, string path, string quality, string ext, int br)
    {
        t.FilePath = path;
        if (br > 0) t.Bitrate = br / 1000;
        string e = ext.ToUpperInvariant();
        switch (quality)
        {
            case "jymaster": t.FormatBadge = L.T("FLAC 超清母带"); t.BitDepth = 24; t.SampleRate = 96000; break;
            case "hires": t.FormatBadge = "FLAC Hi-Res"; t.BitDepth = 24; t.SampleRate = 96000; break;
            case "lossless": t.FormatBadge = L.F("{0} 无损", e); t.BitDepth = 16; t.SampleRate = 44100; break;
            case "higher": t.FormatBadge = $"{e} 320k"; t.BitDepth = 16; t.SampleRate = 44100; break;
            default: t.FormatBadge = L.F("{0} 标准", e); t.BitDepth = 16; t.SampleRate = 44100; break;
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
            if (!NetGuard.IsNeteaseUrl(track.CoverUrl)) return;
            var url = track.CoverUrl.Contains('?') ? track.CoverUrl : track.CoverUrl + "?param=400y400";
            var bytes = await _client.Http.GetByteArrayAsync(url);
            if (bytes.Length <= 1024) return;
            var tmp = p + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, p, true);
            track.CoverPath = p;
            CoverCached?.Invoke(track);
        }
        catch { }
    }

    /// <summary>封面下载完成（可能在后台线程触发）。</summary>
    public event Action<TrackItem>? CoverCached;

    #region MV

    public bool SupportsMv => true;

    public async Task EnsureMvIdAsync(TrackItem track, CancellationToken ct = default)
    {
        if (track.MvId.Length > 0) return;
        if (!long.TryParse(track.CloudId, System.Globalization.NumberStyles.None, null, out var songId) || songId == 0) return;
        try
        {
            var det = await _client.WeapiAsync("/weapi/v3/song/detail", new JsonObject { ["c"] = $"[{{\"id\":{songId}}}]" }, ct);
            if ((int?)det?["code"] != 200) return;   // 查询失败时保持“不知道”，下次再查
            long found = (long?)det?["songs"]?[0]?["mv"] ?? 0;
            track.MvId = found.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    /// <summary>
    /// 这首歌的 MV：可选画质和指定画质的播放地址（只认网易云的域名）。没有 MV 时返回 null。
    /// 歌曲对象里没有 MV 编号时（比如从保存的队列恢复的）先查一次歌曲详情。
    /// </summary>
    public async Task<MvStream?> GetMvAsync(TrackItem track, int preferredHeight, CancellationToken ct = default)
    {
        await EnsureMvIdAsync(track, ct);
        if (!long.TryParse(track.MvId, System.Globalization.NumberStyles.None, null, out var mvId) || mvId == 0) return null;

        var info = await _client.WeapiAsync("/weapi/v1/mv/detail", new JsonObject { ["id"] = mvId }, ct);
        var heights = (info?["data"]?["brs"]?.AsArray() ?? new JsonArray())
            .Select(b => (int?)b?["br"] ?? 0).Where(h => h is > 0 and <= 4320).Distinct().OrderByDescending(h => h).ToList();
        string title = (string?)info?["data"]?["name"] ?? track.Title;

        // 选不超过偏好的最高画质；没有就用最低的
        int want = heights.FirstOrDefault(h => h <= preferredHeight);
        if (want == 0) want = heights.Count > 0 ? heights[^1] : preferredHeight;
        var res = await _client.WeapiAsync("/weapi/song/enhance/play/mv/url", new JsonObject { ["id"] = mvId, ["r"] = want }, ct);
        string? url = (string?)res?["data"]?["url"];
        if ((int?)res?["code"] != 200 || !NetGuard.IsNeteaseUrl(url)) return null;
        int actual = (int?)res?["data"]?["r"] ?? want;
        // 未登录等情况下接口只给低画质：列表里只保留实际拿得到的及以下
        if (heights.Count == 0) heights.Add(actual);
        return new MvStream(url!, actual, heights, title);
    }

    #endregion

    #region 动态封面

    private const long MaxDynamicCoverBytes = 40L * 1024 * 1024;
    // 本次运行中确认过没有动态封面的歌曲，不再重复请求
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _noDynamicCover = new();

    /// <summary>网易云的动态封面（需要登录）：取到视频地址后下载到缓存，返回本地 mp4 路径。</summary>
    public async Task<string?> GetDynamicCoverAsync(string cloudId, CancellationToken ct = default)
    {
        if (!long.TryParse(cloudId, System.Globalization.NumberStyles.None, null, out var songId) || songId == 0) return null;
        var path = Path.Combine(_cacheDir, $"{cloudId}_dyncover.mp4");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 10240) { Touch(path); return path; }
        }
        catch { }
        if (!IsLoggedIn || _noDynamicCover.ContainsKey(cloudId)) return null;

        string? url;
        try
        {
            var res = await _client.WeapiAsync("/weapi/songplay/dynamic-cover", new JsonObject { ["songId"] = songId }, ct);
            if ((int?)res?["code"] != 200) return null;   // 接口出错时不记为“没有”，下次再试
            url = (string?)res?["data"]?["videoPlayUrl"];
            if (!NetGuard.IsNeteaseUrl(url)) { _noDynamicCover[cloudId] = 0; return null; }
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }

        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
            using var dl = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var rsp = await dl.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!rsp.IsSuccessStatusCode || !NetGuard.IsNeteaseUri(rsp.RequestMessage?.RequestUri)) return null;
            long? expected = rsp.Content.Headers.ContentLength;
            if (expected > MaxDynamicCoverBytes) return null;
            long written = 0;
            await using (var src = await rsp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[64 * 1024];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    written += n;
                    if (written > MaxDynamicCoverBytes) return null;
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            if (written <= 10240 || (expected.HasValue && written != expected.Value)) return null;
            File.Move(tmp, path, true);
            EnforceCacheQuota();
            return path;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    #endregion

    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    public (int Count, long Bytes) GetCacheInfo()
    {
        try
        {
            var files = new DirectoryInfo(_cacheDir).EnumerateFiles().ToList();
            return (files.Count(f => f.Extension is not (".jpg" or ".mp4")), files.Sum(f => f.Length));   // 数量只算音频
        }
        catch
        {
            return (0, 0);
        }
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

    /// <summary>按最近使用时间淘汰超出上限的缓存音轨（LRU）。</summary>
    private void EnforceCacheQuota()
    {
        try
        {
            long limit = Math.Max(256, State.NeteaseCacheLimitMb) * 1024L * 1024L;
            var audio = new DirectoryInfo(_cacheDir).EnumerateFiles()
                .Where(f => f.Extension is ".flac" or ".m4a" or ".mp3" or ".ogg" or ".mp4")   // mp4 = 动态封面
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

    #region 歌词

    private static string Ts(double ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    /// <summary>把网易云 yrc（[起,长](起,长,0)字…）转成增强 LRC。</summary>
    private static string YrcToEnhancedLrc(string yrc)
    {
        var sb = new StringBuilder();
        var lineRe = new System.Text.RegularExpressions.Regex(@"^\[(\d+),(\d+)\](.*)$");
        var wordRe = new System.Text.RegularExpressions.Regex(@"\((\d+),(\d+),-?\d+\)([^\(]*)");
        foreach (var raw in yrc.Split('\n'))
        {
            var m = lineRe.Match(raw.Trim());
            if (!m.Success) continue; // 跳过 {"t":..} 元信息行
            double start = double.Parse(m.Groups[1].Value);
            var words = wordRe.Matches(m.Groups[3].Value);
            if (words.Count == 0) continue;
            sb.Append('[').Append(Ts(start)).Append(']');
            double lastEnd = start;
            foreach (System.Text.RegularExpressions.Match w in words)
            {
                double ws = double.Parse(w.Groups[1].Value);
                double wd = double.Parse(w.Groups[2].Value);
                sb.Append('<').Append(Ts(ws)).Append('>').Append(w.Groups[3].Value);
                lastEnd = ws + wd;
            }
            sb.Append('<').Append(Ts(lastEnd)).Append(">\n");
        }
        return sb.ToString();
    }

    /// <summary>取歌词：优先逐字歌词，同时返回翻译与罗马音。</summary>
    public async Task<LyricBundle> GetLyricsAsync(string cloudId, CancellationToken ct = default)
    {
        try
        {
            var res = await _client.WeapiAsync("/weapi/song/lyric/v1", new JsonObject
            {
                ["id"] = cloudId, ["cp"] = false, ["tv"] = 0, ["lv"] = 0, ["rv"] = 0,
                ["kv"] = 0, ["yv"] = 0, ["ytv"] = 0, ["yrv"] = 0
            }, ct);
            if ((int?)res?["code"] == 200)
            {
                string lrc = (string?)res!["lrc"]?["lyric"] ?? "";
                string yrc = (string?)res["yrc"]?["lyric"] ?? "";
                if (yrc.Length > 0)
                {
                    var enhanced = YrcToEnhancedLrc(yrc);
                    if (enhanced.Length > 0) lrc = enhanced;
                }
                return new LyricBundle(lrc, (string?)res["tlyric"]?["lyric"] ?? "", (string?)res["romalrc"]?["lyric"] ?? "");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return LyricBundle.Empty;
    }

    #endregion
}
