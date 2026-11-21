using System.IO;
using System.Text.Json;
using PixelLyrics.Core;

namespace PixelLyrics.Probe;

internal sealed record SourceHistoryEntry(MusicPlayer Playback, SongIdentity Song, LyricSourceMapping? Mapping, bool Confirmed, DateTimeOffset Updated, bool Hidden = false);

// Source history contains metadata only. Temporary lyrics never leave the running session.
internal static class SourceHistory
{
    // History groups identical visible metadata; runtime mappings still use the exact song ID.
    internal static bool SameSong(SourceHistoryEntry entry, MusicPlayer playback, SongIdentity song) =>
        entry.Playback == playback && entry.Song.Title == song.Title &&
        entry.Song.Artist == song.Artist && entry.Song.Album == song.Album;

    internal static List<SourceHistoryEntry> ForDisplay(IEnumerable<SourceHistoryEntry> entries) => entries
        .GroupBy(e => (e.Playback, e.Song.Title, e.Song.Artist, e.Song.Album))
        .Select(group => group.OrderByDescending(e => e.Mapping is not null)
            .ThenByDescending(e => e.Updated).ThenByDescending(e => e.Confirmed).First())
        .Where(e => !e.Hidden).OrderByDescending(e => e.Updated).ToList();

    private static string PathFor(string settingsPath) => settingsPath + ".sources.json";
    internal static List<SourceHistoryEntry> Read(string settingsPath)
    {
        string path = PathFor(settingsPath);
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > ProbeSettings.MaxFileBytes) throw new IOException("歌词历史记录过大。");
        return JsonSerializer.Deserialize<List<SourceHistoryEntry>>(File.ReadAllText(path)) ?? throw new IOException("歌词历史记录损坏。");
    }
    internal static void Record(string settingsPath, MusicPlayer playback, SongIdentity song, LyricSourceMapping? mapping, bool confirmed)
    {
        lock (ProbeSettings.FileSync)
        {
            var entries = Read(settingsPath);
            var old = entries.Find(e => e.Playback == playback && e.Song.Id == song.Id);
            if ((old is { Confirmed: true } || old is { Hidden: true }) && mapping is null) return;
            entries.RemoveAll(e => e.Playback == playback && e.Song.Id == song.Id);
            entries.Insert(0, new(playback, song, mapping ?? old?.Mapping, confirmed, DateTimeOffset.Now));
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(entries, new JsonSerializerOptions { WriteIndented = true });
            if (json.Length > ProbeSettings.MaxFileBytes) throw new IOException("歌词历史记录已满。");
            string path = PathFor(settingsPath), temporary = path + ".tmp";
            File.WriteAllBytes(temporary, json);
            File.Move(temporary, path, true);
        }
    }
    internal static void Forget(string settingsPath, MusicPlayer playback, SongIdentity song)
    {
        lock (ProbeSettings.FileSync)
        {
            var entries = Read(settingsPath);
            entries.RemoveAll(e => e.Playback == playback && e.Song.Id == song.Id);
            string path = PathFor(settingsPath), temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries)); File.Move(temporary, path, true);
        }
    }
    internal static void Hide(string settingsPath, IEnumerable<string> keys)
    {
        lock (ProbeSettings.FileSync)
        {
            var selected = keys.ToHashSet(StringComparer.Ordinal);
            var records = Read(settingsPath);
            var groups = records.Where(e => selected.Contains(LyricSourceMappings.Key(e.Playback, e.Song.Id))).ToList();
            var entries = records.Select(e => groups.Any(group => SameSong(group, e.Playback, e.Song))
                ? e with { Hidden = true } : e).ToList();
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(entries);
            if (json.Length > ProbeSettings.MaxFileBytes) throw new IOException("歌词历史记录已满。");
            string path = PathFor(settingsPath), temporary = path + ".tmp";
            File.WriteAllBytes(temporary, json); File.Move(temporary, path, true);
        }
    }
}
