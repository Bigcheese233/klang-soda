using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services;

public class LyricsService
{
    private static readonly Lazy<LyricsService> _instance = new(() => new LyricsService());
    public static LyricsService Instance => _instance.Value;

    private static readonly Regex LrcTagRegex = new(@"\[(\d{1,2}):(\d{1,2}(?:\.\d{1,3})?)\]", RegexOptions.Compiled);
    private static readonly Regex HeaderNoiseRegex = new(@"^\[(ti|ar|al|by|offset|kana|re|ve):.*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public record RawLrcEntry(double Time, string Text, List<LyricWord>? Words = null);

    private static readonly Regex WordTagRegex = new(@"<(\d{1,2}):(\d{1,2}(?:\.\d{1,3})?)>", RegexOptions.Compiled);

    /// <summary>解析增强 LRC 的逐字标记：&lt;mm:ss.xx&gt;字&lt;mm:ss.xx&gt;字…，返回纯文本与逐字列表。</summary>
    private static (string Text, List<LyricWord>? Words) ExtractWords(string raw)
    {
        var tags = WordTagRegex.Matches(raw);
        if (tags.Count == 0) return (raw, null);

        double TimeOf(Match m) =>
            double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 60.0 +
            double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);

        var words = new List<LyricWord>();
        for (int i = 0; i < tags.Count; i++)
        {
            int start = tags[i].Index + tags[i].Length;
            int end = i + 1 < tags.Count ? tags[i + 1].Index : raw.Length;
            string seg = raw.Substring(start, end - start);
            double t0 = TimeOf(tags[i]);
            double t1 = i + 1 < tags.Count ? TimeOf(tags[i + 1]) : t0 + 0.5;
            if (seg.Length == 0) continue; // 末尾的结束标记
            if (string.IsNullOrWhiteSpace(seg) && words.Count > 0)
            {
                words[^1].Text += seg;
                continue;
            }
            words.Add(new LyricWord(t0, Math.Max(t1, t0 + 0.05), seg));
        }
        string text = string.Concat(words.Select(w => w.Text)).Trim();
        return (text, words.Count > 0 ? words : null);
    }

    #region 用户选定的歌词

    /// <summary>一份歌词：主歌词（LRC / 增强 LRC），可选的翻译与罗马音。</summary>
    public sealed record LyricSet(string Lrc, string Trans = "", string Roma = "");

    /// <summary>用户在“选择歌词”里选定的歌词，按曲目 ID 存放，优先于自动匹配（不改动音乐文件夹）。</summary>
    public static string ChosenDir => Path.Combine(
        StateService.ResolveDataDirectory(), "LyricsChosen");

    private static string ChosenPath(string trackId, string suffix) =>
        Path.Combine(ChosenDir, string.Concat(trackId.Split(Path.GetInvalidFileNameChars())) + suffix);

    public static bool HasChosen(string trackId)
    {
        try { return File.Exists(ChosenPath(trackId, ".lrc")); } catch { return false; }
    }

    public static LyricSet? ReadChosen(string trackId)
    {
        try
        {
            var p = ChosenPath(trackId, ".lrc");
            if (!File.Exists(p)) return null;
            string Read(string suffix) { var f = ChosenPath(trackId, suffix); return File.Exists(f) ? File.ReadAllText(f) : ""; }
            return new LyricSet(File.ReadAllText(p), Read(".trans.lrc"), Read(".roma.lrc"));
        }
        catch { return null; }
    }

    public static void SaveChosen(string trackId, LyricSet set)
    {
        Directory.CreateDirectory(ChosenDir);
        File.WriteAllText(ChosenPath(trackId, ".lrc"), set.Lrc);
        foreach (var (suffix, text) in new[] { (".trans.lrc", set.Trans), (".roma.lrc", set.Roma) })
        {
            var f = ChosenPath(trackId, suffix);
            if (string.IsNullOrWhiteSpace(text)) { if (File.Exists(f)) File.Delete(f); }
            else File.WriteAllText(f, text);
        }
    }

