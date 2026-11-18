using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Klang_WinUI.Services.QQMusic;

/// <summary>
/// QQ 音乐非官方接口的请求层：统一接口（musicu.fcg）、zzc 签名、QQ / 微信扫码登录、凭证刷新。
/// 请求格式参考 luren-dc/QQMusicApi（GPL-3.0），用 C# 重写。
/// </summary>
internal sealed class QQMusicClient
{
    private const string WebUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string MusicuUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";
    private const string MusicsUrl = "https://u.y.qq.com/cgi-bin/musics.fcg";
    private const string WxAppId = "wx48db31d50e334801";

    // 手动管理 Cookie、不自动跟随跳转：登录流程要从各步的 Set-Cookie / Location 里取值
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>登录接口返回的 data 原样保存（刷新凭证时要用到其中的 openid / refresh_token 等字段）。</summary>
    public JsonObject? Credential { get; set; }

    public long MusicId => (long?)Credential?["musicid"] ?? 0;
    public string MusicKey => (string?)Credential?["musickey"] ?? "";
    public int LoginType => (int?)Credential?["loginType"] ?? (MusicKey.StartsWith("W_X") ? 1 : 2);
    public bool HasCredential => MusicId > 0 && MusicKey.Length > 0;

    /// <summary>凭证到期时间（UTC）；未知时为 null。</summary>
    public DateTime? KeyExpiresAt
    {
        get
        {
            long created = (long?)Credential?["musickeyCreateTime"] ?? 0;
            long expiresIn = (long?)Credential?["keyExpiresIn"] ?? 0;
            if (created <= 0 || expiresIn <= 0) return null;
            return DateTimeOffset.FromUnixTimeSeconds(created + expiresIn).UtcDateTime;
        }
    }

    public HttpClient Http => _http;

    #region 工具

    public static int Hash33(string s, long h = 0)
    {
        foreach (var c in s) h = ((h << 5) + h + c) & 0xFFFFFFFFFFFFL;
        return (int)(h & 2147483647);
    }

