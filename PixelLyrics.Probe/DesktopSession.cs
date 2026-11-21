using PixelLyrics.Core;
using PixelLyrics.OnlineProbe;
namespace PixelLyrics.Probe;

// 参数按值比较；绑定、自启和授权不属于运行显示参数。
internal sealed record DesktopParameters(int OffsetMs, int Width, int Hold, int ClockStyle,
    bool IdleClock, int IdleDelay, bool Intro, int IntroHold, int NoLyricsHold,
    int Poll, int Settle, int Timeout, MusicPlayer Player = MusicPlayer.QQMusic, PlayerMode Mode = PlayerMode.Auto)
{
    internal static DesktopParameters From(ProbeSettings s) => new(s.OffsetMs, s.DisplayWidthUnits,
        s.PageHoldMs, s.ClockStyle, s.IdleClockEnabled, s.IdleClockDelayMs, s.TrackIntroEnabled,
        s.TrackIntroHoldMs, s.NoLyricsTitleHoldMs, s.PollIntervalMs, s.TrackSettleMs, s.RequestTimeoutMs,
        s.Mode == PlayerMode.NetEaseMusic ? MusicPlayer.NetEaseMusic : MusicPlayer.QQMusic, s.Mode);
    internal void ApplyTo(ProbeSettings s)
    {
        s.Player = Player; s.Mode = Mode; s.OffsetMs = OffsetMs; s.DisplayWidthUnits = Width; s.PageHoldMs = Hold;
        s.ClockStyle = ClockStyle; s.IdleClockEnabled = IdleClock; s.IdleClockDelayMs = IdleDelay;
        s.TrackIntroEnabled = Intro; s.TrackIntroHoldMs = IntroHold; s.NoLyricsTitleHoldMs = NoLyricsHold;
        s.PollIntervalMs = Poll; s.TrackSettleMs = Settle; s.RequestTimeoutMs = Timeout;
    }
    internal string Differences(DesktopParameters? other)
    {
        if (other is null) return "无快照";
        var names = new List<string>();
        if (Mode != other.Mode) names.Add("播放器");
        if (OffsetMs != other.OffsetMs) names.Add("偏移");
        if (Width != other.Width) names.Add("宽度");
        if (Hold != other.Hold) names.Add("分页停留");
        if (ClockStyle != other.ClockStyle) names.Add("时钟样式");
        if (IdleClock != other.IdleClock || IdleDelay != other.IdleDelay) names.Add("空闲回钟");
        if (Intro != other.Intro || IntroHold != other.IntroHold) names.Add("曲目开场");
        if (NoLyricsHold != other.NoLyricsHold) names.Add("无词停留");
        if (Poll != other.Poll) names.Add("轮询");
        if (Settle != other.Settle) names.Add("稳定等待");
        if (Timeout != other.Timeout) names.Add("请求超时");
        return names.Count == 0 ? "一致" : string.Join("、", names);
    }
}

internal sealed record TrackVersion(Guid RunId, long Generation, string SongId, long Revision);
internal sealed record LyricAttempt(TrackVersion Track, long AttemptId);
internal sealed record DesktopSnapshot(Guid RunId, bool Running = true, bool Stopping = false,
    TrackVersion? Track = null, string? Title = null, string? Artist = null, string Playback = "未知",
    string Check = "初始化", string Display = "尚未输出；实际设备显示未确认", bool Busy = false,
    bool Settled = false, bool NetworkBlocked = false, TimeSpan CooldownUntil = default,
    long AttemptId = 0, DesktopParameters? Active = null, MusicPlayer? Source = null,
    long ParameterVersion = 0, string SourceNotice = "", bool RecoveryWaiting = false, bool RecoveryManual = false, bool DeviceReady = true,
    SongIdentity? Song = null, bool NeedsSource = false, LyricSourceMapping? Mapping = null, bool Trial = false);

