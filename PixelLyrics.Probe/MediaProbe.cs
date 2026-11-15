using System.Diagnostics;
using PixelLyrics.Core;
using Windows.Media.Control;

namespace PixelLyrics.Probe;

internal sealed record MediaSnapshot(SongIdentity Song, TimeSpan Position, TimeSpan Start,
    TimeSpan End, DateTimeOffset Updated, GlobalSystemMediaTransportControlsSessionPlaybackStatus Status,
    double Rate, long Revision, bool Stable, TimeSpan TimelineAgeAtSample, long SampleTimestamp)
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
    private readonly GlobalSystemMediaTransportControlsSession? selected;
    private long revision;

    public MediaProbe(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        this.manager = manager;
        var sessions = manager.GetSessions();
        Console.WriteLine($"媒体会话：{sessions.Count}");
        foreach (var session in sessions) Console.WriteLine($"  AppId={session.SourceAppUserModelId}");
        var matches = sessions.Where(IsQqMusic).ToArray();
        if (matches.Length == 1)
        {
            selected = matches[0];
            selected.MediaPropertiesChanged += OnMediaChanged;
        }
        else Console.WriteLine($"QQMusic 匹配数为 {matches.Length}，不自动选择或绑定歌词。");
    }

    private static bool IsQqMusic(GlobalSystemMediaTransportControlsSession session) =>
        session.SourceAppUserModelId.Contains("qqmusic", StringComparison.OrdinalIgnoreCase);

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        Interlocked.Increment(ref revision);

    public async Task<MediaSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        var matches = manager.GetSessions().Where(IsQqMusic).ToArray();
        if (selected is null || matches.Length != 1 || !selected.Equals(matches[0])) return null;
        long before = Interlocked.Read(ref revision);
        var first = await selected.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
        var timeline = selected.GetTimelineProperties();
        var timelineAge = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
        long sampleTimestamp = Stopwatch.GetTimestamp();
        var playback = selected.GetPlaybackInfo();
        var second = await selected.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
        bool stable = before == Interlocked.Read(ref revision)
            && first.Title == second.Title && first.Artist == second.Artist && first.AlbumTitle == second.AlbumTitle
            && !string.IsNullOrWhiteSpace(second.Title)
            && timeline.StartTime >= TimeSpan.Zero && timeline.EndTime > timeline.StartTime
            && timeline.Position >= timeline.StartTime && timeline.Position <= timeline.EndTime;
        var duration = timeline.EndTime >= timeline.StartTime ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero;
        return new(new SongIdentity(second.Title, second.Artist, second.AlbumTitle, duration),
            timeline.Position, timeline.StartTime, timeline.EndTime, timeline.LastUpdatedTime,
            playback.PlaybackStatus, playback.PlaybackRate ?? 1d, before, stable, timelineAge, sampleTimestamp);
    }

    public bool IsCurrent(MediaSnapshot snapshot) => snapshot.Revision == Interlocked.Read(ref revision)
        && selected is not null && manager.GetSessions().Where(IsQqMusic).ToArray() is var matches
        && matches.Length == 1 && selected.Equals(matches[0]);

    public void Dispose()
    {
        if (selected is not null) selected.MediaPropertiesChanged -= OnMediaChanged;
    }
}
