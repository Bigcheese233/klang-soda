using System;
using System.Collections.Generic;
using System.Linq;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services.Cloud;

/// <summary>云端面板来源，以及保留独立侧栏的汽水播放适配器。</summary>
public static class CloudSources
{
    public const string NeteaseKey = "netease";

    private static readonly Lazy<IReadOnlyList<ICloudSource>> _all = new(() => new ICloudSource[]
    {
        Netease.NeteaseService.Instance,
        QQMusic.QQMusicService.Instance,
    });

    public static IReadOnlyList<ICloudSource> All => _all.Value;

    /// <summary>按标识找来源；空标识按网易云处理（早期版本的曲目只有网易云）。</summary>
    public static ICloudSource? Get(string? key)
    {
        if (string.IsNullOrEmpty(key)) key = NeteaseKey;
        if (key == Soda.SodaCloudSource.SourceKey) return Soda.SodaCloudSource.Instance;
        return All.FirstOrDefault(s => s.Key == key);
    }

    /// <summary>云端曲目所属的来源；本地曲目或来源已不存在时返回 null。</summary>
    public static ICloudSource? For(TrackItem? track) =>
        track != null && track.IsOnline ? Get(track.CloudSource) : null;

    /// <summary>云端面板当前显示的来源（记在状态里，找不到时用第一个）。</summary>
    public static ICloudSource Current
    {
        get => All.FirstOrDefault(s => s.Key == StateService.Instance.State.CloudSource) ?? All[0];
        set
        {
            StateService.Instance.State.CloudSource = value.Key;
            StateService.Instance.RequestSave();
        }
    }

    /// <summary>把云端曲目存成快照。</summary>
    public static OnlineTrackSnapshot ToSnapshot(TrackItem t) => new()
    {
        Source = string.IsNullOrEmpty(t.CloudSource) ? NeteaseKey : t.CloudSource,
        CloudId = t.CloudId, Title = t.Title, Artist = t.Artist,
        Album = t.Album, Duration = t.Duration, CoverUrl = t.CoverUrl
    };

    /// <summary>按来源分组还原快照，保持原来的顺序；来源已不存在的条目跳过。</summary>
    public static List<TrackItem> FromSnapshots(IEnumerable<OnlineTrackSnapshot> list)
    {
        var result = new List<TrackItem>();
        foreach (var s in list)
        {
            var src = Get(s.Source);
            if (src == null) continue;
            result.AddRange(src.FromSnapshots(new[] { s }));
        }
        for (int i = 0; i < result.Count; i++) result[i].Index = i + 1;
        return result;
    }
}
