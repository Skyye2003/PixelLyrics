namespace PixelLyrics.Probe;

// Events signal intent; only the follow loop writes USB.
internal sealed class RunLifecycle
{
    private readonly SemaphoreSlim changed = new(0, 1);
    private void Wake() { if (changed.CurrentCount == 0) { try { changed.Release(); } catch (SemaphoreFullException) { } } }
    internal async Task<bool> WaitAsync(int milliseconds, CancellationToken token) { await changed.WaitAsync(milliseconds, token); return true; }
    internal static bool ShouldAutoStart(bool firstLoad, bool saving, bool previouslyEnabled, bool enabled, bool valid)
        => valid && enabled && (firstLoad || saving && !previouslyEnabled);
    private int paused;
    private int revision;
    private int deviceRevision;
    private readonly ManualResetEventSlim acknowledged = new(false);
    internal bool IsPaused => Volatile.Read(ref paused) != 0;
    internal int Revision => Volatile.Read(ref revision);
    internal int DeviceRevision => Volatile.Read(ref deviceRevision);
    internal void DeviceChanged() { Interlocked.Increment(ref deviceRevision); Wake(); }
    internal void Pause()
    {
        acknowledged.Reset();
        Interlocked.Exchange(ref paused, 1);
        Interlocked.Increment(ref revision);
        Wake();
    }
    internal void EndSession() { Pause(); acknowledged.Wait(TimeSpan.FromSeconds(2)); }
    internal void Resume() { Interlocked.Exchange(ref paused, 0); Interlocked.Increment(ref revision); DeviceChanged(); }
    internal void Acknowledge() => acknowledged.Set();
}
