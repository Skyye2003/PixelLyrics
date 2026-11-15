using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using PixelLyrics.Core;
using PixelLyrics.OnlineProbe;
using System.Text.Json;
using Windows.Media.Control;

namespace PixelLyrics.Probe;

internal static class Program
{
    public static event Action<string>? DesktopLog;

    internal static void ReportDesktopLog(string message)
    {
        Console.WriteLine(message);
        DesktopLog?.Invoke(message);
    }

    public static async Task<int> RunDesktopAsync(int offset, string settingsPath, bool save, CancellationToken token, int widthUnits = 32, int pageHoldMs = 2800, ProbeSettings? runtimeSettings = null, DesktopSession? session = null)
    {
        var arguments = new List<string> { "--online", "--follow", "--send", "--offset-ms",
            offset.ToString(System.Globalization.CultureInfo.InvariantCulture), "--settings", settingsPath };
        if (save) arguments.Add("--save-settings");
        try
        {
            ReportDesktopLog("开始初始化设备和QQ音乐媒体会话。");
            return await RunAsync(ProbeOptions.Parse(arguments.ToArray()), token, widthUnits, pageHoldMs, runtimeSettings, session).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ReportDesktopLog("运行已停止；退出清屏流程已执行，实际显示以设备为准。");
            return 0;
        }
        catch (Exception ex)
        {
            ReportDesktopLog($"运行终止：{ex.GetType().Name}；未记录异常正文，不自动重试。");
            return 1;
        }
    }

    public static async Task<int> Main(string[] args)
    {
        if (DesktopLaunch.Parse(args) is bool startInTray) return DesktopHost.Run(startInTray);
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 1 && args[0] == "--self-test") return ProbeSelfTests.Run();
        if (args.Length == 1 && args[0] == "--inspect-metadata") return await InspectMetadataAsync();
        if (args.Length == 1 && args[0] == "--clear-screen")
        {
            var device = ScreenOutput.Enumerate(true);
            if (device is null) return 3;
            device.ShowLyric(" ");
            Console.WriteLine(device.HasFailed ? "静态空白写入失败，屏幕可能仍有残留。" : "静态空白写入已返回，请确认滚动是否停止。");
            return device.HasFailed ? 5 : 0;
        }
        if (args.Length == 1 && args[0] == "--native-scroll-test")
        {
            try
            {
                var device = ScreenOutput.Enumerate(true);
                if (device is null) return 3;
                await device.TestNativeScrollAsync(CancellationToken.None);
                return device.HasFailed ? 5 : 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"原生滚动测试失败：{ex.GetType().Name}；请检查屏幕是否残留文字。");
                return 5;
            }
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            ProbeOptions options;
            try
            {
                options = ProbeOptions.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            if (options.Help)
            {
                Console.WriteLine(ProbeOptions.HelpText);
                return 0;
            }
            return await RunAsync(options, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.WriteLine("已取消。");
            return 0;
        }
        catch (NoLyricsException)
        {
            Console.Error.WriteLine("当前曲目无歌词；不重试。");
            return 4;
        }
        catch (PixelLyrics.OnlineProbe.ProbeException ex)
        {
            Console.Error.WriteLine("在线校验失败（不重试）：" + JsonSerializer.Serialize(ex.Message));
            return 4;
        }
        catch (Exception ex)
        {
            // 不输出异常的完整内容，避免第三方异常夹带歌词或曲目信息。
            Console.Error.WriteLine($"探测失败：{ex.GetType().Name}；未自动重试。");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static async Task<int> InspectMetadataAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(deadline.Token);
            var sessions = manager.GetSessions().Where(s => s.SourceAppUserModelId.Contains("qqmusic", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sessions.Length != 1) { Console.WriteLine("唯一QQ音乐会话不可用；未联网。"); return 3; }
            var session = sessions[0];
            var p = await session.TryGetMediaPropertiesAsync().AsTask(deadline.Token);
            var timeline = session.GetTimelineProperties();
            Console.WriteLine(JsonSerializer.Serialize(new { Stage = "GSMTC", p.Title, p.Subtitle, p.Artist, p.AlbumTitle,
                p.AlbumArtist, p.TrackNumber, p.AlbumTrackCount, DurationSeconds = (timeline.EndTime - timeline.StartTime).TotalSeconds }));
            using var client = new OnlineLyricsClient();
            var candidates = await client.SearchAsync(p.Title, deadline.Token);
            foreach (var song in candidates.Take(8))
            {
                try
                {
                    var raw = await client.InspectMetadataAsync(song.Mid, deadline.Token);
                    Console.WriteLine(JsonSerializer.Serialize(new { Stage = "Candidate", Metadata = raw }));
                }
                catch (CandidateMetadataException) { Console.WriteLine("单候选元数据缺失。"); }
            }
            var after = await session.TryGetMediaPropertiesAsync().AsTask(deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { Stage = "Consistency", Same = p.Title == after.Title && p.Artist == after.Artist && p.AlbumTitle == after.AlbumTitle && p.Subtitle == after.Subtitle }));
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("只读元数据调查停止：" + ex.GetType().Name); return 1; }
    }

