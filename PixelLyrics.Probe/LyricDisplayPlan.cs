using PixelBar.Sdk.Protocol;

namespace PixelLyrics.Probe;

internal sealed record LyricDisplayPlan(string Text, TextDisplayEffect Effect)
{
    internal static LyricDisplayPlan Create(string text, TimeSpan elapsed, bool paused,
        int widthUnits = LyricViewport.WidthUnits, int pageHoldMs = LyricViewport.HoldMs)
    {
        // Elapsed is the playback position relative to this lyric, not wall-clock time.
        // A paused timeline therefore retains its page without firmware animation.
        // Never select native scrolling automatically: speed control is not verified.
        return new(LyricViewport.Frame(text, elapsed, widthUnits, pageHoldMs), TextDisplayEffect.Static);
    }
}
