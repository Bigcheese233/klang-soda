using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Klang_WinUI.Services;

/// <summary>
/// 用户自己调整 / 更换的封面（#18）。调整后的正方形图片存在 数据目录\CustomCovers，
/// index.json 记录“曲目 ID → 图片文件”。曲目原来的封面路径不动：恢复默认只要删掉这条记录；
/// 重新扫描曲库、在线补全写回的是原始封面，也不会覆盖用户的选择。
/// </summary>
public static class CustomCoverService
{
    public static string Dir => Path.Combine(StateService.ResolveDataDirectory(), "CustomCovers");
    private static string IndexPath => Path.Combine(Dir, "index.json");

    private static readonly object _gate = new();
    private static Dictionary<string, string>? _map;   // 曲目 ID → 图片完整路径（只保留文件还在的）

    private static Dictionary<string, string> Map
    {
        get
        {
            if (_map != null) return _map;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(IndexPath))
                {
                    var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IndexPath)) ?? new();
                    foreach (var (id, file) in raw)
                    {
                        var full = Path.Combine(Dir, Path.GetFileName(file));
                        if (File.Exists(full)) map[id] = full;
                    }
                }
            }
            catch (Exception ex) { App.LogError("Custom covers load", ex.Message); }
            _map = map;
            return map;
        }
    }

    /// <summary>这首歌的自定义封面；没有时返回 null。</summary>
    public static string? Lookup(string? trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return null;
        lock (_gate) return Map.TryGetValue(trackId, out var path) ? path : null;
    }

    public static bool Has(string? trackId) => Lookup(trackId) != null;

    /// <summary>新图片在数据目录里的保存路径（每次一个新文件名，避免界面沿用旧图的缓存）。</summary>
    public static string NewFilePath()
    {
        Directory.CreateDirectory(Dir);
        return Path.Combine(Dir, $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}.jpg");
    }

    /// <summary>把这些曲目的封面设为 path。</summary>
    public static void Assign(IEnumerable<string> trackIds, string path)
    {
        lock (_gate)
        {
            foreach (var id in trackIds) Map[id] = path;
            SaveAndPrune();
        }
    }

    /// <summary>恢复默认：去掉这些曲目的自定义封面。</summary>
    public static void Clear(IEnumerable<string> trackIds)
    {
        lock (_gate)
        {
            foreach (var id in trackIds) Map.Remove(id);
            SaveAndPrune();
        }
    }

    /// <summary>写回索引，并删掉已经没有曲目在用的图片。</summary>
    private static void SaveAndPrune()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var data = Map.ToDictionary(kv => kv.Key, kv => Path.GetFileName(kv.Value));
            var tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data));
            File.Move(tmp, IndexPath, true);

            var used = new HashSet<string>(Map.Values, StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(Dir, "*.jpg"))
                if (!used.Contains(f)) { try { File.Delete(f); } catch { } }
        }
        catch (Exception ex) { App.LogError("Custom covers save", ex.Message); }
    }
}