    private static async Task<int> RunAsync(ProbeOptions options, CancellationToken cancellationToken, int? widthUnits = null, int? pageHoldMs = null, ProbeSettings? runtimeSettings = null, DesktopSession? session = null)
    {
        var settings = ProbeSettings.LoadForRun(options);
        if (runtimeSettings is not null) settings.ApplyRuntimeOverrides(runtimeSettings);
        options = options with { OffsetMs = options.HasOffset ? options.OffsetMs : settings.OffsetMs };
        settings.DisplayWidthUnits = widthUnits ?? settings.DisplayWidthUnits;
        settings.PageHoldMs = pageHoldMs ?? settings.PageHoldMs;
        session?.Update(s => s with { Active = DesktopParameters.From(settings) });
        if (options.Accessibility) return DiagnoseAccessibility(cancellationToken);
        if (options.DesktopLyrics) return await DiagnoseDesktopLyricsAsync(options, cancellationToken);
        var screen = ScreenOutput.Enumerate(options.TestScreen || options.Send || options.ScreenSequence);
        cancellationToken.ThrowIfCancellationRequested();
        if ((options.Send || options.TestScreen || options.ScreenSequence) && screen is null) return 3;
        if (screen is not null)
        {
            screen.DisplayWidthUnits = widthUnits ?? settings.DisplayWidthUnits;
            screen.PageHoldMs = pageHoldMs ?? settings.PageHoldMs;
        }
        if (options.ScreenSequence) return await RunScreenSequenceAsync(screen!, cancellationToken);
        if (options.TestScreen && !screen!.Test()) return 5;

        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);
        using var media = new MediaProbe(manager);
        if (options.Follow) return await FollowRunner.RunAsync(options, media, screen, settings, cancellationToken, session);
        var initial = await media.ReadAsync(cancellationToken);
        if (initial is not null) PrintTrack(initial);
        else Console.WriteLine("没有可安全读取的唯一 QQMusic 会话。");

        LyricsDocument? lyrics = null;
        SongIdentity? boundSong = null;
        if (options.LrcPath is not null)
        {
            if (initial is null || !initial.Stable || !media.IsCurrent(initial))
            {
                Console.Error.WriteLine("启动时曲目信息不完整或不稳定，拒绝导入绑定；请曲目稳定后重新运行。");
                return 4;
            }
            using var input = File.OpenRead(options.LrcPath);
            var parsed = LrcParser.Parse(input);
            foreach (var error in parsed.Errors)
                Console.Error.WriteLine($"LRC 第 {error.LineNumber} 行解析失败；不输出原文或异常消息。");
            if (parsed.Document.Lines.Count == 0 || parsed.Errors.Count != 0)
            {
                Console.Error.WriteLine("LRC 为空或包含解析错误，拒绝预览及推送。");
                return 4;
            }
            lyrics = parsed.Document;
            boundSong = initial.Song;
            Console.WriteLine($"已在内存绑定 {lyrics.Lines.Count} 行歌词至启动时曲目；不会自动绑定后续曲目。");
        }

