using System.Diagnostics;
using PixelLyrics.Core;
using Windows.Media.Control;
using Microsoft.Win32;

namespace PixelLyrics.Probe;

internal sealed record MediaSnapshot(SongIdentity Song, TimeSpan Position, TimeSpan Start,
    TimeSpan End, DateTimeOffset Updated, GlobalSystemMediaTransportControlsSessionPlaybackStatus Status,
    double Rate, long Revision, bool Stable, TimeSpan TimelineAgeAtSample, long SampleTimestamp, string? SourceSongId = null)
{
    public TimeSpan TimelineAge => TimelineAgeAtSample + Stopwatch.GetElapsedTime(SampleTimestamp);
    public bool IsStale => TimelineAge > TimeSpan.FromSeconds(5);

    public TimeSpan? GetPosition()
    {
        var elapsed = TimelineAge;
        if (!Stable || elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(5)) return null;
        if (Status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return Position;
        if (!double.IsFinite(Rate) || Rate < 0) return null;
        double ticks = Position.Ticks + elapsed.Ticks * Rate;
        return TimeSpan.FromTicks((long)Math.Clamp(ticks, Start.Ticks, End.Ticks));
    }
}

internal sealed class MediaProbe : IDisposable
{
    private readonly GlobalSystemMediaTransportControlsSessionManager manager;
    private GlobalSystemMediaTransportControlsSession? selected;
    private int resumePending;
    private long revision;
    private readonly MusicPlayer player;
    internal string UnavailableReason
    {
        get
        {
            int count = manager.GetSessions().Count(s => player.Matches(s.SourceAppUserModelId));
            return count != 1 ? $"{player.DisplayName()}会话匹配数为 {count}；请保留一个播放会话后停止并重新开始"
                : $"{player.DisplayName()}会话或进度不可用；请确认进度组件兼容并停止后重新开始";
        }
    }

    public MediaProbe(GlobalSystemMediaTransportControlsSessionManager manager, MusicPlayer player = MusicPlayer.QQMusic)
    {
        this.manager = manager;
        this.player = player;
        var sessions = manager.GetSessions();
        Console.WriteLine($"媒体会话：{sessions.Count}");
        foreach (var session in sessions) Console.WriteLine($"  AppId={session.SourceAppUserModelId}");
        var matches = sessions.Where(s => player.Matches(s.SourceAppUserModelId)).ToArray();
        if (matches.Length == 1)
        {
            selected = matches[0];
            selected.MediaPropertiesChanged += OnMediaChanged;
        }
        else Console.WriteLine($"{player.DisplayName()}匹配数为 {matches.Length}，不自动选择或绑定歌词。");
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode != PowerModes.Resume) return;
        Interlocked.Increment(ref revision);
        Interlocked.Exchange(ref resumePending, 1);
    }

    internal static T? SelectSession<T>(T? current, T[] matches, bool afterResume) where T : class =>
        matches.Length == 1 && (afterResume || Equals(current, matches[0])) ? matches[0] : null;

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        Interlocked.Increment(ref revision);

    public async Task<MediaSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        var matches = manager.GetSessions().Where(s => player.Matches(s.SourceAppUserModelId)).ToArray();
        if (Interlocked.Exchange(ref resumePending, 0) != 0)
        {
            if (selected is not null) selected.MediaPropertiesChanged -= OnMediaChanged;
            selected = SelectSession(selected, matches, afterResume: true);
            Interlocked.Increment(ref revision);
            if (selected is not null) selected.MediaPropertiesChanged += OnMediaChanged;
            else Interlocked.Exchange(ref resumePending, 1);
        }
        var active = SelectSession(selected, matches, afterResume: false);
        if (active is null) return null;
        long before = Interlocked.Read(ref revision);
        var first = await active.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
        var timeline = active.GetTimelineProperties();
        var timelineAge = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
        long sampleTimestamp = Stopwatch.GetTimestamp();
        var playback = active.GetPlaybackInfo();
        var second = await active.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
        bool stable = before == Interlocked.Read(ref revision)
            && (player != MusicPlayer.NetEaseMusic || MusicPlayers.NetEaseId(first.Genres) == MusicPlayers.NetEaseId(second.Genres))
            && first.Title == second.Title && first.Artist == second.Artist && first.AlbumTitle == second.AlbumTitle
            && !string.IsNullOrWhiteSpace(second.Title)
            && timeline.StartTime >= TimeSpan.Zero && timeline.EndTime > timeline.StartTime
            && timeline.Position >= timeline.StartTime && timeline.Position <= timeline.EndTime;
        var duration = timeline.EndTime >= timeline.StartTime ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero;
        return new(new SongIdentity(second.Title, second.Artist, second.AlbumTitle, duration),
            timeline.Position, timeline.StartTime, timeline.EndTime, timeline.LastUpdatedTime,
            playback.PlaybackStatus, playback.PlaybackRate ?? 1d, before, stable, timelineAge, sampleTimestamp,
            player == MusicPlayer.NetEaseMusic ? MusicPlayers.NetEaseId(second.Genres) : null);
    }

    public bool IsCurrent(MediaSnapshot snapshot) => snapshot.Revision == Interlocked.Read(ref revision)
        && Volatile.Read(ref resumePending) == 0
        && selected is not null && manager.GetSessions().Where(s => player.Matches(s.SourceAppUserModelId)).ToArray() is var matches
        && matches.Length == 1 && selected.Equals(matches[0]);

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (selected is not null) selected.MediaPropertiesChanged -= OnMediaChanged;
    }
}