    public static void ClearChosen(string trackId)
    {
        foreach (var suffix in new[] { ".lrc", ".trans.lrc", ".roma.lrc" })
        {
            try { var f = ChosenPath(trackId, suffix); if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    #endregion

    #region 本地歌词来源

    /// <summary>音频旁边的同名 .lrc（以及 .trans.lrc / .zh.lrc 翻译）。文件编码自动识别（UTF-8 / GBK）。</summary>
    public static LyricSet? ReadLocalFile(string? audioFilePath)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath)) return null;
        try
        {
            string dir = Path.GetDirectoryName(audioFilePath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(audioFilePath);
            string lrc = Path.Combine(dir, $"{baseName}.lrc");
            if (!File.Exists(lrc)) return null;
            string trans = Path.Combine(dir, $"{baseName}.trans.lrc");
            if (!File.Exists(trans)) trans = Path.Combine(dir, $"{baseName}.zh.lrc");
            return new LyricSet(CueParser.ReadText(lrc), File.Exists(trans) ? CueParser.ReadText(trans) : "");
        }
        catch { return null; }
    }

    /// <summary>音频文件标签里内嵌的歌词（ID3v2 USLT / Vorbis LYRICS 等）。</summary>
    public static LyricSet? ReadEmbedded(string? audioFilePath)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath)) return null;
        try
        {
            using var tagFile = TagLib.File.Create(audioFilePath);
            string? embedded = tagFile.Tag?.Lyrics;
            return string.IsNullOrWhiteSpace(embedded) ? null : new LyricSet(embedded);
        }
        catch { return null; }
    }

    /// <summary>这份歌词能不能用：至少有一行带时间轴。</summary>
    public bool IsSynced(string? lrc) => !string.IsNullOrWhiteSpace(lrc) && ParseRawEntries(lrc).Count > 0;

    #endregion

    public List<LyricLine> LoadLyrics(string? audioFilePath)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath))
        {
            return CreateEmptyPlaceholder(L.T("暂无歌词"));
        }

        // 1. 同目录下同名 .lrc 文件
        if (ReadLocalFile(audioFilePath) is { } local)
        {
            var parsed = ParseLrc(local.Lrc, local.Trans);
            if (IsSynced(local.Lrc)) return parsed;
        }

        // 2. 音频文件标签里的内嵌歌词
        if (ReadEmbedded(audioFilePath) is { } embedded && IsSynced(embedded.Lrc))
        {
            return ParseLrc(embedded.Lrc);
        }

        // 3. 之前在线补全并缓存下来的歌词（LRCLIB）
        try
        {
            var cached = LrclibService.ReadCache(TrackItem.MakeId(Path.GetFullPath(audioFilePath)));
            if (!string.IsNullOrWhiteSpace(cached))
            {
                var parsed = ParseLrc(cached);
                if (parsed.Count > 0) return parsed;
            }
        }
        catch { }

        return CreateEmptyPlaceholder(L.T("暂无歌词"));
    }

    public List<LyricLine> ParseLrc(string primaryLrc, string? transLrc = null, string? romaLrc = null)
    {
        var primaryEntries = ParseRawEntries(primaryLrc);
        if (primaryEntries.Count == 0)
        {
            return CreateEmptyPlaceholder(L.T("暂无歌词"));
        }

        var transEntries = !string.IsNullOrWhiteSpace(transLrc) ? ParseRawEntries(transLrc) : new List<RawLrcEntry>();

        var romaEntries = !string.IsNullOrWhiteSpace(romaLrc) ? ParseRawEntries(romaLrc) : new List<RawLrcEntry>();
        string FindNear(List<RawLrcEntry> list, double t) => Clean(list
            .Where(te => Math.Abs(te.Time - t) <= 0.8)
            .OrderBy(te => Math.Abs(te.Time - t))
            .Select(te => te.Text)
            .FirstOrDefault());

        // 网易云的翻译里，不需要翻译的行（比如中文歌里的中文句）写成 “//”，当作没有翻译（#26）
        static string Clean(string? s) =>
            string.IsNullOrWhiteSpace(s) || s.All(ch => ch == '/' || char.IsWhiteSpace(ch)) ? "" : s.Trim();

        // ---- 本地歌词常见的双语写法：原文和翻译各占一行、时间标签相同。合并成一句，否则两行同时开始，
        //      高亮会落在排在后面的那行（通常是翻译）上。只在没有单独的翻译歌词时这样处理。----
        var groups = new List<List<RawLrcEntry>>();
        foreach (var entry in primaryEntries)
        {
            bool canMerge = transEntries.Count == 0 && groups.Count > 0 && !IsBlankEntry(entry) && !IsBlankEntry(groups[^1][0])
                            && Math.Abs(entry.Time - groups[^1][0].Time) <= 0.05;
            if (canMerge) groups[^1].Add(entry);
            else groups.Add(new List<RawLrcEntry> { entry });
        }
        // 整首歌统一判断原文在前还是在后（逐行判断会出现这句对、下一句反的情况）
        var multi = groups.Where(g => g.Count >= 2).ToList();
        bool originalLast = multi.Count > 0 && multi.Sum(g => TranslationScore(g[0])) > multi.Sum(g => TranslationScore(g[^1]));

        var rawList = new List<(double Time, string Text, string Sub, string Roma, List<LyricWord>? Words, bool Paired, List<LyricWord>? AltWords)>();
        foreach (var group in groups)
        {
            if (group.Count >= 2)
            {
                var main = originalLast ? group[^1] : group[0];
                var others = group.Where(e => !ReferenceEquals(e, main)).ToList();
                // 原文是日文 / 韩文时，几乎全是拉丁字母的那行是罗马音，其余是翻译
                bool asian = HasKanaOrHangul(main.Text);
                var romaLine = asian ? others.FirstOrDefault(e => IsMostlyLatin(e.Text)) : null;
                var transLines = others.Where(e => !ReferenceEquals(e, romaLine)).ToList();
                string trans = string.Join(" / ", transLines.Select(e => Clean(e.Text)).Where(s => s.Length > 0));
                string roma = romaLine != null ? Clean(romaLine.Text) : (romaEntries.Count > 0 ? FindNear(romaEntries, main.Time) : "");
                rawList.Add((main.Time, main.Text.Trim(), trans, roma, main.Words, trans.Length > 0, transLines.FirstOrDefault()?.Words));
                continue;
            }

            var entry = group[0];
            double t = entry.Time;
            string text = entry.Text;
            string mainText = text;
            string subText = "";

            if (text.Contains("//"))
            {
                var parts = text.Split("//", 2);
                mainText = parts[0].Trim();
                subText = Clean(parts[1]);
            }
            else if (transEntries.Count > 0)
            {
                subText = Clean(transEntries
                    .Where(te => Math.Abs(te.Time - t) <= 0.6)
                    .OrderBy(te => Math.Abs(te.Time - t))
                    .Select(te => te.Text)
                    .FirstOrDefault());
            }

            string romaText = romaEntries.Count > 0 ? FindNear(romaEntries, t) : "";
            rawList.Add((t, mainText, subText, romaText, entry.Words, false, null));
        }

        // 只有时间、没有文字的空行（有的歌词每句后面都跟一个，标记这句唱完）不单独占一行，
        // 否则歌词之间会出现大段空白、间距忽大忽小（#26）；它的时间用作上一句的结束时间
        var kept = new List<(double Time, string Text, string Sub, string Roma, List<LyricWord>? Words, bool Paired, List<LyricWord>? AltWords, double? End)>();
        foreach (var item in rawList)
        {
            bool blank = string.IsNullOrWhiteSpace(item.Text) && (item.Words == null || item.Words.Count == 0);
            if (blank)
            {
                if (kept.Count > 0 && kept[^1].End == null) kept[^1] = kept[^1] with { End = item.Time };
                continue;
            }
            kept.Add((item.Time, item.Text, item.Sub, item.Roma, item.Words, item.Paired, item.AltWords, null));
        }
        if (kept.Count == 0) return CreateEmptyPlaceholder(L.T("暂无歌词"));

        var result = new List<LyricLine>();
        for (int i = 0; i < kept.Count; i++)
        {
            var item = kept[i];
            double nextTime = (i + 1 < kept.Count) ? kept[i + 1].Time : item.Time + 6.0;
            double endTime = Math.Max(item.Time + 1.2, (item.End ?? nextTime) - 0.1);

            result.Add(new LyricLine
            {
                Index = i,
                Time = item.Time,
                EndTime = endTime,
                Text = item.Text,
                Sub = item.Sub,
                Roma = item.Roma,
                Words = item.Words,
                PairedTranslation = item.Paired,
                AltWords = item.AltWords
            });
        }

        return result;
    }

    // ---- 双语歌词：判断哪一行是翻译 ----

    private static bool IsBlankEntry(RawLrcEntry e) => string.IsNullOrWhiteSpace(e.Text) && (e.Words == null || e.Words.Count == 0);

    private static bool IsKana(char c) => c is >= '぀' and <= 'ヿ' or >= 'ㇰ' and <= 'ㇿ';
    private static bool IsHangul(char c) => c is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';
    private static bool IsHan(char c) => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿';
    private static bool IsLatin(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' || c is >= 'À' and <= 'ɏ';

    private static bool HasKanaOrHangul(string s) => s.Any(c => IsKana(c) || IsHangul(c));

    private static bool IsMostlyLatin(string s)
    {
        int letters = s.Count(char.IsLetter);
        return letters > 0 && s.Count(IsLatin) >= letters * 0.8;
    }

    /// <summary>
    /// 一行歌词“像翻译”的程度：带逐字时间的、含假名或韩文的更像原文；
    /// 是界面语言（中文界面下的纯中文、英文 / 丹麦文界面下的拉丁字母）的更像翻译——翻译是给听的人看的。
    /// </summary>
    private static double TranslationScore(RawLrcEntry e)
    {
        double s = 0;
        if (e.Words is { Count: > 0 }) s -= 3;
        if (HasKanaOrHangul(e.Text)) s -= 2;
        int letters = e.Text.Count(char.IsLetter);
        if (letters > 0)
        {
            bool zhUi = L.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            bool userLang = zhUi
                ? e.Text.Count(IsHan) >= letters * 0.5 && !HasKanaOrHangul(e.Text)
                : IsMostlyLatin(e.Text);
            if (userLang) s += 1;
        }
        return s;
    }

    private List<RawLrcEntry> ParseRawEntries(string text)
    {
        var entries = new List<RawLrcEntry>();
        if (string.IsNullOrWhiteSpace(text)) return entries;

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || HeaderNoiseRegex.IsMatch(line))
                continue;

            var matches = LrcTagRegex.Matches(line);
            if (matches.Count == 0) continue;

            var (lyricText, words) = ExtractWords(LrcTagRegex.Replace(line, "").Trim());

            foreach (Match match in matches)
            {
                if (double.TryParse(match.Groups[1].Value, out double minutes) &&
                    double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
                {
                    double totalSec = minutes * 60.0 + seconds;
                    entries.Add(new RawLrcEntry(totalSec, lyricText, words));
                }
            }
        }

        return entries.OrderBy(e => e.Time).ToList();
    }

    public int FindActiveIndex(IList<LyricLine> lyrics, double currentTime)
    {
        if (lyrics == null || lyrics.Count == 0) return -1;

        int active = -1;
        for (int i = 0; i < lyrics.Count; i++)
        {
            if (currentTime >= lyrics[i].Time)
            {
                active = i;
            }
            else
            {
                break;
            }
        }

        return active;
    }

    private List<LyricLine> CreateEmptyPlaceholder(string message = "暂无歌词", string sub = "")
    {
        return new List<LyricLine>
        {
            new LyricLine { Index = 0, Time = 0.0, EndTime = 9999.0, Text = message, Sub = sub }
        };
    }
}
