namespace PixelLyrics.Probe;

internal enum MusicPlayer { QQMusic, NetEaseMusic }

internal static class MusicPlayers
{
    internal static string DisplayName(this MusicPlayer player) => player == MusicPlayer.NetEaseMusic ? "网易云音乐" : "QQ 音乐";
    internal static bool Matches(this MusicPlayer player, string appId) => player == MusicPlayer.NetEaseMusic
        ? NativeMediaDiagnostic.IsNetEase(appId) : appId.Contains("qqmusic", StringComparison.OrdinalIgnoreCase);
    internal static string? NetEaseId(IEnumerable<string> genres)
    {
        var ids = genres.Where(g => g.StartsWith("NCM-", StringComparison.Ordinal)).Select(g => g[4..]).Distinct().ToArray();
        return ids.Length == 1 && ValidNetEaseId(ids[0]) ? ids[0] : null;
    }
    internal static bool ValidNetEaseId(string id) => id.Length is > 0 and <= 19
        && id.All(char.IsAsciiDigit) && long.TryParse(id, out long value) && value > 0;
}