        if (options.Online)
        {
            Console.WriteLine("--online 授权：向 QQ音乐发送启动歌名、歌手和 MID 以搜索、核对详情及取普通 LRC；不发送 Cookie/凭据/播放进度，不代理、不重定向、不重试。默认仅预览，只有 --send 才写屏。");
            if (initial is null || !initial.Stable || !media.IsCurrent(initial)
                || string.IsNullOrWhiteSpace(initial.Song.Artist) || string.IsNullOrWhiteSpace(initial.Song.Album))
            {
                Console.Error.WriteLine("启动曲目/歌手/专辑/时间线缺失或不稳定，拒绝联网。");
                return 4;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var client = new OnlineLyricsClient();
            async Task EnsureUnchanged()
            {
                var current = await media.ReadAsync(deadline.Token);
                if (current is null || !current.Stable || current.Song != initial.Song
                    || !media.IsCurrent(initial) || !media.IsCurrent(current))
                    throw new PixelLyrics.OnlineProbe.ProbeException("联网期间曲目或会话变化，本轮停止，不请求新曲目。");
            }
            settings.Bindings.TryGetValue(initial.Song.Id, out var savedBinding);
            string? selectedMid = options.SongMid ?? savedBinding?.Mid;
            string? selectedAlias = options.ArtistAlias ?? savedBinding?.ArtistAlias;
            SongSelection selection;
            if (selectedMid is null)
                selection = await client.RecallAsync(initial.Song.Title, initial.Song.Artist, initial.Song.Album,
                    initial.Song.Duration.TotalSeconds, deadline.Token, artistAlias: selectedAlias, beforeRequest: EnsureUnchanged);
            else
            {
                await EnsureUnchanged();
                try
                {
                    var details = await client.GetDetailsAsync(selectedMid, deadline.Token);
                    selection = SongMatcher.Rank([details], initial.Song.Title, initial.Song.Artist,
                        initial.Song.Album, initial.Song.Duration.TotalSeconds, selectedAlias);
                }
                catch (CandidateMetadataException) { selection = new(MatchCategory.InvalidMetadata, null, []); }
            }
            if (selection.Winner is not { } winner)
            {
                Console.Error.WriteLine(FollowRunner.MatchFailureReason(selection.Category) + "；拒绝取词与写屏。");
                return 4;
            }
            await EnsureUnchanged();
            var verified = await client.FetchAsync(winner.Details.Mid, deadline.Token);
            await EnsureUnchanged();
            lyrics = verified.Document;
            boundSong = initial.Song;
            settings.SetVerifiedBinding(initial.Song, winner.Details.Mid, selectedAlias);
            if (options.SaveSettings) settings.Save(options.SettingsPath!);
            Console.WriteLine($"已完整解析 {lyrics.Lines.Count} 行 LRC；匹配理由：{winner.Reason}；仅内存绑定启动曲目，不保存完整歌词文件。");
        }

        if (!options.Watch && lyrics is null && !options.Send) return 0;
        Console.WriteLine(options.DurationSeconds is int seconds
            ? $"有限探测 {seconds} 秒后自动退出。未启用 --send 时不推送歌词。"
            : "持续探测，Ctrl+C 退出。未启用 --send 时不推送歌词。");
        var stopwatch = Stopwatch.StartNew();
        var nextPoll = TimeSpan.Zero;
        var nextWatch = TimeSpan.Zero;
        var nextSend = TimeSpan.Zero;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        MediaSnapshot? snapshot = initial;
        LyricLine? preview = null;
        SongIdentity? displayedSong = initial?.Song;
        MediaSnapshot? lastMotionSample = null;
        long lastAdvanceAt = 0;
        int advanceCount = 0;
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = stopwatch.Elapsed;
                if (options.DurationSeconds is int duration && now >= TimeSpan.FromSeconds(duration)) break;
                if (now >= nextPoll)
                {
                    snapshot = await media.ReadAsync(cancellationToken);
                    if (snapshot is not null && snapshot.Stable && !snapshot.IsStale
                        && snapshot.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing
                        && lastMotionSample is not null && snapshot.Song == lastMotionSample.Song
                        && snapshot.Revision == lastMotionSample.Revision)
                    {
                        var delta = snapshot.Position - lastMotionSample.Position;
                        if (delta > TimeSpan.Zero && delta < TimeSpan.FromSeconds(1))
                        {
                            advanceCount++;
                            lastAdvanceAt = Stopwatch.GetTimestamp();
                        }
                        else if (delta < TimeSpan.Zero || delta >= TimeSpan.FromSeconds(1)) advanceCount = 0;
                    }
                    else advanceCount = 0;
                    lastMotionSample = snapshot;
                    nextPoll = stopwatch.Elapsed + TimeSpan.FromMilliseconds(options.Send ? 200 : 250);
                    if (snapshot is not null && snapshot.Song != displayedSong)
                    {
                        PrintTrack(snapshot);
                        displayedSong = snapshot.Song;
                    }
                }

                bool valid = snapshot is not null && snapshot.Stable && media.IsCurrent(snapshot)
                    && (!options.Online || media.IsCurrent(initial!));
                if (lyrics is not null && (snapshot is null || (valid && snapshot.Song != boundSong)
                    || (options.Online && (!media.IsCurrent(initial!) || snapshot.Song != boundSong))))
                {
                    lyrics = null;
                    boundSong = null;
                    preview = null;
                    Console.WriteLine("导入歌词已永久失效（曲目或会话变化）；新曲目未绑定，不显示旧词。");
                }
                var position = valid ? snapshot!.GetPosition() : null;
                bool inferredPlaying = valid && snapshot!.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing
                    && !snapshot.IsStale && advanceCount >= 3 && lastAdvanceAt != 0
                    && Stopwatch.GetElapsedTime(lastAdvanceAt) < TimeSpan.FromMilliseconds(700);
                bool playing = valid && (snapshot!.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing || inferredPlaying);
                bool paused = valid && snapshot!.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
                if (lyrics is null || position is null || (!playing && !paused)) preview = null;
                else
                {
                    var queryPosition = position.Value - TimeSpan.FromMilliseconds(options.OffsetMs);
                    preview = queryPosition < TimeSpan.Zero ? null : lyrics.GetLineAt(queryPosition);
                }

                now = stopwatch.Elapsed;
                if (now >= nextWatch)
                {
                    string state = valid ? snapshot!.Status.ToString() + (inferredPlaying ? "（位置连续前进，兼容播放）" : "") : "会话缺失/不稳定";
                    if (snapshot?.IsStale == true)
                        Console.Error.WriteLine($"时间线 stale：age={snapshot.TimelineAge.TotalSeconds:F2}s > 5s；不显示歌词，保留此限制观察真实时间线。");
                    string point = position?.ToString(@"hh\:mm\:ss\.fff") ?? "未知/时间线无效";
                    Console.WriteLine($"位置={point} 状态={state} 歌词={(lyrics is null ? "[未绑定]" : preview?.Text ?? "[无当前行]")}");
                    nextWatch = now + TimeSpan.FromMilliseconds(250);
                }
                if (options.Send && now >= nextSend)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (lyrics is null || position is null || (!playing && !paused)
                        || (options.Online && !media.IsCurrent(initial!))) screen!.ClearLyrics();
                    else if ((playing || paused) && media.IsCurrent(snapshot!)) screen!.ShowLyric(preview?.Text,
                        preview is null ? TimeSpan.Zero : position.Value - TimeSpan.FromMilliseconds(options.OffsetMs) - preview.Timestamp,
                        paused, preview is null ? null : snapshot!.Song.Id + ":" + preview.Timestamp.Ticks);
                    if (screen!.HasFailed) break;
                    nextSend = stopwatch.Elapsed + TimeSpan.FromMilliseconds(200);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("已取消。");
        }
        finally
        {
            if (options.Send) screen!.ClearLyrics();
        }
        return screen?.HasFailed == true ? 5 : 0;
    }

    private static async Task<int> RunScreenSequenceAsync(ScreenOutput screen, CancellationToken cancellationToken)
    {
        Console.WriteLine("序列仅测试歌词屏，不修改灯光或音量；SDK 返回成功不代表屏幕显示正确。");
        try
        {
            foreach (string text in new[] { "第一行 测试", "第二行 测试" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                screen.ShowLyric(text);
                Console.WriteLine($"ShowLyric(“{text}”)：{(screen.HasFailed ? "失败" : "调用成功")}。");
                if (screen.HasFailed) break;
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("序列已取消。");
        }
        finally
        {
            screen.ClearLyrics();
            Console.WriteLine(screen.HasFailed
                ? "ClearLyrics：未成功；USB 故障后禁止继续写屏，屏上可能残留内容。"
                : "ClearLyrics：调用成功（以空格替换旧歌词）。");
            Console.WriteLine("请用户肉眼确认：是否依次显示两行、各保持3秒并最终清除？未经用户确认，不视为显示验证通过。");
        }
        return screen.HasFailed ? 5 : 0;
    }

    private static async Task<int> DiagnoseDesktopLyricsAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        Console.WriteLine("只读 QQMusic 窗口诊断；标题不是已验证歌词，普通 QQ音乐主窗口标题不能作为歌词使用。");
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
        };
        var previous = new Dictionary<nint, (uint ProcessId, string Title)>();
        bool? hadWindows = null;
        var stopwatch = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processIds = new HashSet<uint>();
            foreach (var process in Process.GetProcessesByName("QQMusic"))
            {
                using (process) processIds.Add((uint)process.Id);
            }
            var current = new Dictionary<nint, (uint ProcessId, string Title)>();
            NativeWindows.EnumWindowsCallback callback = (window, _) =>
            {
                NativeWindows.GetWindowThreadProcessId(window, out uint processId);
                if (!processIds.Contains(processId)) return true;
                var className = new StringBuilder(257);
                NativeWindows.GetClassName(window, className, className.Capacity);
                var title = new StringBuilder(257);
                NativeWindows.GetWindowText(window, title, title.Capacity);
                string text = title.ToString();
                current[window] = (processId, text);
                if (!previous.TryGetValue(window, out var old) || old != (processId, text))
                    Console.WriteLine($"类名={System.Text.Json.JsonSerializer.Serialize(className.ToString(), jsonOptions)} 可见性={NativeWindows.IsWindowVisible(window)} 标题={System.Text.Json.JsonSerializer.Serialize(text, jsonOptions)}");
                return true;
            };
            if (!NativeWindows.EnumWindows(callback, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (current.Count == 0 && hadWindows != false)
                Console.WriteLine("未找到 QQMusic 进程窗口，请启动 QQ音乐并开启QQ音乐桌面歌词。");
            hadWindows = current.Count != 0;
            previous = current;
            if (options.DurationSeconds is not int duration) break;
            var remaining = TimeSpan.FromSeconds(duration) - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(options.DurationSeconds!.Value));
        return 0;
    }

    private static int DiagnoseAccessibility(CancellationToken cancellationToken)
    {
        Console.WriteLine("只读 UI Automation 单次采样：仅 QQMusic 的“桌面歌词”顶层窗口；最多150元素、深度8、文本256字符。仅终端输出，不OCR、不写屏、不发送输入。");
        var processIds = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("QQMusic"))
        {
            using (process) processIds.Add((uint)process.Id);
        }
        var windows = new List<(nint Handle, uint ProcessId)>();
        NativeWindows.EnumWindowsCallback callback = (window, _) =>
        {
            NativeWindows.GetWindowThreadProcessId(window, out uint processId);
            if (!processIds.Contains(processId)) return true;
            var title = new StringBuilder(257);
            NativeWindows.GetWindowText(window, title, title.Capacity);
            if (string.Equals(title.ToString(), "桌面歌词", StringComparison.Ordinal))
                windows.Add((window, processId));
            return true;
        };
        if (!NativeWindows.EnumWindows(callback, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (windows.Count == 0)
        {
            Console.WriteLine("未找到 QQMusic 进程中标题精确为“桌面歌词”的顶层窗口；未读取其他窗口的 UIA。");
            return 0;
        }

        int visited = 0;
        var walker = TreeWalker.RawViewWalker;
        foreach (var window in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited >= 150) break;
            // 窗口可能在枚举后关闭或更名；调用 UIA 前再次验证归属与精确标题。
            NativeWindows.GetWindowThreadProcessId(window.Handle, out uint processId);
            var title = new StringBuilder(257);
            NativeWindows.GetWindowText(window.Handle, title, title.Capacity);
            if (processId != window.ProcessId || !string.Equals(title.ToString(), "桌面歌词", StringComparison.Ordinal))
                continue;
            try
            {
                var root = AutomationElement.FromHandle(window.Handle);
                if (root is null) continue;
                Console.WriteLine("开始采样匹配的桌面歌词窗口（根深度0）。");
                Visit(root, 0);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or ElementNotEnabledException
                or COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"此窗口 UIA 采样已停止：{ex.GetType().Name}；不重试，不输出异常消息。");
            }
        }
        Console.WriteLine($"UIA 单次采样结束：已访问 {visited} 个元素（全次上限150，最大深度8）。");
        return 0;

        void Visit(AutomationElement element, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited >= 150) return;
            visited++;
            string name = element.Current.Name;
            string controlType = element.Current.ControlType.ProgrammaticName;
            var visibleText = new StringBuilder(256);
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern))
            {
                var ranges = ((TextPattern)pattern).GetVisibleRanges();
                for (int i = 0; i < ranges.Length && i < 150 && visibleText.Length < 256; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int remaining = 256 - visibleText.Length;
                    string text = ranges[i].GetText(remaining);
                    visibleText.Append(text.AsSpan(0, Math.Min(text.Length, remaining)));
                }
            }
            Console.WriteLine($"深度={depth} Name={Quote(name)} ControlType={Quote(controlType)} 可见文本={Quote(visibleText.ToString())}");
            if (depth >= 8 || visited >= 150) return;
            var child = walker.GetFirstChild(element);
            while (child is not null && visited < 150)
            {
                Visit(child, depth + 1);
                if (visited >= 150) break;
                child = walker.GetNextSibling(child);
            }
        }

        static string Quote(string text) =>
            System.Text.Json.JsonSerializer.Serialize(text.Length > 256 ? text[..256] : text);
    }

    private static class NativeWindows
    {
        internal delegate bool EnumWindowsCallback(nint window, nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW", ExactSpelling = true)]
        internal static extern int GetClassName(nint window, StringBuilder className, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW", ExactSpelling = true)]
        internal static extern int GetWindowText(nint window, StringBuilder title, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);
    }

    private static void PrintTrack(MediaSnapshot snapshot)
    {
        Console.WriteLine($"当前曲目：{snapshot.Song.Title} / {snapshot.Song.Artist} / {snapshot.Song.Album}");
        Console.WriteLine($"时间线：位置={snapshot.Position} 起点={snapshot.Start} 终点={snapshot.End} 更新时间={snapshot.Updated:O} 状态={snapshot.Status} 稳定={snapshot.Stable}");
    }
}
