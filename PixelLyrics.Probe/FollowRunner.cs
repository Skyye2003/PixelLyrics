using System.Diagnostics;
using System.Net.Http;
using PixelLyrics.Core;
using PixelLyrics.OnlineProbe;
using Windows.Media.Control;

namespace PixelLyrics.Probe;

// All state is advanced on one loop. Background requests return values only: never write USB or settings.
internal static class FollowRunner
{
    internal sealed record LoadedLyrics(LyricsDocument Document, string Mid, string? Alias);
    internal sealed class MatchUnavailableException(MatchCategory category) : Exception
    {
        internal MatchCategory Category { get; } = category;
    }

    internal static async Task<LoadedLyrics> LoadAsync(SongIdentity song, ProbeSongBinding? binding,
        CancellationToken token, Action? ensureNetworkAllowed = null, ILyricsSource? testClient = null,
        int requestTimeoutMs = 10000, MusicPlayer player = MusicPlayer.QQMusic, string? sourceSongId = null,
        Func<Task>? beforeRequest = null, RequestRecovery? recovery = null, Action<RequestNotice>? report = null, LyricSourceMapping? mapping = null)
    {
        ensureNetworkAllowed?.Invoke();
        if (mapping is null && (string.IsNullOrWhiteSpace(song.Artist) || string.IsNullOrWhiteSpace(song.Album)))
            throw new MatchUnavailableException(MatchCategory.InvalidMetadata);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (requestTimeoutMs is < 2000 or > 30000) throw new ArgumentOutOfRangeException(nameof(requestTimeoutMs));

        if (mapping is not null) { player = mapping.Platform; binding = new(mapping.SongId); sourceSongId = null; }
        using ILyricsSource? ownedClient = testClient is not null ? null : player == MusicPlayer.NetEaseMusic
            ? new NetEaseLyricsClient(requestTimeoutMs) : new OnlineLyricsClient(requestTimeoutMs);
        ILyricsSource client = testClient ?? ownedClient!;
        if (player == MusicPlayer.NetEaseMusic && sourceSongId is not null) binding = new(sourceSongId);
        if (client is OnlineLyricsClient qq) { if (recovery is not null) qq.Recovery = recovery; if (beforeRequest is not null) qq.BeforeRequest = _ => beforeRequest(); qq.Report = report; }
        if (client is NetEaseLyricsClient ne) { if (recovery is not null) ne.Recovery = recovery; if (beforeRequest is not null) ne.BeforeRequest = _ => beforeRequest(); ne.Report = report; }
        bool guardsRequests = client is OnlineLyricsClient or NetEaseLyricsClient;
        SongSelection selection;
        if (binding is null)
            selection = await client.RecallAsync(song.Title, song.Artist, song.Album,
                song.Duration.TotalSeconds, deadline.Token, ensureNetworkAllowed, beforeRequest: guardsRequests ? null : beforeRequest);
        else
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!guardsRequests && beforeRequest is not null) await beforeRequest();
            ensureNetworkAllowed?.Invoke();
            try
            {
                var details = await client.GetDetailsAsync(binding.Mid, deadline.Token);
                if (mapping is not null)
                {
                    var verified = SongMatcher.EvaluateTrial(details, song.Title, song.Artist, song.Album, song.Duration.TotalSeconds);
                    selection = new(verified.Category, verified.Category == MatchCategory.Accepted ? verified : null, [verified]);
                }
                else selection = SongMatcher.Rank([details], song.Title, song.Artist, song.Album,
                    song.Duration.TotalSeconds, binding.ArtistAlias);
            }
            catch (CandidateMetadataException) { throw new MatchUnavailableException(MatchCategory.InvalidMetadata); }
        }
        if (selection.Winner is null) throw new MatchUnavailableException(selection.Category);
        deadline.Token.ThrowIfCancellationRequested();
        if (!guardsRequests && beforeRequest is not null) await beforeRequest();
        ensureNetworkAllowed?.Invoke();
        var result = await client.FetchAsync(selection.Winner.Details.Mid, deadline.Token);
        token.ThrowIfCancellationRequested();
        return new(result.Document, selection.Winner.Details.Mid, binding?.ArtistAlias);
    }

    public static async Task<int> RunAsync(ProbeOptions options, MediaProbe media, ScreenOutput? screen,
        ProbeSettings settings, CancellationToken token, DesktopSession? session = null)
    {
        session ??= new DesktopSession();
        var runId = session.Latest.RunId;
        LyricAttempt? pendingAttempt = null;
        string checkState = "等待曲目稳定";
        Program.ReportDesktopLog($"自动跟随已启用：选择模式为{settings.Mode.DisplayName()}；切换来源取消旧请求。临时故障自动重试，单曲异常不影响下一首；不输出歌词正文。自动运行授权由独立开关控制。");
        var started = session.Clock;
        media.Mode = settings.Mode;
        session.RememberBindings(settings);
        var gate = new FollowGeneration(settings.TrackSettleMs);
        var fallback = new NoLyricsFallback();
        var idleClock = new IdleClock(settings.IdleClockEnabled, settings.IdleClockDelayMs);
        var intro = new TrackIntro(settings.TrackIntroEnabled, settings.TrackIntroHoldMs);
        CancellationTokenSource? requestCancellation = null;
        Task<LoadedLyrics>? pending = null;
        long pendingGeneration = -1;
        int pendingLifecycle = -1;
        string? pendingSourceId = null;
        MusicPlayer pendingPlayer = MusicPlayer.QQMusic;
        LyricsDocument? lyrics = null;
        MediaSnapshot? previous = null;
        long lastAdvance = 0;
        int advanceCount = 0;
        bool attempted = false;
        int lifecycleRevision = session.Lifecycle.Revision;
        bool unavailable = false;
        bool suppressIntro = false;
        bool hadTrack = false;
        bool pauseClockSent = false;
        var recoveries = session.Recoveries;
        LyricSourceMapping? pendingMapping = null;
        var reminder = new SourceReminder();
        string? occurrence = null;
        async Task<LoadedLyrics> LoadGuarded(SongIdentity song, ProbeSongBinding? binding, CancellationToken requestToken, string? sourceSongId, MusicPlayer source, int timeout, LyricAttempt attempt, long generation)
        {
            int requestLifecycle = session.Lifecycle.Revision;
            var mapping = pendingMapping;
            var lyricsSource = mapping?.Platform ?? source;
            try
            {
                return await LoadAsync(song, binding, requestToken, () =>
                {
                    requestToken.ThrowIfCancellationRequested();
                    if (session.Lifecycle.IsPaused || requestLifecycle != session.Lifecycle.Revision) throw new OperationCanceledException();

                }, requestTimeoutMs: timeout, player: source, sourceSongId: sourceSongId, recovery: recoveries[lyricsSource], mapping: mapping,
                    report: notice => { if (session.CanApply(attempt)) { session.Update(s => s with { Check = notice.Message }); Program.ReportDesktopLog($"{source.DisplayName()}；歌曲 ID {SafeSongId(sourceSongId ?? binding?.Mid)}；{notice.Stage}；尝试 {notice.Attempt}；{notice.Message}"); } },
                    beforeRequest: async () =>
                    {
                        var current = await media.ReadAsync(requestToken);
                        if (current is not { Stable: true } || !media.IsCurrent(current)
                            || current.Player != source || current.Song.Id != song.Id || current.SourceSongId != sourceSongId
                            || current.GetPosition() is null || !session.CanApply(attempt)
                            || !gate.CanApply(generation, current.Song.Id, current.Revision))
                            throw new OperationCanceledException();
                    });
            }
            catch (LyricsRequestException ex)
            {
                if (session.CanApply(attempt))
                    Program.ReportDesktopLog($"请求失败：{source.DisplayName()}；歌曲 ID {SafeSongId(sourceSongId ?? binding?.Mid)}；{ex.Stage}；{ex.Kind}；{ex.Message}");
                throw;
            }
        }

        string fallbackMessage = settings.TrackIntroEnabled
            ? "；开场结束后切时钟，不追加歌名停留，下一首将自动继续。"
            : "；显示当前歌名后切时钟，下一首将自动继续。";
        async Task ArmFallbackAsync()
        {
            var check = await media.ReadAsync(token);
            if (check is { Stable: true } && media.IsCurrent(check)
                && session.CanApply(pendingAttempt) && gate.CanApply(pendingGeneration, check.Song.Id, check.Revision))
                fallback.Start(check.Song.Title, started.Elapsed);
        }
        try
        {
            do
            {
                token.ThrowIfCancellationRequested();
                if (options.DurationSeconds is int duration && started.Elapsed >= TimeSpan.FromSeconds(duration)) break;
                bool updateClock = false;
                bool applied = session.ApplyParameters(parameters =>
                {
                    var old = DesktopParameters.From(settings);
                    parameters.ApplyTo(settings);
                    media.Mode = settings.Mode;
                    gate.Configure(settings.TrackSettleMs);
                    intro.Configure(settings.TrackIntroEnabled, settings.TrackIntroHoldMs, started.Elapsed);
                    if (old.IdleClock != parameters.IdleClock || old.IdleDelay != parameters.IdleDelay)
                        idleClock.Configure(settings.IdleClockEnabled, settings.IdleClockDelayMs);
                    if (screen is not null)
                    {
                        screen.DisplayWidthUnits = settings.DisplayWidthUnits;
                        screen.PageHoldMs = settings.PageHoldMs;
                        updateClock = options.Send && screen.ClockRequested && !screen.HasFailed && old.ClockStyle != parameters.ClockStyle;
                    }
                });
                if (updateClock && !session.Latest.Stopping && !session.Lifecycle.IsPaused)
                {
                    token.ThrowIfCancellationRequested();
                    screen!.ShowClock(settings.ClockStyle);
                    if (screen.HasFailed) session.Update(s => s with { Display = "设备写入失败；实际显示未知" });
                }
                if (applied) Program.ReportDesktopLog($"配置版本 {session.Latest.ParameterVersion} 已应用" + (screen?.HasFailed == true ? "；设备写入失败，实际显示未知。" : "。"));
                token.ThrowIfCancellationRequested();
                bool pausedBySystem = session.Lifecycle.IsPaused;
                bool deviceReady = !options.Send || (pausedBySystem ? screen!.Ready : screen!.RefreshConnection(started.Elapsed, session.Lifecycle.DeviceRevision));
                bool lifecycleChanged = lifecycleRevision != session.Lifecycle.Revision;
                if (lifecycleChanged || (!deviceReady || pausedBySystem) && !unavailable)
                {
                    lifecycleRevision = session.Lifecycle.Revision;
                    pauseClockSent = false;
                    requestCancellation?.Cancel();
                    if (pending is not null) ObserveAbandoned(pending);
                    pending = null; requestCancellation?.Dispose(); requestCancellation = null;
                    pendingAttempt = null; lyrics = null; attempted = false;
                    gate.Observe(null, -1, started.Elapsed);
                    fallback.Reset(); intro.Start(gate.Generation, null, started.Elapsed);
                    previous = null; advanceCount = 0; lastAdvance = 0;
                    suppressIntro = hadTrack;
                    Program.ReportDesktopLog(pausedBySystem ? "系统暂停：撤销旧请求并尝试显示时钟。" : "设备或系统状态变化：旧歌词及播放位置已失效。");
                }
                session.Update(s => s with { DeviceReady = deviceReady && !pausedBySystem });
                if (pausedBySystem && !pauseClockSent)
                {
                    if (options.Send && deviceReady) screen!.ShowClock(settings.ClockStyle);
                    pauseClockSent = true;
                    session.Lifecycle.Acknowledge();
                }
                if (!pausedBySystem) pauseClockSent = false;
                if (!deviceReady || pausedBySystem)
                {
                    unavailable = true;
                    session.Update(s => s with { Track = null, Busy = false, Settled = false,
                        Display = pausedBySystem ? "系统已暂停；恢复后重新校验播放进度" : screen!.ConnectionStatus });
                    continue;
                }
                if (unavailable)
                {
                    unavailable = false;
                    Program.ReportDesktopLog("连接已恢复：重新核对当前曲目及进度；保留接口限制。");
                }
                var snapshot = await media.ReadAsync(token);
                if (session.Latest.Source != snapshot?.Player)
                    Program.ReportDesktopLog($"来源变化：{session.Latest.Source?.DisplayName() ?? "未绑定"} → {snapshot?.Player.DisplayName() ?? "不可用"}；旧歌词及位置绑定失效。");
                bool usable = snapshot is not null && snapshot.Stable && media.IsCurrent(snapshot);
                string? identity = usable ? snapshot!.Song.Id : null;
                string? nextOccurrence = usable ? LyricSourceMappings.Key(snapshot!.Player, snapshot.Song.Id) : null;
                if (nextOccurrence is not null && occurrence != nextOccurrence)
                { occurrence = nextOccurrence; reminder.Reset(); session.SourceReminderSuppressed = false; }
                long revision = usable ? snapshot!.Revision : -1;
                if (gate.Observe(identity, revision, started.Elapsed))
                {
                    requestCancellation?.Cancel();
                    // Observe late faults without letting an old request modify new-song state.
                    if (pending is not null) ObserveAbandoned(pending);
                    pending = null;
                    requestCancellation?.Dispose();
                    requestCancellation = null;
                    lyrics = null;
                    pendingAttempt = null;
                    checkState = usable ? "等待曲目稳定" : media.UnavailableReason;
                    session.Update(s => s with { Track = usable ? new(runId, gate.Generation, snapshot!.Song.Id, snapshot.Revision) : null,
                        Title = usable ? snapshot!.Song.Title : null, Artist = usable ? snapshot!.Song.Artist : null,
                        Song = usable ? snapshot!.Song : null, NeedsSource = false, Trial = false, Mapping = usable ? settings.SourceMappings.GetValueOrDefault(LyricSourceMappings.Key(snapshot!.Player, snapshot.Song.Id)) : null,
                        Busy = false, Settled = false, Check = s.NetworkBlocked ? s.Check : checkState });
                    fallback.Reset();
                    intro.Start(gate.Generation, usable && !suppressIntro ? snapshot!.Song.Title : null, started.Elapsed,
                        usable ? snapshot!.Song.Artist : null);
                    if (usable) { suppressIntro = false; hadTrack = true; }
                    attempted = false;
                    advanceCount = 0;
                    lastAdvance = 0;
                    previous = null;
                    if (options.Send) screen!.ClearLyrics();
                    Program.ReportDesktopLog(identity is null ? "会话暂不可用：旧词已失效。" : "检测到曲目或媒体版本变化，等待稳定后核对歌词。");
                }
                if (screen?.HasFailed == true) { if (screen.ReconnectEnabled) continue; break; }
                session.Update(s => s with { Source = snapshot?.Player, SourceNotice = media.Notice, Settled = usable && gate.IsSettled(started.Elapsed),
                    Playback = usable ? snapshot!.Status.ToString() : "会话不可用",
                    Check = s.NetworkBlocked ? s.Check : !usable ? media.UnavailableReason
                        : snapshot!.GetPosition() is null ? "播放进度不可用或已过期；请恢复播放并确认进度组件正常"
                        : s.Check == "播放进度不可用或已过期；请恢复播放并确认进度组件正常" ? checkState : s.Check });
                if (session.TakeSource() is { } choice)
                {
                    try
                    {
                        if (session.Latest.Stopping || session.Latest.NetworkBlocked) { session.FinishSource(false); continue; }
                        string key = LyricSourceMappings.Key(choice.Playback, choice.Song.Id);
                        if (options.SettingsPath is null) throw new InvalidOperationException("缺少来源映射保存路径。");
                        if (choice.Trial && (session.Latest.Track != choice.Track || !usable || snapshot?.Player != choice.Playback || snapshot.GetPosition() is null || !session.Latest.DeviceReady || session.Lifecycle.IsPaused))
                        { session.FinishSource(false); continue; }
                        if (!choice.Trial) lock (ProbeSettings.FileSync)
                        {
                            var disk = ProbeSettings.Load(options.SettingsPath);
                            session.MergeBindings(disk);
                            if (choice.Mapping is null) disk.SourceMappings.Remove(key); else disk.SourceMappings[key] = choice.Mapping;
                            disk.Save(options.SettingsPath);
                            if (choice.Mapping is null) settings.SourceMappings.Remove(key); else settings.SourceMappings[key] = choice.Mapping;
                            session.RememberSource(key, choice.Mapping);
                        }
                        if (!choice.Trial && choice.Mapping is null) SourceHistory.Forget(options.SettingsPath, choice.Playback, choice.Song);
                        else SourceHistory.Record(options.SettingsPath, choice.Playback, choice.Song, choice.Mapping, !choice.Trial && choice.Mapping is not null);
                        if (session.Latest.Track == choice.Track && snapshot?.Player == choice.Playback)
                        {
                            requestCancellation?.Cancel();
                            if (pending is not null) ObserveAbandoned(pending);
                            pending = null; requestCancellation?.Dispose(); requestCancellation = null;
                            bool keepLyrics = !choice.Trial && choice.Mapping is not null && session.Latest.Mapping == choice.Mapping && lyrics is not null;
                            lyrics = keepLyrics ? lyrics : choice.Document; attempted = choice.Mapping is not null && lyrics is not null;
                            fallback.Reset();
                            checkState = choice.Trial && choice.Mapping is not null ? "正在试用 · 未确认" : choice.Mapping is null ? "重新自动核对" : "已记住所选来源";
                            session.Update(s => s with { Busy = false, NeedsSource = false, Trial = choice.Trial && choice.Mapping is not null, Mapping = choice.Mapping, Check = checkState, AttemptId = s.AttemptId + 1 });
                        }
                        session.FinishSource(true);
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
                    { Program.ReportDesktopLog("来源映射保存失败：" + ex.Message); session.FinishSource(false); }
                }
                var recovery = snapshot is null ? null : recoveries[session.Latest.Mapping?.Platform ?? snapshot.Player];
                var remaining = recovery?.Remaining ?? TimeSpan.Zero;
                bool accessDenied = recovery?.AccessDenied == true;
                if (remaining > TimeSpan.Zero && !fallback.Active) await ArmFallbackAsync();
                session.Update(s => s with { RecoveryWaiting = remaining > TimeSpan.Zero,
                    RecoveryManual = accessDenied,
                    Check = remaining > TimeSpan.Zero ? $"请求受限，{Math.Ceiling(remaining.TotalSeconds)} 秒后恢复" : accessDenied ? "接口访问受限，请重新尝试" : s.Check });
                if (session.TakeRecheck(session.Latest.Track, started.Elapsed))
                {
                    recovery?.RetryAccess();
                    attempted = false;
                    lyrics = null;
                    fallback.Reset();
                    if (options.Send) screen!.ClearLyrics();
                    // 保留 MID 绑定和原开场计时，不重播开场。
                }

                if (usable && snapshot!.GetPosition() is not null && !attempted && remaining == TimeSpan.Zero && !accessDenied && gate.IsSettled(started.Elapsed))
                {
                    pendingAttempt = session.BeginAttempt(new(runId, gate.Generation, snapshot!.Song.Id, snapshot.Revision));
                    if (pendingAttempt is null) continue;
                    attempted = true;
                    ProbeSongBinding? binding;
                    if (snapshot!.Player == MusicPlayer.NetEaseMusic)
                        binding = settings.NetEaseBindings.TryGetValue(snapshot!.Song.Id, out var id) ? new(id) : null;
                    else settings.Bindings.TryGetValue(snapshot!.Song.Id, out binding);
                    requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    pendingGeneration = gate.Generation;
                    pendingLifecycle = session.Lifecycle.Revision;
                    pendingSourceId = snapshot.SourceSongId;
                    pendingPlayer = snapshot.Player;
                    pendingMapping = settings.SourceMappings.GetValueOrDefault(LyricSourceMappings.Key(snapshot.Player, snapshot.Song.Id));
                    pending = LoadGuarded(snapshot.Song, binding, requestCancellation.Token, snapshot.SourceSongId, pendingPlayer, settings.RequestTimeoutMs, pendingAttempt, pendingGeneration);
                    Program.ReportDesktopLog("开始核对当前曲目；取词与可选歌名与歌手开场独立进行。");
                }
                if (pending is { IsCompleted: true })
                {
                    var completed = pending;
                    pending = null;
                    try
                    {
                        var loaded = await completed;
                        // Re-read after completion to close the request/track-change race.
                        var check = await media.ReadAsync(token);
                        if (usable && pendingLifecycle == session.Lifecycle.Revision && session.CanApply(pendingAttempt) && gate.CanApply(pendingGeneration, check?.Song.Id, check?.Revision ?? -1)
                            && check is { Stable: true } && check.Player == pendingPlayer && check.SourceSongId == pendingSourceId && media.IsCurrent(check))
                        {
                            token.ThrowIfCancellationRequested();
                            checkState = "已核对并在内存绑定歌词";
                            lyrics = loaded.Document;
                            fallback.Reset();
                            session.Update(s => s with { NeedsSource = false });
                            if (pendingMapping is null)
                            {
                                if (pendingPlayer == MusicPlayer.NetEaseMusic) settings.NetEaseBindings[check.Song.Id] = loaded.Mid;
                                else settings.SetVerifiedBinding(check.Song, loaded.Mid, loaded.Alias);
                            }
                            session.RememberBindings(settings);
                            if (options.SaveSettings)
                                lock (ProbeSettings.FileSync)
                                {
                                    var disk = ProbeSettings.Load(options.SettingsPath!);
                                    session.MergeBindings(disk);
                                    disk.Save(options.SettingsPath!);
                                }
                            snapshot = check;
                            Program.ReportDesktopLog($"当前曲目已绑定 {lyrics.Lines.Count} 行；歌词仅保存在内存。");
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        checkState = "取词已取消；可重新核对";
                        Program.ReportDesktopLog("取词已取消或超时，本媒体版本下一首将自动继续。");
                    }
                    catch (MatchUnavailableException ex)
                    {
                        session.Update(s => s with { NeedsSource = true });
                        checkState = MatchFailureReason(ex.Category);
                        Program.ReportDesktopLog(checkState + fallbackMessage);
                        await ArmFallbackAsync();
                    }
                    catch (UntimedLyricsException)
                    {
                        session.Update(s => s with { NeedsSource = true });
                        checkState = "当前曲目没有可同步的时间轴";
                        Program.ReportDesktopLog(checkState + "；不锁止联网，切歌后正常核对。" + fallbackMessage);
                        await ArmFallbackAsync();
                    }
                    catch (NoLyricsException)
                    {
                        session.Update(s => s with { NeedsSource = true });
                        checkState = "当前曲目无歌词";
                        Program.ReportDesktopLog(checkState + fallbackMessage);
                        await ArmFallbackAsync();
                    }
                    catch (LyricsRequestException ex)
                    {
                        if (ex.Kind == RequestFailureKind.RateLimited) { attempted = false; checkState = "请求受限，等待自动恢复"; }
                        else if (ex.Kind == RequestFailureKind.AccessDenied) checkState = "接口访问受限，请重新尝试";
                        else if (pendingMapping is not null && (ex.Kind == RequestFailureKind.Identity || ex.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone))
                        {
                            session.Update(s => s with { NeedsSource = true });
                            checkState = "已记住的来源不可用，请重新选择歌词来源";
                            Program.ReportDesktopLog(checkState + "；保留原来源映射。");
                        }
                        else checkState = ex.Message + "；当前曲目已跳过，下一首将自动继续";
                        await ArmFallbackAsync();
                    }
                    catch (ProbeException ex)
                    {
                        checkState = SafeFailureReason(ex) + "；当前曲目已跳过，下一首将自动继续";
                        Program.ReportDesktopLog(checkState);
                        await ArmFallbackAsync();
                    }
                    catch (HttpRequestException)
                    {
                        checkState = "网络请求失败；下一首将自动继续";
                        await ArmFallbackAsync();
                    }
                    finally
                    {
                        session.Complete(pendingAttempt, checkState, started.Elapsed);
                        requestCancellation?.Dispose();
                        requestCancellation = null;
                    }
                }
                usable = snapshot is not null && snapshot.Stable && media.IsCurrent(snapshot)
                    && gate.CanApply(gate.Generation, snapshot.Song.Id, snapshot.Revision);
                if (usable && snapshot!.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing
                    && previous is not null && previous.Song == snapshot.Song && previous.Revision == snapshot.Revision
                    && !snapshot.IsStale)
                {
                    var delta = snapshot.Position - previous.Position;
                    if (delta > TimeSpan.Zero && delta < TimeSpan.FromSeconds(1))
                    {
                        advanceCount++;
                        lastAdvance = Stopwatch.GetTimestamp();
                    }
                    else if (delta < TimeSpan.Zero || delta >= TimeSpan.FromSeconds(1)) advanceCount = 0;
                }
                else advanceCount = 0;
                previous = snapshot;
                bool playing = usable && (snapshot!.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    || (snapshot.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing
                        && advanceCount >= 3 && lastAdvance != 0
                        && Stopwatch.GetElapsedTime(lastAdvance) < TimeSpan.FromMilliseconds(700)));
                bool paused = usable && snapshot!.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
                var position = usable ? snapshot!.GetPosition() : null;

                if (session.Lifecycle.IsPaused || lifecycleRevision != session.Lifecycle.Revision) continue;
                if (options.Send)
                {
                    token.ThrowIfCancellationRequested();
                    bool inactive = !usable || snapshot!.Status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
                        or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                    if (!inactive && intro.IsShowing(gate.Generation, started.Elapsed) && media.IsCurrent(snapshot!))
                        screen!.ShowLyric(intro.Title, intro.Elapsed(started.Elapsed), paused,
                            snapshot!.Song.Id + ":intro:" + gate.Generation);
                    else if (!inactive && !paused && fallback.Active && media.IsCurrent(snapshot!))
                    {
                        if (intro.ReplacesFallbackTitle(gate.Generation)
                            ? !fallback.ClockSent
                            : fallback.ShouldSwitch(started.Elapsed, settings.NoLyricsTitleHoldMs))
                        {
                            if (session.Latest.NeedsSource && !session.SourceReminderSuppressed && reminder.Show(started.Elapsed))
                                screen!.ShowLyric("歌词待选择", reminder.Elapsed(started.Elapsed), false, snapshot!.Song.Id + ":source-reminder");
                            else
                            {
                                bool sent = screen!.ShowClock(settings.ClockStyle);
                                fallback.MarkClock();
                                Program.ReportDesktopLog(sent ? "无词降级：时钟指令已发送，实际显示及时间需确认。" : "无词降级：时钟发送失败，本轮停止写屏。");
                            }
                        }
                        else if (!fallback.ClockSent)
                            screen!.ShowLyric(fallback.Title, started.Elapsed - fallback.StartedAt, false, snapshot!.Song.Id + ":fallback");
                    }
                    else if (paused && (position is null || lyrics is null || fallback.Active))
                    {
                        // 暂停时可能没有新时间线；保留已显示内容，不把过期位置当成空闲。
                    }
                    else if (lyrics is null || position is null || (!playing && !paused)) screen!.ClearLyrics();
                    else if ((playing || paused) && media.IsCurrent(snapshot!))
                    {
                        var query = position.Value - TimeSpan.FromMilliseconds(settings.OffsetMs);
                        var line = query < TimeSpan.Zero ? null : lyrics.GetLineAt(query);
                        if (!paused || !string.IsNullOrWhiteSpace(line?.Text))
                            screen!.ShowLyric(line?.Text, line is null ? TimeSpan.Zero : query - line.Timestamp,
                                paused, line is null ? null : snapshot!.Song.Id + ":" + line.Timestamp.Ticks);
                    }
                    bool blank = !screen!.HasDisplayableLyrics;
                    bool waiting = !inactive && (pending is not null || !gate.IsSettled(started.Elapsed)
                        || intro.IsShowing(gate.Generation, started.Elapsed));
                    if (idleClock.Observe(gate.Generation, started.Elapsed, blank, paused, waiting,
                        token.IsCancellationRequested, fallback.ClockSent))
                    {
                        token.ThrowIfCancellationRequested();
                        bool sent = screen.ShowClock(settings.ClockStyle);
                        if (fallback.Active) fallback.MarkClock();
                        Program.ReportDesktopLog(sent
                            ? "持续空闲：时钟指令已发送一次；不代表无词或已校时，恢复歌词后正常覆盖。"
                            : "持续空闲：时钟发送失败，本轮停止写屏。");
                    }
                    if (screen.HasFailed)
                    {
                        session.Update(s => s with { Display = "设备写入失败；实际显示未知" });
                        if (screen.ReconnectEnabled) continue;
                        break;
                    }
                }
                session.Update(s => s with { Display = !options.Send ? "仅预览，未写屏"
                    : screen!.HasFailed ? "设备写入失败；实际显示未知"
                    : screen.ClockRequested ? "已请求时钟；实际显示待确认"
                    : intro.IsShowing(gate.Generation, started.Elapsed) ? "开场输出阶段；实际显示待确认"
                    : screen.HasDisplayableLyrics ? "歌词输出阶段；实际显示待确认"
                    : "无可显示歌词；实际显示待确认" });
            }
            while (await session.Lifecycle.WaitAsync(settings.PollIntervalMs, token));
        }
        finally
        {
            session.Stop();
            idleClock.Stop();
            requestCancellation?.Cancel();
            if (pending is not null) ObserveAbandoned(pending);
            requestCancellation?.Dispose();
            if (options.Send)
            {
                bool sent = screen!.ShowClock(settings.ClockStyle);
                Program.ReportDesktopLog(sent ? "停止：时钟指令已发送；实际显示待确认。" : "停止：未能发送时钟指令；设备可能仍保留旧内容。");
            }
            session.Lifecycle.Acknowledge();
        }
        return screen?.HasFailed == true ? 5 : 0;
    }

    internal static bool CandidateMatches(Song candidate, SongIdentity song)
    {
        return SongMatcher.IsPlausibleCandidate(candidate, song.Title);
    }

    internal static string MatchFailureReason(MatchCategory category) => category switch
    {
        MatchCategory.NoCandidates => "搜索无候选",
        MatchCategory.Ambiguous => "候选歧义：第一名领先不足",
        MatchCategory.BelowThreshold => "匹配置信度未达阈值",
        MatchCategory.InvalidMetadata => "曲目或候选元数据不完整",
        MatchCategory.TitleConflict => "无匹配：歌名主体冲突",
        MatchCategory.VersionConflict => "无匹配：重要版本冲突或一方缺省",
        MatchCategory.ArtistConflict => "无匹配：歌手身份冲突",
        MatchCategory.DurationConflict => "无匹配：时长差距过大",
        _ => "无可信匹配：歌名、版本、歌手或时长冲突"
    };

    private static string SafeSongId(string? id) => id is { Length: > 0 and <= 64 } && id.All(char.IsAsciiLetterOrDigit) ? id : "未知";

    internal static string SafeFailureReason(ProbeException error) => error.Message switch
    {
        _ when error is NetEaseLrcException lrc => "网易云 LRC 解析失败：" + lrc.Diagnostic,
        "缺少歌词。" => "响应缺少歌词字段",
        "无歌词。" => "当前曲目返回空歌词",
        "缺少搜索列表。" => "搜索响应没有歌曲列表",
        "业务 code 非零或格式错误。" => "接口业务码异常",
        "响应带加密/QRC 数据或标志，拒绝处理。" => "响应不是已支持的普通LRC格式",
        "HTTP 401；不跟随重定向。" => "HTTP 401，访问未授权",
        "HTTP 403；不跟随重定向。" => "HTTP 403，访问被拒绝",
        "网易云 LRC 无效。" => "网易云歌词时间标签或内容格式无效",
        "网易云响应缺少 LRC。" => "网易云响应缺少普通 LRC",
        "网易云接口业务码异常。" => "网易云接口业务码异常",
        "网易云响应格式错误。" => "网易云响应格式错误",
        "网易云详情 ID 不一致。" => "网易云歌曲详情 ID 不一致",
        "不是明文 LRC 或普通 Base64。" => "歌词响应既不是明文 LRC，也不是有效 Base64",
        "Base64 内容不是严格 UTF-8。" => "歌词 Base64 解码后不是有效 UTF-8",
        "响应层级不是对象。" => "接口响应顶层不是 JSON 对象",
        "缺少业务 code。" => "接口响应缺少业务码",
        "缺少对象：data" => "接口响应缺少 data 对象",
        "缺少对象：song" => "搜索响应缺少 song 对象",
        "缺少对象：req_1" => "接口响应缺少 req_1 对象",
        "网易云搜索列表缺失。" => "网易云搜索响应缺少歌曲列表",
        "网易云搜索格式错误。" => "网易云搜索歌曲列表格式错误",
        _ when System.Text.RegularExpressions.Regex.IsMatch(error.Message, @"\AHTTP [1-5][0-9]{2}；不跟随重定向。\z") => error.Message,
        _ when System.Text.RegularExpressions.Regex.IsMatch(error.Message, @"\A非有效普通 LRC（解析错误 [0-9]{1,6}）。\z") => "QQ 音乐歌词解析失败：" + error.Message,
        _ => "接口响应未通过协议校验"
    };

    private static void ObserveAbandoned(Task task) => _ = task.ContinueWith(t => _ = t.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}

// 仅显示状态，使用单调墙钟；不会调用播放器控制或阻塞取词。
internal sealed class TrackIntro(bool enabled, int holdMs)
{
    private long generation = -1;
    private TimeSpan startedAt;
    public string? Title { get; private set; }

    private bool ended;
    public void Configure(bool nextEnabled, int nextHold, TimeSpan now)
    {
        if (Title is null || Elapsed(now) >= TimeSpan.FromMilliseconds(holdMs)) ended = true;
        if (!nextEnabled) ended = true;
        enabled = nextEnabled; holdMs = nextHold;
    }
    public void Start(long currentGeneration, string? title, TimeSpan now, string? artist = null)
    {
        generation = currentGeneration;
        ended = false;
        Title = !enabled || title is null ? null
            : string.IsNullOrWhiteSpace(artist) ? title : $"{title} - {artist}";
        startedAt = now;
    }

    public TimeSpan Elapsed(TimeSpan now) => now < startedAt ? TimeSpan.Zero : now - startedAt;
    public bool ReplacesFallbackTitle(long currentGeneration) => generation == currentGeneration && Title is not null;
    public bool IsShowing(long currentGeneration, TimeSpan now)
    {
        if (Elapsed(now) >= TimeSpan.FromMilliseconds(holdMs)) ended = true;
        return enabled && !ended && ReplacesFallbackTitle(currentGeneration);
    }
}

// 纯状态计时器；返回 true 即消耗本次发送机会，失败也不重试。无后台计时任务。
internal sealed class IdleClock(bool enabled, int delayMs)
{
    private long generation = -1;
    private TimeSpan? blankSince;
    private bool clockSent;
    private bool stopped;

    public void Configure(bool nextEnabled, int nextDelay)
    {
        enabled = nextEnabled; delayMs = nextDelay; blankSince = null;
    }
    public bool Observe(long currentGeneration, TimeSpan now, bool blank, bool paused = false,
        bool waiting = false, bool exiting = false, bool otherClockSent = false)
    {
        if (exiting) Stop();
        if (stopped || !enabled) return false;
        if (generation != currentGeneration)
        {
            generation = currentGeneration;
            blankSince = null;
            clockSent = false;
        }
        if (otherClockSent)
        {
            blankSince = null;
            clockSent = true;
            return false;
        }
        if (!blank)
        {
            blankSince = null;
            clockSent = false;
            return false;
        }
        if (paused || waiting)
        {
            blankSince = null;
            return false;
        }
        if (clockSent) return false;
        blankSince ??= now;
        if (now - blankSince.Value < TimeSpan.FromMilliseconds(delayMs)) return false;
        clockSent = true;
        return true;
    }

    public void Stop() { stopped = true; blankSince = null; }
}

internal sealed class NoLyricsFallback
{
    public bool Active { get; private set; }
    public string Title { get; private set; } = "";
    public TimeSpan StartedAt { get; private set; }
    public bool ClockSent { get; private set; }
    public void Start(string title, TimeSpan now)
    {
        Title = title; StartedAt = now; Active = true; ClockSent = false;
    }
    public bool ShouldSwitch(TimeSpan now, int holdMs) => Active && !ClockSent
        && now - StartedAt >= TimeSpan.FromMilliseconds(holdMs);
    public void MarkClock() => ClockSent = true;
    public void Reset() { Active = false; ClockSent = false; Title = ""; }
}

internal sealed class FollowGeneration(int settleMs = 600)
{
    private string? identity;
    private long revision = -1;
    private TimeSpan changedAt;
    public long Generation { get; private set; }

    public bool Observe(string? songId, long mediaRevision, TimeSpan now)
    {
        if (identity == songId && revision == mediaRevision) return false;
        identity = songId;
        revision = mediaRevision;
        changedAt = now;
        Generation++;
        return true;
    }

    internal void Configure(int milliseconds) => settleMs = milliseconds;
    public bool IsSettled(TimeSpan now) => identity is not null && now - changedAt >= TimeSpan.FromMilliseconds(settleMs);
    public bool CanApply(long generation, string? songId, long mediaRevision) => identity is not null
        && Generation == generation && identity == songId && revision == mediaRevision;
}