    /// <summary>musics.fcg 需要的 zzc 签名。</summary>
    public static string ZzcSign(string payload)
    {
        int[] p1 = { 23, 14, 6, 36, 16, 7, 19 }, p2 = { 16, 1, 32, 12, 19, 27, 8, 5 };
        int[] sc = { 89, 39, 179, 150, 218, 82, 58, 252, 177, 52, 186, 123, 120, 64, 242, 133, 143, 161, 121, 179 };
        var hex = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(payload)));
        var a = string.Concat(p1.Select(i => hex[i]));
        var b = string.Concat(p2.Select(i => hex[i]));
        var part3 = new byte[20];
        for (int i = 0; i < 20; i++) part3[i] = (byte)(sc[i] ^ Convert.ToInt32(hex.Substring(i * 2, 2), 16));
        var b64 = Regex.Replace(Convert.ToBase64String(part3), @"[\\/+=]", "");
        return ("zzc" + a + b64 + b).ToLowerInvariant();
    }

    private static string? GetCookie(HttpResponseMessage rsp, string name)
    {
        if (!rsp.Headers.TryGetValues("Set-Cookie", out var vals)) return null;
        foreach (var v in vals)
        {
            var first = v.Split(';')[0];
            int eq = first.IndexOf('=');
            if (eq > 0 && first[..eq].Trim() == name && first.Length > eq + 1) return first[(eq + 1)..];
        }
        return null;
    }

    private static string AllCookies(HttpResponseMessage rsp) =>
        rsp.Headers.TryGetValues("Set-Cookie", out var vals)
            ? string.Join("; ", vals.Select(v => v.Split(';')[0]).Where(p => p.Contains('=') && !p.EndsWith('=')))
            : "";

    private HttpRequestMessage Get(string url, string? referer = null, string? cookie = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", WebUA);
        if (referer != null) req.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrEmpty(cookie)) req.Headers.TryAddWithoutValidation("Cookie", cookie);
        return req;
    }

    #endregion

    #region 统一接口

    /// <summary>公共参数。desktop 用于搜索（网页端搜索接口不返回结果），其余用 web。</summary>
    private JsonObject Comm(bool desktop, int? tmeLoginType)
    {
        int gtk = MusicKey.Length > 0 ? Hash33(MusicKey, 5381) : 5381;
        var c = desktop
            ? new JsonObject { ["ct"] = "19", ["cv"] = "2201", ["chid"] = "0", ["guid"] = Guid.NewGuid().ToString("N").ToUpperInvariant(), ["g_tk"] = gtk.ToString() }
            : new JsonObject
            {
                ["ct"] = "24", ["cv"] = "4747474", ["platform"] = "yqq.json", ["chid"] = "0",
                ["g_tk"] = gtk.ToString(), ["g_tk_new_20200303"] = gtk.ToString(),
                ["format"] = "json", ["inCharset"] = "utf-8", ["outCharset"] = "utf-8", ["notice"] = "0", ["need_new_code"] = "1"
            };
        c["uin"] = MusicId.ToString();
        if (HasCredential)
        {
            c["qq"] = MusicId.ToString();
            c["authst"] = MusicKey;
            c["tmeLoginType"] = LoginType.ToString();
        }
        if (tmeLoginType != null) c["tmeLoginType"] = tmeLoginType.ToString();
        return c;
    }

    /// <summary>调用一个 模块.方法，返回 req_0（含 code 与 data）。</summary>
    public async Task<JsonNode?> CgiAsync(string module, string method, JsonObject param,
        bool desktop = false, bool sign = false, int? tmeLoginType = null, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["comm"] = Comm(desktop, tmeLoginType),
            ["req_0"] = new JsonObject { ["module"] = module, ["method"] = method, ["param"] = param }
        };
        var json = payload.ToJsonString();
        var url = sign ? $"{MusicsUrl}?_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}&sign={ZzcSign(json)}" : MusicuUrl;
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("User-Agent", WebUA);
        req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
        if (HasCredential)
            req.Headers.TryAddWithoutValidation("Cookie", $"uin={MusicId}; qqmusic_uin={MusicId}; qm_keyst={MusicKey}; qqmusic_key={MusicKey}");
        using var rsp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await rsp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonNode.Parse(text)?["req_0"];
    }

    #endregion

    #region 扫码登录

    /// <summary>QQ 扫码：返回 ("qq:" + qrsig, PNG)。</summary>
    public async Task<(string Key, byte[] Image)> CreateQqQrAsync()
    {
        var url = $"https://ssl.ptlogin2.qq.com/ptqrshow?appid=716027609&e=2&l=M&s=3&d=72&v=4&t={Random.Shared.NextDouble()}&daid=383&pt_3rd_aid=100497308";
        using var req = Get(url, "https://xui.ptlogin2.qq.com/");
        using var rsp = await _http.SendAsync(req).ConfigureAwait(false);
        var qrsig = GetCookie(rsp, "qrsig");
        var png = await rsp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return qrsig == null || png.Length == 0 ? ("", Array.Empty<byte>()) : ("qq:" + qrsig, png);
    }

    /// <summary>微信扫码：返回 ("wx:" + uuid, JPEG)。</summary>
    public async Task<(string Key, byte[] Image)> CreateWxQrAsync()
    {
        var url = "https://open.weixin.qq.com/connect/qrconnect?appid=" + WxAppId +
            "&redirect_uri=" + Uri.EscapeDataString("https://y.qq.com/portal/wx_redirect.html?login_type=2&surl=https://y.qq.com/") +
            "&response_type=code&scope=snsapi_login&state=STATE&href=" +
            Uri.EscapeDataString("https://y.qq.com/mediastyle/music_v17/src/css/popup_wechat.css#wechat_redirect");
        using var req = Get(url);
        using var rsp = await _http.SendAsync(req).ConfigureAwait(false);
        var html = await rsp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var uuid = Regex.Match(html, "uuid=([A-Za-z0-9_\\-]+)\"").Groups[1].Value;
        if (uuid.Length == 0) return ("", Array.Empty<byte>());
        using var qReq = Get($"https://open.weixin.qq.com/connect/qrcode/{uuid}", "https://open.weixin.qq.com/connect/qrconnect");
        using var qRsp = await _http.SendAsync(qReq).ConfigureAwait(false);
        var img = await qRsp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return img.Length == 0 ? ("", Array.Empty<byte>()) : ("wx:" + uuid, img);
    }

    public enum QrStatus { Waiting, Scanned, Expired, Refused, Done, Error }

    /// <summary>查询扫码状态；Done 时已完成登录，凭证写入 <see cref="Credential"/>。</summary>
    public async Task<(QrStatus Status, string Message)> PollQrAsync(string key)
    {
        if (key.StartsWith("qq:")) return await PollQqAsync(key[3..]).ConfigureAwait(false);
        if (key.StartsWith("wx:")) return await PollWxAsync(key[3..]).ConfigureAwait(false);
        return (QrStatus.Error, "unknown qr key");
    }

    private async Task<(QrStatus, string)> PollQqAsync(string qrsig)
    {
        var url = "https://ssl.ptlogin2.qq.com/ptqrlogin?u1=" + Uri.EscapeDataString("https://graph.qq.com/oauth2.0/login_jump") +
            $"&ptqrtoken={Hash33(qrsig)}&ptredirect=0&h=1&t=1&g=1&from_ui=1&ptlang=2052&action=0-0-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" +
            "&js_ver=20102616&js_type=1&pt_uistyle=40&aid=716027609&daid=383&pt_3rd_aid=100497308&has_onekey=1";
        using var req = Get(url, "https://xui.ptlogin2.qq.com/", $"qrsig={qrsig}");
        using var rsp = await _http.SendAsync(req).ConfigureAwait(false);
        var text = await rsp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var m = Regex.Match(text, @"ptuiCB\((.*?)\)");
        if (!m.Success) return (QrStatus.Error, "");
        var parts = Regex.Matches(m.Groups[1].Value, @"'((?:\\.|[^'])*)'").Select(x => x.Groups[1].Value).ToList();
        string msg = parts.Count > 4 ? parts[4] : "";
        switch (parts.FirstOrDefault())
        {
            case "66": return (QrStatus.Waiting, msg);
            case "67": return (QrStatus.Scanned, msg);
            case "65": return (QrStatus.Expired, msg);
            case "68": return (QrStatus.Refused, msg);
            case "0": break;
            default: return (QrStatus.Error, msg);
        }

        var done = parts.Count > 2 ? parts[2] : "";
        var sigx = Regex.Match(done, @"(?:\?|&)ptsigx=(.+?)&s_url").Groups[1].Value;
        var uin = Regex.Match(done, @"(?:\?|&)uin=(.+?)&service").Groups[1].Value;
        if (sigx.Length == 0 || uin.Length == 0) return (QrStatus.Error, "");

        var checkUrl = $"https://ssl.ptlogin2.graph.qq.com/check_sig?uin={uin}&pttype=1&service=ptqrlogin&nodirect=0&ptsigx={sigx}&s_url=" +
            Uri.EscapeDataString("https://graph.qq.com/oauth2.0/login_jump") +
            "&ptlang=2052&ptredirect=100&aid=716027609&daid=383&j_later=0&low_login_hour=0&regmaster=0&pt_login_type=3&pt_aid=0&pt_aaid=16&pt_light=0&pt_3rd_aid=100497308";
        using var cReq = Get(checkUrl, "https://xui.ptlogin2.qq.com/");
        using var cRsp = await _http.SendAsync(cReq).ConfigureAwait(false);
        var pskey = GetCookie(cRsp, "p_skey");
        if (pskey == null) return (QrStatus.Error, "p_skey");

        var form = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = "100497308",
            ["redirect_uri"] = "https://y.qq.com/portal/wx_redirect.html?login_type=1&surl=https://y.qq.com/",
            ["scope"] = "get_user_info,get_app_friends", ["state"] = "state", ["switch"] = "", ["from_ptlogin"] = "1", ["src"] = "1",
            ["update_auth"] = "1", ["openapi"] = "1010_1030", ["g_tk"] = Hash33(pskey, 5381).ToString(),
            ["auth_time"] = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1000).ToString(), ["ui"] = Guid.NewGuid().ToString(),
        };
        using var aReq = new HttpRequestMessage(HttpMethod.Post, "https://graph.qq.com/oauth2.0/authorize") { Content = new FormUrlEncodedContent(form) };
        aReq.Headers.TryAddWithoutValidation("User-Agent", WebUA);
        aReq.Headers.TryAddWithoutValidation("Cookie", AllCookies(cRsp));
        using var aRsp = await _http.SendAsync(aReq).ConfigureAwait(false);
        var code = Regex.Match(aRsp.Headers.Location?.ToString() ?? "", @"(?<=code=)(.+?)(?=&)").Value;
        if (code.Length == 0) return (QrStatus.Error, "code");

        var r = await CgiAsync("QQConnectLogin.LoginServer", "QQLogin", new JsonObject { ["code"] = code }, tmeLoginType: 2).ConfigureAwait(false);
        return AcceptLogin(r, 2);
    }

    private async Task<(QrStatus, string)> PollWxAsync(string uuid)
    {
        string text;
        try
        {
            // 长轮询：没有变化时服务端最多挂起约 25 秒
            using var req = Get($"https://lp.open.weixin.qq.com/connect/l/qrconnect?uuid={uuid}&_={DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1000}", "https://open.weixin.qq.com/");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            using var rsp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            text = await rsp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (QrStatus.Waiting, "");
        }
        var m = Regex.Match(text, @"window\.wx_errcode=(\d+);window\.wx_code='([^']*)'");
        if (!m.Success) return (QrStatus.Error, "");
        switch (m.Groups[1].Value)
        {
            case "408": return (QrStatus.Waiting, "");
            case "404": return (QrStatus.Scanned, "");
            case "402": return (QrStatus.Expired, "");
            case "403": return (QrStatus.Refused, "");
            case "405": break;
            default: return (QrStatus.Error, m.Groups[1].Value);
        }
        var wxCode = m.Groups[2].Value;
        if (wxCode.Length == 0) return (QrStatus.Error, "code");
        var r = await CgiAsync("music.login.LoginServer", "Login",
            new JsonObject { ["code"] = wxCode, ["strAppid"] = WxAppId }, tmeLoginType: 1).ConfigureAwait(false);
        return AcceptLogin(r, 1);
    }

    private (QrStatus, string) AcceptLogin(JsonNode? r, int loginType)
    {
        int code = (int?)r?["code"] ?? -1;
        if (code != 0 || r?["data"] is not JsonObject data) return (QrStatus.Error, LoginErrorText(code));
        if (data["loginType"] == null) data["loginType"] = loginType;
        var cred = (JsonObject)data.DeepClone();
        if (((long?)cred["musicid"] ?? 0) <= 0 || string.IsNullOrEmpty((string?)cred["musickey"])) return (QrStatus.Error, LoginErrorText(code));
        Credential = cred;
        return (QrStatus.Done, "");
    }

    public static string LoginErrorText(int code) => code switch
    {
        1000 or 104400 or 104401 => L.T("登录已过期"),
        20277 or 20278 or 20450 => L.T("账号受限"),
        20279 => L.T("登录设备数已达上限"),
        104604 => L.T("请求过于频繁，请稍后再试"),
        _ => L.F("登录失败（{0}）", code)
    };

    #endregion

    #region 凭证刷新

    /// <summary>用保存的 refresh_key 等字段换新的 musickey。返回 true 表示已刷新；false 表示服务端拒绝（需要重新登录）。网络错误会抛出。</summary>
    public async Task<bool> RefreshAsync()
    {
        var c = Credential;
        if (c == null || !HasCredential) return false;
        string S(string k) => c[k]?.ToString() ?? "";
        var param = new JsonObject
        {
            ["openid"] = S("openid"),
            ["refresh_token"] = S("refresh_token"),
            ["musickey"] = MusicKey,
            ["unionid"] = S("unionid"),
            ["refresh_key"] = S("refresh_key"),
            ["loginMode"] = 2,
        };
        if (LoginType == 1)
        {
            param["str_musicid"] = S("str_musicid").Length > 0 ? S("str_musicid") : MusicId.ToString();
        }
        else
        {
            param["access_token"] = S("access_token");
            param["expired_in"] = (long?)c["expired_at"] ?? 0;
            param["musicid"] = MusicId;
        }
        var r = await CgiAsync("music.login.LoginServer", "Login", param, tmeLoginType: LoginType).ConfigureAwait(false);
        int code = (int?)r?["code"] ?? -1;
        if (code != 0 || r?["data"] is not JsonObject data) return false;
        var fresh = (JsonObject)data.DeepClone();
        if (string.IsNullOrEmpty((string?)fresh["musickey"])) return false;
        // 刷新结果可能缺少部分字段（如 openid），保留旧值
        foreach (var kv in c)
            if (fresh[kv.Key] == null && kv.Value != null) fresh[kv.Key] = kv.Value.DeepClone();
        if (fresh["loginType"] == null) fresh["loginType"] = LoginType;
        Credential = fresh;
        return true;
    }

    #endregion

    /// <summary>当前账号的昵称；取不到时返回 null。</summary>
    public async Task<string?> GetNicknameAsync()
    {
        if (!HasCredential) return null;
        var url = "https://c6.y.qq.com/rsc/fcgi-bin/fcg_get_profile_homepage.fcg?format=json&inCharset=utf-8&outCharset=utf-8&notice=0" +
            $"&cid=205360838&needNewCode=0&g_tk={Hash33(MusicKey, 5381)}&loginUin={MusicId}&hostUin=0&userid={MusicId}&reqfrom=1";
        using var req = Get(url, "https://y.qq.com/", $"uin={MusicId}; qqmusic_uin={MusicId}; qm_keyst={MusicKey}; qqmusic_key={MusicKey}");
        using var rsp = await _http.SendAsync(req).ConfigureAwait(false);
        var root = JsonNode.Parse(await rsp.Content.ReadAsStringAsync().ConfigureAwait(false));
        if ((int?)root?["code"] != 0) return null;
        var nick = (string?)root?["data"]?["creator"]?["nick"];
        return string.IsNullOrWhiteSpace(nick) ? null : nick;
    }
}
