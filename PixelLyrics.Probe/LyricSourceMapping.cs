using PixelLyrics.Core;
using PixelLyrics.OnlineProbe;

namespace PixelLyrics.Probe;

internal sealed record LyricSourceMapping(MusicPlayer Platform, string SongId);
internal sealed record SourceChoice(TrackVersion Track, SongIdentity Song, MusicPlayer Playback, LyricSourceMapping? Mapping, LyricsDocument? Document, bool Trial = false);

internal sealed class SourceReminder
{
    private TimeSpan? start;
    internal bool Show(TimeSpan now) { start ??= now; return now - start.Value < TimeSpan.FromSeconds(2); }
    internal TimeSpan Elapsed(TimeSpan now) => start is null ? TimeSpan.Zero : now - start.Value;
    internal void Reset() => start = null;
}

internal static class LyricSourceMappings
{
    internal static SongScore[] RankCandidates(IEnumerable<SongScore> candidates, SongIdentity song) => candidates
        .OrderByDescending(candidate => candidate.Category == MatchCategory.Accepted)
        .ThenByDescending(candidate => SongMatcher.EvaluateManual(candidate.Details, song.Title, song.Artist, song.Album, song.Duration.TotalSeconds).Score)
        .ThenBy(candidate => double.IsFinite(candidate.Details.Interval) ? Math.Abs(candidate.Details.Interval - song.Duration.TotalSeconds) : double.PositiveInfinity)
        .ToArray();

    internal static string Key(MusicPlayer playback, string identity) => playback + ":" + identity;
    internal static bool ValidId(MusicPlayer platform, string id) => platform == MusicPlayer.NetEaseMusic
        ? MusicPlayers.ValidNetEaseId(id) : id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit);

    internal static string? ParseLink(string input, MusicPlayer platform)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;
        string? id = null;
        if (platform == MusicPlayer.NetEaseMusic && uri.Host == "music.163.com")
        {
            string path = uri.Fragment.Length > 0 ? uri.Fragment[1..] : uri.PathAndQuery;
            var match = System.Text.RegularExpressions.Regex.Match(path, @"^/song[?]id=([0-9]+)(?:&[^#]*)?$");
            if (match.Success) id = match.Groups[1].Value;
        }
        if (platform == MusicPlayer.QQMusic && uri.Host is "y.qq.com" or "i.y.qq.com")
        {
            var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, @"^/n/ryqq/songDetail/([A-Za-z0-9]+)$|^/n/yqq/song/([A-Za-z0-9]+)[.]html$");
            if (match.Success) id = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        }
        return id is not null && ValidId(platform, id) ? id : null;
    }
}
