using System.Diagnostics;

namespace PixelLyrics.Core;

public sealed class PlaybackClock
{
    public static readonly TimeSpan AnchorLifetime = TimeSpan.FromSeconds(5);
    private readonly Func<long> monotonicTicks;
    private long anchorTicks;
    private long positionTicks;
    private TimeSpan anchorPosition;
    private bool hasAnchor;

    public bool IsPaused { get; private set; }

    // 注入函数必须返回单调递增的 TimeSpan ticks，而非 Stopwatch 原始 ticks。
    public PlaybackClock(Func<long>? monotonicTicks = null)
    {
        this.monotonicTicks = monotonicTicks ?? (() => Stopwatch.GetElapsedTime(0).Ticks);
    }

    public void SetAnchor(TimeSpan position, bool isPaused = false)
    {
        if (position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(position));
        anchorPosition = position;
        anchorTicks = monotonicTicks();
        positionTicks = anchorTicks;
        IsPaused = isPaused;
        hasAnchor = true;
    }

    public bool TryGetPosition(out TimeSpan position) => TryGetPositionAt(monotonicTicks(), out position);

    private bool TryGetPositionAt(long now, out TimeSpan position)
    {
        position = default;
        if (!hasAnchor) return false;
        if (now < anchorTicks) return false;
        ulong elapsed = unchecked((ulong)now - (ulong)anchorTicks);
        if (elapsed > (ulong)AnchorLifetime.Ticks) return false;
        long delta = IsPaused ? 0 : now - positionTicks;
        if (anchorPosition.Ticks > long.MaxValue - delta) return false;
        position = TimeSpan.FromTicks(anchorPosition.Ticks + delta);
        return true;
    }

    // 暂停或恢复不刷新锚点有效期；过期后只能由 SetAnchor 恢复。
    public bool SetPaused(bool isPaused)
    {
        long now = monotonicTicks();
        if (!TryGetPositionAt(now, out var position)) return false;
        if (IsPaused == isPaused) return true;
        anchorPosition = position;
        positionTicks = now;
        IsPaused = isPaused;
        return true;
    }

    public void Reset()
    {
        hasAnchor = false;
        IsPaused = false;
        anchorPosition = default;
        anchorTicks = 0;
    }
}
