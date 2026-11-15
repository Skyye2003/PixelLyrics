using System.Diagnostics;
using System.Text.Json;
using Windows.Media.Control;

namespace PixelLyrics.Probe;

// Read-only compatibility capture: no lyric client, device or configuration access.
internal static class NativeMediaDiagnostic
{
    internal static bool IsNetEase(string appId) =>
        appId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase)
        || appId.Contains("netease", StringComparison.OrdinalIgnoreCase)
        || appId.Contains("orpheus", StringComparison.OrdinalIgnoreCase)
        || appId.Contains("网易云", StringComparison.Ordinal);

    internal static bool HasUsableTimeline(string title, string artist, string album,
        TimeSpan start, TimeSpan end, TimeSpan position, TimeSpan age, double rate) =>
        !string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist)
        && !string.IsNullOrWhiteSpace(album) && start >= TimeSpan.Zero && end > start
        && position >= start && position <= end && age >= TimeSpan.Zero
        && age <= TimeSpan.FromSeconds(5) && double.IsFinite(rate) && rate >= 0;

    internal static async Task<int> RunAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(80));
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
        try
        {
            var versions = new List<string>();
            foreach (var process in Process.GetProcessesByName("cloudmusic"))
            {
                using (process)
                {
                    try
                    {
                        string? version = process.MainModule?.FileVersionInfo.FileVersion;
                        if (version is not null) versions.Add(version);
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            Write(new { Kind = "environment", Windows = Environment.OSVersion.VersionString,
                NetEaseVersions = versions.Distinct().ToArray(), Seconds = 65,
                Note = "只读原生会话；未验证客户端是否加载插件。样本可用不代表兼容性验收通过。" });
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                .AsTask(cancellation.Token);
            var clock = Stopwatch.StartNew();
            int netEaseSamples = 0, usableSamples = 0;
            for (int sample = 0; sample <= 65; sample++)
            {
                var allSessions = manager.GetSessions();
                var sessions = allSessions.Where(s => IsNetEase(s.SourceAppUserModelId)).ToArray();
                Write(new { Kind = "sample", Sample = sample, ElapsedMs = clock.ElapsedMilliseconds,
                    AppIds = allSessions.Select(s => s.SourceAppUserModelId).ToArray(),
                    NetEaseSessions = sessions.Length, QqSessions = allSessions.Count(s =>
                        s.SourceAppUserModelId.Contains("qqmusic", StringComparison.OrdinalIgnoreCase)) });
                foreach (var session in sessions)
                {
                    var first = await session.TryGetMediaPropertiesAsync().AsTask(cancellation.Token);
                    var timeline = session.GetTimelineProperties();
                    var playback = session.GetPlaybackInfo();
                    var second = await session.TryGetMediaPropertiesAsync().AsTask(cancellation.Token);
                    var age = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                    bool consistent = first.Title == second.Title && first.Artist == second.Artist
                        && first.AlbumTitle == second.AlbumTitle;
                    bool usable = sessions.Length == 1 && consistent && HasUsableTimeline(second.Title,
                        second.Artist, second.AlbumTitle, timeline.StartTime, timeline.EndTime,
                        timeline.Position, age, playback.PlaybackRate ?? 1d);
                    netEaseSamples++;
                    if (usable) usableSamples++;
                    Write(new { Kind = "session", Sample = sample, AppId = session.SourceAppUserModelId,
                        second.Title, second.Artist, Album = second.AlbumTitle,
                        NetEaseSongIds = second.Genres.Where(g => g.StartsWith("NCM-", StringComparison.Ordinal)).ToArray(),
                        PositionMs = timeline.Position.TotalMilliseconds, StartMs = timeline.StartTime.TotalMilliseconds,
                        EndMs = timeline.EndTime.TotalMilliseconds, Updated = timeline.LastUpdatedTime,
                        AgeMs = age.TotalMilliseconds, Status = playback.PlaybackStatus.ToString(),
                        Rate = playback.PlaybackRate, Consistent = consistent, Usable = usable });
                }
                if (sample < 65) await Task.Delay(1000, cancellation.Token);
            }
            Write(new { Kind = "summary", NetEaseSamples = netEaseSamples, UsableSamples = usableSamples,
                Gate = "未验收：还需核对连续播放、暂停恢复、前后拖动、切歌及双播放器场景。" });
            return netEaseSamples == 0 || usableSamples == 0 ? 2 : 0;
        }
        catch (OperationCanceledException)
        {
            Write(new { Kind = "incomplete", Reason = "诊断取消或超时，兼容性未通过。" });
            return 2;
        }
        catch (Exception ex)
        {
            Write(new { Kind = "unavailable", Error = ex.GetType().Name,
                Reason = "无法读取原生媒体会话，不能认定客户端不支持。请在交互桌面重新诊断。" });
            return 3;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