// 每次运行独享；只有一个最新快照和一个待处理命令，不向 Dispatcher 堆积事件。
internal sealed class DesktopSession
{
    internal RunLifecycle Lifecycle { get; } = new();
    internal System.Diagnostics.Stopwatch Clock { get; } = System.Diagnostics.Stopwatch.StartNew();
    private readonly object sync = new();
    internal readonly Dictionary<MusicPlayer, RequestRecovery> Recoveries = new() { [MusicPlayer.QQMusic] = new(true), [MusicPlayer.NetEaseMusic] = new(true) };
    private SourceChoice? sourceChoice;
    internal volatile bool SourceReminderSuppressed;
    private TaskCompletionSource<bool>? sourceCompletion;
    private readonly Dictionary<string, LyricSourceMapping> mappings = new();
    private readonly HashSet<string> forgotten = new();
    internal Task<bool> QueueSource(SourceChoice choice)
    {
        lock (sync)
        {
            if (!state.Running || state.Stopping || state.NetworkBlocked || sourceCompletion is not null) return Task.FromResult(false);
            if (choice.Trial && (state.Track != choice.Track || !state.DeviceReady || Lifecycle.IsPaused)) return Task.FromResult(false);
            sourceChoice = choice;
            sourceCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return sourceCompletion.Task;
        }
    }
    internal SourceChoice? TakeSource() { lock (sync) { var value = sourceChoice; sourceChoice = null; return value; } }
    internal void FinishSource(bool success) { lock (sync) { sourceCompletion?.TrySetResult(success); sourceCompletion = null; } }
    internal void RememberSource(string key, LyricSourceMapping? mapping)
    {
        lock (sync)
        {
            if (mapping is null) { mappings.Remove(key); forgotten.Add(key); }
            else { mappings[key] = mapping; forgotten.Remove(key); }
        }
    }
    private DesktopSnapshot state;
    private TrackVersion? command;
    private long attempt;
    private long parameterVersion;
    private (long Version, DesktopParameters Parameters, TaskCompletionSource<bool> Completion)? configuration;
    private readonly Dictionary<string, ProbeSongBinding> qqBindings = new();
    private readonly Dictionary<string, string> netEaseBindings = new();
    internal void RememberBindings(ProbeSettings settings)
    {
        lock (sync)
        {
            foreach (var pair in settings.Bindings) qqBindings[pair.Key] = pair.Value;
            foreach (var pair in settings.NetEaseBindings) netEaseBindings[pair.Key] = pair.Value;
            foreach (var pair in settings.SourceMappings) if (!forgotten.Contains(pair.Key)) mappings[pair.Key] = pair.Value;
        }
    }
    internal void MergeBindings(ProbeSettings settings)
    {
        lock (sync)
        {
            foreach (var pair in qqBindings) settings.Bindings[pair.Key] = pair.Value;
            foreach (var pair in netEaseBindings) settings.NetEaseBindings[pair.Key] = pair.Value;
            foreach (var key in forgotten) settings.SourceMappings.Remove(key);
            foreach (var pair in mappings) settings.SourceMappings[pair.Key] = pair.Value;
        }
    }
    internal Task<bool> QueueParameters(DesktopParameters parameters)
    {
        lock (sync)
        {
            if (!state.Running || state.Stopping) return Task.FromResult(false);
            if (configuration is not null) throw new InvalidOperationException("已有待应用的设置。");
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            configuration = (++parameterVersion, parameters, completion);
            return completion.Task;
        }
    }
    internal bool ApplyParameters(Action<DesktopParameters> apply)
    {
        lock (sync)
        {
            if (configuration is not { } next) return false;
            configuration = null;
            if (!state.Running || state.Stopping) { next.Completion.TrySetResult(false); return false; }
            try
            {
                apply(next.Parameters);
                state = state with { Active = next.Parameters, ParameterVersion = next.Version };
                next.Completion.TrySetResult(true);
                return true;
            }
            catch (Exception error) { next.Completion.TrySetException(error); throw; }
        }
    }
    private void CancelParameters()
    {
        sourceChoice = null; sourceCompletion?.TrySetResult(false); sourceCompletion = null;
        configuration?.Completion.TrySetResult(false);
        configuration = null;
    }
    internal DesktopSession() => state = new(Guid.NewGuid());
    internal DesktopSnapshot Latest { get { lock (sync) return state; } }
    internal void Update(Func<DesktopSnapshot, DesktopSnapshot> change)
    {
        lock (sync)
        {
            if (!state.Running) return;
            state = change(state);
        }
    }
    internal bool CanRecheck(TimeSpan now)
    {
        lock (sync) return Allowed(now);
    }
    private bool Allowed(TimeSpan now) => state.Running && !state.Stopping && state.Track is not null
        && state.DeviceReady && state.Settled && !state.Busy && !state.RecoveryWaiting && !state.NetworkBlocked && command is null && now >= state.CooldownUntil;
    internal bool QueueRecheck(TrackVersion? expected, TimeSpan now)
    {
        lock (sync)
        {
            if (expected is null || state.Track != expected || !Allowed(now)) return false;
            command = expected;
            return true;
        }
    }
    internal bool TakeRecheck(TrackVersion? current, TimeSpan now)
    {
        lock (sync)
        {
            var queued = command;
            command = null;
            return queued is not null && queued == current && state.Track == current && Allowed(now);
        }
    }
    internal LyricAttempt? BeginAttempt(TrackVersion track)
    {
        lock (sync)
        {
            if (!state.Running || state.Stopping || state.NetworkBlocked || !state.DeviceReady || Lifecycle.IsPaused || state.Busy || state.Track != track) return null;
            state = state with { Busy = true, AttemptId = ++attempt, Check = "核对中" };
            return new(track, attempt);
        }
    }
    internal bool CanApply(LyricAttempt? value)
    {
        lock (sync) return value is not null && state.Running && !state.Stopping && !state.NetworkBlocked && state.DeviceReady && !Lifecycle.IsPaused
            && value.Track.RunId == state.RunId && state.Track == value.Track && state.AttemptId == value.AttemptId;
    }
    internal void Complete(LyricAttempt? value, string result, TimeSpan now)
    {
        lock (sync)
        {
            if (value is null || !state.Running || state.Stopping || state.Track != value.Track || state.AttemptId != value.AttemptId) return;
            state = state with { Busy = false, Check = state.NetworkBlocked ? state.Check : result,
                CooldownUntil = now + TimeSpan.FromSeconds(3) };
        }
    }
    // 旧曲目的迟到权限/网络错误仍锁止同一次运行，但不能影响另一次运行。
    internal void BlockNetwork(Guid runId, string? reason = null)
    {
        lock (sync)
        {
            if (!state.Running || state.RunId != runId) return;
            command = null;
            state = state with { NetworkBlocked = true, Check = reason is null
                ? "权限或网络故障：本次运行禁止后续联网；请停止再开始"
                : reason + "；本次联网已锁止，请停止后重新开始" };
        }
    }
    internal void Stop()
    {
        lock (sync) { CancelParameters(); sourceChoice = null; FinishSource(false); command = null; state = state with { Stopping = true }; }
    }
    internal void End()
    {
        lock (sync) { CancelParameters(); sourceChoice = null; FinishSource(false); command = null; state = state with { Running = false, Stopping = false, Busy = false, Settled = false }; }
    }
}
