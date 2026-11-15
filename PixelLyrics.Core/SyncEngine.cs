namespace PixelLyrics.Core;

// 引擎状态及歌词结果的提交由同一把锁串行化，允许后台加载任务提交结果。
public sealed class SyncEngine
{
    private readonly object gate = new();
    private readonly PlaybackClock clock;
    private long generation;
    private SongIdentity? currentSong;
    private LyricsDocument? document;
    private PixelLyricsSettings settings = new();

    public SyncEngine(PlaybackClock? clock = null) => this.clock = clock ?? new PlaybackClock();

    public long Generation { get { lock (gate) return generation; } }
    public SongIdentity? CurrentSong { get { lock (gate) return currentSong; } }
    public LyricsDocument? Document { get { lock (gate) return document; } }
    public PixelLyricsSettings Settings
    {
        get { lock (gate) return settings; }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (gate) settings = value;
        }
    }

    // 每次调用均开始新一代，包括重新加载同一首歌。
    public long ChangeSong(SongIdentity? song)
    {
        lock (gate)
        {
            generation = checked(generation + 1);
            currentSong = song;
            document = null;
            clock.Reset();
            return generation;
        }
    }

    public bool TryApplyLyrics(long resultGeneration, LyricsDocument lyrics)
    {
        ArgumentNullException.ThrowIfNull(lyrics);
        lock (gate)
        {
            if (currentSong is null || resultGeneration != generation) return false;
            document = lyrics;
            return true;
        }
    }

    // 锚点也绑定 generation，防止旧歌曲的位置回调污染新歌曲。
    public bool TrySetAnchor(long resultGeneration, TimeSpan position, bool isPaused = false)
    {
        lock (gate)
        {
            if (currentSong is null || resultGeneration != generation) return false;
            clock.SetAnchor(position, isPaused);
            return true;
        }
    }

    public bool TrySetPaused(long resultGeneration, bool isPaused)
    {
        lock (gate)
        {
            if (currentSong is null || resultGeneration != generation) return false;
            return clock.SetPaused(isPaused);
        }
    }

    public bool SetPaused(bool isPaused)
    {
        lock (gate) return clock.SetPaused(isPaused);
    }

    public LyricLine? GetCurrentLine()
    {
        lock (gate)
        {
            if (document is null || !clock.TryGetPosition(out var position)) return null;
            long offset = settings.GlobalOffset.Ticks;
            if (offset > 0 && position.Ticks < long.MinValue + offset) return null;
            if (offset < 0 && position.Ticks > long.MaxValue + offset)
                return document.GetLineAt(TimeSpan.MaxValue);
            return document.GetLineAt(TimeSpan.FromTicks(position.Ticks - offset));
        }
    }
}
