namespace PixelLyrics.Probe;

internal enum MusicPlayer { QQMusic, NetEaseMusic }
internal enum PlayerMode { Auto, QQMusic, NetEaseMusic }

internal static class MusicPlayers
{
    internal static string DisplayName(this PlayerMode mode) => mode == PlayerMode.Auto ? "自动识别" : mode == PlayerMode.NetEaseMusic ? "网易云音乐" : "QQ 音乐";
    internal static PlayerMode Mode(this MusicPlayer player) => player == MusicPlayer.NetEaseMusic ? PlayerMode.NetEaseMusic : PlayerMode.QQMusic;
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

// Selection uses playback state only; timeline validity is checked after selection, never used to
// silently substitute a paused player for the only playing player.
internal sealed class PlayerSelector
{
    private MusicPlayer? candidate;
    private TimeSpan since;
    internal MusicPlayer? Bound { get; private set; }
    internal string Notice { get; private set; } = "等待播放器开始播放";
    internal MusicPlayer? Observe(PlayerMode mode, bool qqPlaying, bool netEasePlaying, TimeSpan now)
    {
        Notice = "";
        MusicPlayer? next;
        if (mode != PlayerMode.Auto) next = mode == PlayerMode.NetEaseMusic ? MusicPlayer.NetEaseMusic : MusicPlayer.QQMusic;
        else if (qqPlaying && netEasePlaying)
        {
            candidate = null;
            Notice = Bound is null ? "两个播放器同时播放；请选择播放器并保存" : "两个播放器同时播放；保持当前来源";
            return Bound;
        }
        else if (!qqPlaying && !netEasePlaying)
        {
            candidate = null;
            Notice = Bound is null ? "等待播放器开始播放" : "等待恢复播放";
            return Bound;
        }
        else next = qqPlaying ? MusicPlayer.QQMusic : MusicPlayer.NetEaseMusic;
        if (next == Bound) { candidate = null; return Bound; }
        if (mode != PlayerMode.Auto) { Bound = next; candidate = null; return Bound; }
        if (candidate != next) { candidate = next; since = now; }
        if (now - since >= TimeSpan.FromSeconds(1)) { Bound = next; candidate = null; }
        else Notice = "等待播放来源稳定";
        return Bound;
    }
}
