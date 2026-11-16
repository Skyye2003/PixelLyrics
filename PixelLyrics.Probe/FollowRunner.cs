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
        Func<Task>? beforeRequest = null)
    {
        ensureNetworkAllowed?.Invoke();
        if (string.IsNullOrWhiteSpace(song.Artist) || string.IsNullOrWhiteSpace(song.Album))
            throw new MatchUnavailableException(MatchCategory.InvalidMetadata);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (requestTimeoutMs is < 2000 or > 30000) throw new ArgumentOutOfRangeException(nameof(requestTimeoutMs));
        deadline.CancelAfter(TimeSpan.FromMilliseconds(requestTimeoutMs));
        using ILyricsSource? ownedClient = testClient is not null ? null : player == MusicPlayer.NetEaseMusic
            ? new NetEaseLyricsClient(requestTimeoutMs) : new OnlineLyricsClient(requestTimeoutMs);
        ILyricsSource client = testClient ?? ownedClient!;
        if (player == MusicPlayer.NetEaseMusic && sourceSongId is not null) binding = new(sourceSongId);
        SongSelection selection;
        if (binding is null)
            selection = await client.RecallAsync(song.Title, song.Artist, song.Album,
                song.Duration.TotalSeconds, deadline.Token, ensureNetworkAllowed, beforeRequest: beforeRequest);
        else
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (beforeRequest is not null) await beforeRequest();
            ensureNetworkAllowed?.Invoke();
            try
            {
                var details = await client.GetDetailsAsync(binding.Mid, deadline.Token);
                selection = SongMatcher.Rank([details], song.Title, song.Artist, song.Album,
                    song.Duration.TotalSeconds, binding.ArtistAlias);
            }
            catch (CandidateMetadataException) { throw new MatchUnavailableException(MatchCategory.InvalidMetadata); }
        }
        if (selection.Winner is null) throw new MatchUnavailableException(selection.Category);
        deadline.Token.ThrowIfCancellationRequested();
        if (beforeRequest is not null) await beforeRequest();
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
        Program.ReportDesktopLog($"自动跟随已启用：播放器及歌词来源为{settings.Player.DisplayName()}；切歌取消旧请求。失败不自动重试；不输出歌词正文。配置不会保存联网授权。");
        var started = session.Clock;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(settings.PollIntervalMs));
        var gate = new FollowGeneration(settings.TrackSettleMs);
        var fallback = new NoLyricsFallback();
        var idleClock = new IdleClock(settings.IdleClockEnabled, settings.IdleClockDelayMs);
        var intro = new TrackIntro(settings.TrackIntroEnabled, settings.TrackIntroHoldMs);
        CancellationTokenSource? requestCancellation = null;
        Task<LoadedLyrics>? pending = null;
        long pendingGeneration = -1;
        string? pendingSourceId = null;
        LyricsDocument? lyrics = null;
        MediaSnapshot? previous = null;
        long lastAdvance = 0;
        int advanceCount = 0;
        bool attempted = false;
        int networkBlocked = 0;
        async Task<LoadedLyrics> LoadGuarded(SongIdentity song, ProbeSongBinding? binding, CancellationToken requestToken, string? sourceSongId)
        {
            try
            {
                return await LoadAsync(song, binding, requestToken, () =>
                {
                    requestToken.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref networkBlocked) != 0) throw new OperationCanceledException();
                }, requestTimeoutMs: settings.RequestTimeoutMs, player: settings.Player, sourceSongId: sourceSongId,
                    beforeRequest: async () =>
                    {
                        var current = await media.ReadAsync(requestToken);
                        if (current is not { Stable: true } || !media.IsCurrent(current)
                            || current.Song.Id != song.Id || current.SourceSongId != sourceSongId
                            || current.GetPosition() is null || !session.CanApply(pendingAttempt)
                            || !gate.CanApply(pendingGeneration, current.Song.Id, current.Revision))
                            throw new OperationCanceledException();
                    });
            }
            catch (Exception ex) when (ex is ProbeException or HttpRequestException)
            {
                Interlocked.Exchange(ref networkBlocked, 1);
                string reason = ex is ProbeException protocol ? SafeFailureReason(protocol) : "网络请求失败";
                string context = sourceSongId is { Length: > 0 and <= 19 } && sourceSongId.All(char.IsAsciiDigit)
                    ? $"；歌曲 ID {sourceSongId}" : "";
                session.BlockNetwork(runId, reason + context);
                Program.ReportDesktopLog($"联网锁止：{reason}{context}；本次运行停止后续联网，请停止后重新开始。");
                throw;
            }
        }
        string fallbackMessage = settings.TrackIntroEnabled
            ? "；开场结束后切时钟，不追加歌名停留，不自动重试。"
            : "；显示当前歌名后切时钟，不自动重试。";
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
                var snapshot = await media.ReadAsync(token);
                bool usable = snapshot is not null && snapshot.Stable && media.IsCurrent(snapshot);
                string? identity = usable ? snapshot!.Song.Id : null;
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
                        Busy = false, Settled = false, Check = s.NetworkBlocked ? s.Check : checkState });
                    fallback.Reset();
                    intro.Start(gate.Generation, usable ? snapshot!.Song.Title : null, started.Elapsed,
                        usable ? snapshot!.Song.Artist : null);
                    attempted = false;
                    advanceCount = 0;
                    lastAdvance = 0;
                    previous = null;
                    if (options.Send) screen!.ClearLyrics();
                    Program.ReportDesktopLog(identity is null ? "会话暂不可用：旧词已失效。" : "检测到曲目或媒体版本变化，等待稳定后核对歌词。");
                }
                if (screen?.HasFailed == true) break;
                session.Update(s => s with { Settled = usable && gate.IsSettled(started.Elapsed),
                    Playback = usable ? snapshot!.Status.ToString() : "会话不可用",
                    Check = s.NetworkBlocked ? s.Check : !usable ? media.UnavailableReason
                        : snapshot!.GetPosition() is null ? "播放进度不可用或已过期；请恢复播放并确认进度组件正常"
                        : s.Check == "播放进度不可用或已过期；请恢复播放并确认进度组件正常" ? checkState : s.Check });
                if (session.TakeRecheck(session.Latest.Track, started.Elapsed))
                {
                    attempted = false;
                    lyrics = null;
                    fallback.Reset();
                    if (options.Send) screen!.ClearLyrics();
                    // 保留 MID 绑定和原开场计时，不重播开场。
                }
                if (Volatile.Read(ref networkBlocked) != 0) lyrics = null;
                if (usable && snapshot!.GetPosition() is not null && !attempted && Volatile.Read(ref networkBlocked) == 0 && gate.IsSettled(started.Elapsed))
                {
                    pendingAttempt = session.BeginAttempt(new(runId, gate.Generation, snapshot!.Song.Id, snapshot.Revision));
                    if (pendingAttempt is null) continue;
                    attempted = true;
                    ProbeSongBinding? binding;
                    if (settings.Player == MusicPlayer.NetEaseMusic)
                        binding = settings.NetEaseBindings.TryGetValue(snapshot!.Song.Id, out var id) ? new(id) : null;
                    else settings.Bindings.TryGetValue(snapshot!.Song.Id, out binding);
                    requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    pendingGeneration = gate.Generation;
                    pendingSourceId = snapshot.SourceSongId;
                    pending = LoadGuarded(snapshot.Song, binding, requestCancellation.Token, snapshot.SourceSongId);
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
                        if (usable && session.CanApply(pendingAttempt) && gate.CanApply(pendingGeneration, check?.Song.Id, check?.Revision ?? -1)
                            && check is { Stable: true } && check.SourceSongId == pendingSourceId && media.IsCurrent(check))
                        {
                            token.ThrowIfCancellationRequested();
                            checkState = "已核对并在内存绑定歌词";
                            lyrics = loaded.Document;
                            if (settings.Player == MusicPlayer.NetEaseMusic) settings.NetEaseBindings[check.Song.Id] = loaded.Mid;
                            else settings.SetVerifiedBinding(check.Song, loaded.Mid, loaded.Alias);
                            if (options.SaveSettings) settings.Save(options.SettingsPath!);
                            snapshot = check;
                            Program.ReportDesktopLog($"当前曲目已绑定 {lyrics.Lines.Count} 行；歌词仅保存在内存。");
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        checkState = "取词取消或超时；可在冷却结束后手动核对";
                        Program.ReportDesktopLog("取词已取消或超时，本媒体版本不自动重试。");
                    }
                    catch (MatchUnavailableException ex)
                    {
                        checkState = MatchFailureReason(ex.Category);
                        Program.ReportDesktopLog(checkState + fallbackMessage);
                        await ArmFallbackAsync();
                    }
                    catch (UntimedLyricsException)
                    {
                        checkState = "当前曲目没有可同步的时间轴";
                        Program.ReportDesktopLog(checkState + "；不锁止联网，切歌后正常核对。" + fallbackMessage);
                        await ArmFallbackAsync();
                    }
                    catch (NoLyricsException)
                    {
                        checkState = "当前曲目无歌词";
                        Program.ReportDesktopLog(checkState + fallbackMessage);
                        await ArmFallbackAsync();
                    }
                    catch (ProbeException)
                    {
                        // Only locally defined, allowlisted categories are logged; never raw response text.
                        Interlocked.Exchange(ref networkBlocked, 1);
                    }
                    catch (HttpRequestException)
                    {
                        Interlocked.Exchange(ref networkBlocked, 1);
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
                if (Volatile.Read(ref networkBlocked) != 0) lyrics = null;
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
                            bool sent = screen!.ShowClock(settings.ClockStyle);
                            fallback.MarkClock();
                            Program.ReportDesktopLog(sent ? "无词降级：时钟指令已发送，实际显示及时间需确认。" : "无词降级：时钟发送失败，本轮停止写屏。");
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
                        var query = position.Value - TimeSpan.FromMilliseconds(options.OffsetMs);
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
            while (await timer.WaitForNextTickAsync(token));
        }
        finally
        {
            session.Stop();
            idleClock.Stop();
            requestCancellation?.Cancel();
            if (pending is not null) ObserveAbandoned(pending);
            requestCancellation?.Dispose();
            if (options.Send) screen!.ClearLyrics();
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

    public void Start(long currentGeneration, string? title, TimeSpan now, string? artist = null)
    {
        generation = currentGeneration;
        Title = !enabled || title is null ? null
            : string.IsNullOrWhiteSpace(artist) ? title : $"{title} - {artist}";
        startedAt = now;
    }

    public TimeSpan Elapsed(TimeSpan now) => now < startedAt ? TimeSpan.Zero : now - startedAt;
    public bool ReplacesFallbackTitle(long currentGeneration) => generation == currentGeneration && Title is not null;
    public bool IsShowing(long currentGeneration, TimeSpan now) => ReplacesFallbackTitle(currentGeneration)
        && Elapsed(now) < TimeSpan.FromMilliseconds(holdMs);
}

// 纯状态计时器；返回 true 即消耗本次发送机会，失败也不重试。无后台计时任务。
internal sealed class IdleClock(bool enabled, int delayMs)
{
    private long generation = -1;
    private TimeSpan? blankSince;
    private bool clockSent;
    private bool stopped;

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

    public bool IsSettled(TimeSpan now) => identity is not null && now - changedAt >= TimeSpan.FromMilliseconds(settleMs);
    public bool CanApply(long generation, string? songId, long mediaRevision) => identity is not null
        && Generation == generation && identity == songId && revision == mediaRevision;
}
