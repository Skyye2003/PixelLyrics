using System.IO;

namespace PixelLyrics.Probe;

internal static class ProbeSelfTests
{
    internal static int Run()
    {
        int passed = 0, failed = 0;
        void Check(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("通过：" + name); }
            catch { failed++; Console.Error.WriteLine("失败：" + name); }
        }
        void Accept(params string[] args) => Check(string.Join(' ', args), () => { ProbeOptions.Parse(args); });
        void Reject(params string[] args) => Check("拒绝 " + string.Join(' ', args), () =>
        {
            try { ProbeOptions.Parse(args); }
            catch (ArgumentException) { return; }
            throw new Exception();
        });
        Check("桌面无参和gui保持可见，tray独立隐藏", () =>
        {
            if (DesktopLaunch.Parse([]) != false || DesktopLaunch.Parse(["--gui"]) != false
                || DesktopLaunch.Parse(["--tray"]) != true || DesktopLaunch.Parse(["--self-test"]) is not null) throw new Exception();
        });
        foreach (string[] arguments in new[] { new[] { "--tray", "--online", "--send" }, new[] { "--tray", "--tray" },
            new[] { "--gui", "--tray" }, new[] { "--tray", "--help" }, new[] { "--gui", "--start-minimized" } })
            Check("托盘参数混用拒绝 " + string.Join(' ', arguments), () =>
            {
                if (DesktopLaunch.Parse(arguments) is not null) throw new Exception();
                try { ProbeOptions.Parse(arguments); } catch (ArgumentException) { return; }
                throw new Exception();
            });
        SessionSelfTests.Register(Check);
        LiveSettingsSelfTests.Register(Check);
        NetEaseSelfTests.Register(Check);
        Check("休眠恢复：只重新绑定唯一会话，平时不切换会话", () =>
        {
            var oldSession = new object();
            var replacement = new object();
            if (MediaProbe.SelectSession(oldSession, new[] { replacement }, false) is not null
                || MediaProbe.SelectSession(oldSession, new[] { replacement }, true) != replacement
                || MediaProbe.SelectSession<object>(null, new[] { replacement }, true) != replacement
                || MediaProbe.SelectSession(oldSession, Array.Empty<object>(), true) is not null
                || MediaProbe.SelectSession(oldSession, new[] { oldSession, replacement }, true) is not null
                || MediaProbe.SelectSession(oldSession, new[] { oldSession }, false) != oldSession) throw new Exception();
        });
        Check("网易云诊断识别会话且不混入QQ", () =>
        {
            if (!NativeMediaDiagnostic.IsNetEase("CloudMusic.exe")
                || !NativeMediaDiagnostic.IsNetEase("Orpheus")
                || NativeMediaDiagnostic.IsNetEase("QQMusic.exe")) throw new Exception();
        });
        Check("网易云诊断拒绝过期进度和缺失专辑", () =>
        {
            bool Valid(string album, int age, int position = 10) => NativeMediaDiagnostic.HasUsableTimeline(
                "song", "artist", album, TimeSpan.Zero, TimeSpan.FromSeconds(200),
                TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(age), 1);
            if (!Valid("album", 1) || Valid("album", 6) || Valid("", 1)
                || Valid("album", -1) || Valid("album", 1, 201)) throw new Exception();
        });
        const string startupExe = @"C:\Program Files\歌词 屏\PixelLyrics.Probe.exe";
        Check("自启绝对路径带空格中文及命令引号", () =>
        {
            if (LoginStartup.BuildCommand(startupExe) != "\"" + startupExe + "\" --tray") throw new Exception();
        });
        foreach (string path in new[] { "relative.exe", @"C:\app.dll", @"C:\dotnet.exe", "C:\\bad\"name.exe", "C:\\bad\nname.exe", @"\\server\app.exe" })
            Check("拒绝不安全自启路径 " + path.Replace('\n', ' '), () =>
            {
                try { LoginStartup.BuildCommand(path); } catch (ArgumentException) { return; }
                throw new Exception();
            });
        Check("默认关闭且读取不写入，启用禁用均回读", () =>
        {
            var store = new MemoryStartupStore();
            var startup = new LoginStartup(store, () => startupExe);
            if (startup.ReadEnabled() || store.Writes != 0 || store.Deletes != 0) throw new Exception();
            startup.Apply(true);
            if (!startup.ReadEnabled() || store.Writes != 1 || store.Reads < 3) throw new Exception();
            startup.Apply(false);
            if (startup.ReadEnabled() || store.Deletes != 1) throw new Exception();
        });
        Check("当前exe从应用位置解析而非dotnet宿主", () =>
        {
            string path = LoginStartup.CurrentExecutablePath();
            if (!File.Exists(path) || !Path.IsPathFullyQualified(path)
                || (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(path) != "PixelLyrics.Probe.exe"
                    : !string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))) throw new Exception();
        });
        Check("旧自启项可以明确保存修复", () =>
        {
            var store = new MemoryStartupStore { Value = "旧路径" };
            var startup = new LoginStartup(store, () => startupExe);
            startup.Apply(true);
            if (!startup.ReadEnabled()) throw new Exception();
            startup.Apply(false);
            if (startup.ReadEnabled()) throw new Exception();
        });
        Check("禁用无需exe路径，已不存在时幂等", () =>
        {
            var startup = new LoginStartup(new MemoryStartupStore(), () => throw new FileNotFoundException());
            startup.Apply(false);
        });
        Check("旧路径及非字符串状态不误报关闭或开启", () =>
        {
            foreach (object value in new object[] { "\"C:\\old.exe\" --tray", 1, "" })
            {
                var startup = new LoginStartup(new MemoryStartupStore { Value = value }, () => startupExe);
                try { startup.ReadEnabled(); } catch (InvalidDataException) { continue; }
                throw new Exception();
            }
        });
        foreach (string operation in new[] { "read", "write", "delete" })
            Check("自启异常不假报成功 " + operation, () =>
            {
                var store = new MemoryStartupStore { Failure = operation };
                var startup = new LoginStartup(store, () => startupExe);
                try
                {
                    if (operation == "read") startup.ReadEnabled();
                    else startup.Apply(operation == "write");
                }
                catch (UnauthorizedAccessException) { return; }
                throw new Exception();
            });
        Check("写入被忽略及回读故障不能算保存成功", () =>
        {
            foreach (bool ignore in new[] { true, false })
            {
                var store = new MemoryStartupStore { IgnoreWrite = ignore, FailReadAfterWrite = !ignore };
                var startup = new LoginStartup(store, () => startupExe);
                try { startup.Apply(true); }
                catch (IOException) when (ignore) { continue; }
                catch (UnauthorizedAccessException) when (!ignore) { continue; }
                throw new Exception();
            }
        });
        Check("搜索宽松预筛不按歌手和版本截断，详情负责拒绝", () =>
        {
            var song = new PixelLyrics.Core.SongIdentity("认真的雪", "薛之谦", "专辑", TimeSpan.FromSeconds(259));
            if (!FollowRunner.CandidateMatches(new("a", "认真的雪", "薛之谦"), song)
                || !FollowRunner.CandidateMatches(new("b", "认真的雪", "其他歌手"), song)
                || !FollowRunner.CandidateMatches(new("c", "认真的雪 (Live)", "薛之谦"), song)) throw new Exception();
            if (FollowRunner.SafeFailureReason(new("敏感歌词或服务端正文")) != "接口响应未通过协议校验") throw new Exception();
        });
        Check("跟随缺字段候选跳过并接受用户实例", () =>
        {
            var song = new PixelLyrics.Core.SongIdentity("Free Free Free", "東京スカパラダイスオーケストラ (东京斯卡乐团)/幾田りら (ikura)", "Album", TimeSpan.FromSeconds(254.233));
            var handler = new OfflineHandler(request =>
            {
                string body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                string json = request.Method == System.Net.Http.HttpMethod.Get
                    ? """{"code":0,"data":{"song":{"itemlist":[{"mid":"bad","name":"Free Free Free","singer":"unknown"},{"mid":"good","name":"Free Free Free","singer":"東京スカパラダイスオーケストラ/幾田りら"}]}}}"""
                    : body.Contains("get_song_detail_yqq")
                        ? body.Contains("bad") ? """{"code":0,"req_1":{"code":0,"data":{"track_info":{}}}}"""
                            : """{"code":0,"req_1":{"code":0,"data":{"track_info":{"mid":"good","title":"Free Free Free","singer":[{"name":"東京スカパラダイスオーケストラ"},{"name":"幾田りら"}],"album":{"title":"Album"},"interval":254}}}}"""
                        : """{"code":0,"req_1":{"code":0,"data":{"lyric":"[00:01.00]测试"}}}""";
                return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8) };
            });
            using var client = new PixelLyrics.OnlineProbe.OnlineLyricsClient(handler);
            var loaded = FollowRunner.LoadAsync(song, null, CancellationToken.None, testClient: client).GetAwaiter().GetResult();
            if (loaded.Mid != "good" || handler.Count != 4) throw new Exception();
        });
        foreach (var status in new[] { System.Net.HttpStatusCode.Unauthorized, System.Net.HttpStatusCode.Forbidden })
            Check("跟随权限错误不降级成无候选 " + status, () =>
            {
                var handler = new OfflineHandler(_ => new(status));
                using var client = new PixelLyrics.OnlineProbe.OnlineLyricsClient(handler);
                var song = new PixelLyrics.Core.SongIdentity("Song", "Artist", "Album", TimeSpan.FromSeconds(120));
                for (int i = 0; i < 2; i++)
                {
                    try { FollowRunner.LoadAsync(song, new("mid", null), CancellationToken.None, testClient: client).GetAwaiter().GetResult(); throw new Exception(); }
                    catch (PixelLyrics.OnlineProbe.ProbeException) { }
                }
                if (handler.Count != 1) throw new Exception();
            });
        Check("开场期间网络故障仍为网络异常，取消不进入无词", () =>
        {
            var song = new PixelLyrics.Core.SongIdentity("Song", "Artist", "Album", TimeSpan.FromSeconds(120));
            var intro = new TrackIntro(true, 3000);
            intro.Start(1, song.Title, TimeSpan.Zero);
            var handler = new OfflineHandler(_ => throw new System.Net.Http.HttpRequestException());
            using var client = new PixelLyrics.OnlineProbe.OnlineLyricsClient(handler);
            bool networkError = false;
            try { FollowRunner.LoadAsync(song, null, CancellationToken.None, testClient: client).GetAwaiter().GetResult(); }
            catch (System.Net.Http.HttpRequestException) { networkError = true; }
            if (!networkError || handler.Count != 1 || intro.IsShowing(1, TimeSpan.FromSeconds(3))) throw new Exception();
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { FollowRunner.LoadAsync(song, null, canceled.Token, testClient: client).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                if (handler.Count == 1) return;
            }
            throw new Exception();
        });
        Check("跟随Last Dance基础标题加歌手召回且逐请求检查停网", () =>
        {
            int searches = 0, guards = 0;
            var song = new PixelLyrics.Core.SongIdentity("Last Dance (Live)", "徐佳莹", "歌手·当打之年 第7期", TimeSpan.FromSeconds(289.939));
            var handler = new OfflineHandler(request =>
            {
                string body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                string json;
                if (request.Method == System.Net.Http.HttpMethod.Get)
                {
                    string key = Uri.UnescapeDataString(request.RequestUri!.Query);
                    searches++;
                    if (searches == 2 && !key.Contains("Last Dance 徐佳莹")) throw new Exception();
                    json = searches == 1 ? "{\"code\":0,\"data\":{\"song\":{\"itemlist\":[]}}}"
                        : "{\"code\":0,\"data\":{\"song\":{\"itemlist\":[{\"mid\":\"004TwGgS0j6pI5\",\"name\":\"Last Dance\"}]}}}";
                }
                else json = body.Contains("get_song_detail_yqq")
                    ? "{\"code\":0,\"req_1\":{\"code\":0,\"data\":{\"track_info\":{\"mid\":\"004TwGgS0j6pI5\",\"title\":\"Last Dance (Live)\",\"singer\":[{\"name\":\"徐佳莹\"}],\"album\":{\"title\":\"歌手·当打之年 第7期\"},\"interval\":289}}}}"
                    : "{\"code\":0,\"req_1\":{\"code\":0,\"data\":{\"lyric\":\"[00:01.00]测试\"}}}";
                return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8) };
            });
            using var client = new PixelLyrics.OnlineProbe.OnlineLyricsClient(handler);
            var loaded = FollowRunner.LoadAsync(song, null, CancellationToken.None, () => guards++, client).GetAwaiter().GetResult();
            if (loaded.Mid != "004TwGgS0j6pI5" || handler.Count != 4 || searches != 2 || guards != 5) throw new Exception();
        });
        Check("GUI错误分类互不混淆且不含正文", () =>
        {
            if (FollowRunner.MatchFailureReason(PixelLyrics.OnlineProbe.MatchCategory.NoCandidates)
                == FollowRunner.MatchFailureReason(PixelLyrics.OnlineProbe.MatchCategory.Ambiguous)) throw new Exception();
        });
        foreach (int delay in new[] { 1000, 5000, 60000 })
            Check("空闲持续空白边界且只发送一次 " + delay, () =>
            {
                var idle = new IdleClock(true, delay);
                var deadline = TimeSpan.FromMilliseconds(delay);
                if (idle.Observe(1, TimeSpan.Zero, true)
                    || idle.Observe(1, deadline - TimeSpan.FromTicks(1), true)
                    || !idle.Observe(1, deadline, true)
                    || idle.Observe(1, deadline + TimeSpan.FromDays(1), true)) throw new Exception();
            });
        Check("空闲暂停保留且恢复后重新完整等待", () =>
        {
            var idle = new IdleClock(true, 5000);
            idle.Observe(1, TimeSpan.Zero, true);
            if (idle.Observe(1, TimeSpan.FromSeconds(60), true, paused: true)
                || idle.Observe(1, TimeSpan.FromSeconds(90), false, paused: true)
                || idle.Observe(1, TimeSpan.FromSeconds(100), true)
                || idle.Observe(1, TimeSpan.FromMilliseconds(104999), true)
                || !idle.Observe(1, TimeSpan.FromSeconds(105), true)) throw new Exception();
        });
        Check("空闲回钟后歌词恢复覆盖并重置状态", () =>
        {
            var idle = new IdleClock(true, 5000);
            idle.Observe(1, TimeSpan.Zero, true);
            if (!idle.Observe(1, TimeSpan.FromSeconds(5), true)
                || idle.Observe(1, TimeSpan.FromSeconds(6), false)
                || idle.Observe(1, TimeSpan.FromSeconds(7), true)
                || idle.Observe(1, TimeSpan.FromMilliseconds(11999), true)
                || !idle.Observe(1, TimeSpan.FromSeconds(12), true)) throw new Exception();
        });
        Check("空闲generation变化隔离旧截止且允许新一轮", () =>
        {
            var idle = new IdleClock(true, 5000);
            idle.Observe(1, TimeSpan.Zero, true);
            if (idle.Observe(2, TimeSpan.FromSeconds(5), true)
                || idle.Observe(2, TimeSpan.FromMilliseconds(9999), true)
                || !idle.Observe(2, TimeSpan.FromSeconds(10), true)
                || idle.Observe(3, TimeSpan.FromSeconds(20), true)
                || !idle.Observe(3, TimeSpan.FromSeconds(25), true)) throw new Exception();
        });
        Check("切曲请求开场短暂空白等待不计时", () =>
        {
            var idle = new IdleClock(true, 5000);
            idle.Observe(1, TimeSpan.Zero, true);
            if (idle.Observe(1, TimeSpan.FromSeconds(30), true, waiting: true)
                || idle.Observe(1, TimeSpan.FromSeconds(40), true)
                || idle.Observe(1, TimeSpan.FromMilliseconds(44999), true)
                || !idle.Observe(1, TimeSpan.FromSeconds(45), true)) throw new Exception();
        });
        foreach (string reason in new[] { "Stopped", "Closed", "无会话", "空白行", "网络故障", "超时" })
            Check("空闲原因不伪造无词 " + reason, () =>
            {
                var idle = new IdleClock(true, 5000);
                var fallback = new NoLyricsFallback();
                idle.Observe(1, TimeSpan.Zero, true);
                if (!idle.Observe(1, TimeSpan.FromSeconds(5), true) || fallback.Active) throw new Exception();
            });
        Check("无词降级已回钟时空闲不重复发送", () =>
        {
            var idle = new IdleClock(true, 5000);
            idle.Observe(1, TimeSpan.Zero, true);
            if (idle.Observe(1, TimeSpan.FromSeconds(5), true, otherClockSent: true)
                || idle.Observe(1, TimeSpan.FromDays(1), true)) throw new Exception();
        });
        foreach (bool exiting in new[] { false, true })
            Check("空闲停止或退出永久禁用且generation不能重启 " + exiting, () =>
            {
                var idle = new IdleClock(true, 5000);
                idle.Observe(1, TimeSpan.Zero, true);
                if (exiting)
                {
                    if (idle.Observe(1, TimeSpan.FromSeconds(5), true, exiting: true)) throw new Exception();
                }
                else idle.Stop();
                if (idle.Observe(1, TimeSpan.FromDays(1), true)
                    || idle.Observe(2, TimeSpan.FromDays(2), false)
                    || idle.Observe(2, TimeSpan.FromDays(3), true)
                    || idle.Observe(2, TimeSpan.FromDays(4), true)) throw new Exception();
            });
        Check("无词阶段媒体停止先空闲回钟恢复后不二次降级写钟", () =>
        {
            var idle = new IdleClock(true, 5000);
            var fallback = new NoLyricsFallback();
            fallback.Start("歌曲甲", TimeSpan.Zero);
            idle.Observe(1, TimeSpan.Zero, true);
            if (!idle.Observe(1, TimeSpan.FromSeconds(5), true)) throw new Exception();
            fallback.MarkClock();
            if (fallback.ShouldSwitch(TimeSpan.FromSeconds(60), 5000)
                || idle.Observe(1, TimeSpan.FromSeconds(60), true, otherClockSent: fallback.ClockSent)) throw new Exception();
        });
        Check("空闲关闭完全不回钟", () =>
        {
            var idle = new IdleClock(false, 1000);
            if (idle.Observe(1, TimeSpan.Zero, true) || idle.Observe(1, TimeSpan.FromDays(1), true)) throw new Exception();
        });
        Check("跟随稳定窗口及旧请求隔离", () =>
        {
            var gate = new FollowGeneration();
            if (!gate.Observe("song-a", 1, TimeSpan.Zero)) throw new Exception();
            long first = gate.Generation;
            if (gate.IsSettled(TimeSpan.FromMilliseconds(599))) throw new Exception();
            if (!gate.IsSettled(TimeSpan.FromMilliseconds(600))) throw new Exception();
            if (!gate.CanApply(first, "song-a", 1)) throw new Exception();
            gate.Observe("song-b", 2, TimeSpan.FromSeconds(1));
            if (gate.CanApply(first, "song-a", 1) || gate.CanApply(first, "song-b", 2)) throw new Exception();
            long second = gate.Generation;
            gate.Observe("song-b", 3, TimeSpan.FromSeconds(2));
            if (gate.CanApply(second, "song-b", 3)) throw new Exception();
            gate.Observe(null, -1, TimeSpan.FromSeconds(3));
            if (gate.IsSettled(TimeSpan.FromSeconds(9)) || gate.CanApply(gate.Generation, null, -1)) throw new Exception();
        });
        foreach (string? artist in new string?[] { null, "", " \t", "歌手甲" })
            Check("开场歌名与歌手拼接及空歌手 " + (artist ?? "null"), () =>
            {
                var intro = new TrackIntro(true, 3000);
                intro.Start(1, "歌曲甲", TimeSpan.Zero, artist);
                string expected = string.IsNullOrWhiteSpace(artist) ? "歌曲甲" : "歌曲甲 - " + artist;
                if (intro.Title != expected || LyricViewport.Frame(intro.Title!, TimeSpan.Zero) != expected)
                    throw new Exception();
                var disabled = new TrackIntro(false, 3000);
                disabled.Start(1, "歌曲甲", TimeSpan.Zero, artist);
                intro.Start(2, null, TimeSpan.Zero, artist);
                if (disabled.Title is not null || intro.Title is not null) throw new Exception();
            });
        Check("开场原样保留实际多歌手斜杠及括号别名字段", () =>
        {
            var song = new PixelLyrics.Core.SongIdentity("Free Free Free",
                "東京スカパラダイスオーケストラ (东京斯卡乐团)/幾田りら (ikura)", "Album", TimeSpan.FromSeconds(254.233));
            var intro = new TrackIntro(true, 3000);
            intro.Start(1, song.Title, TimeSpan.Zero, song.Artist);
            if (intro.Title != "Free Free Free - 東京スカパラダイスオーケストラ (东京斯卡乐团)/幾田りら (ikura)"
                || string.Concat(LyricViewport.Pages(intro.Title!)) != intro.Title) throw new Exception();
            var fallback = new NoLyricsFallback();
            fallback.Start(song.Title, TimeSpan.Zero);
            if (fallback.Title != "Free Free Free") throw new Exception();
        });
        Check("长开场沿用分页且每页不超过54字节、不延长总时长", () =>
        {
            var intro = new TrackIntro(true, 3000);
            intro.Start(1, new string('歌', 40), TimeSpan.Zero, new string('手', 40));
            foreach (int width in new[] { 32, 64 })
            {
                var pages = LyricViewport.Pages(intro.Title!, width);
                if (pages.Count <= 1 || string.Concat(pages) != intro.Title
                    || pages.Any(page => System.Text.Encoding.UTF8.GetByteCount(page) > 54)
                    || (width == 64 && System.Text.Encoding.UTF8.GetByteCount(pages[0]) != 54)
                    || LyricViewport.Frame(intro.Title!, TimeSpan.FromMilliseconds(2799), width) != pages[0]
                    || LyricViewport.Frame(intro.Title!, TimeSpan.FromMilliseconds(2800), width) != pages[1])
                    throw new Exception();
            }
            if (!intro.IsShowing(1, TimeSpan.FromMilliseconds(2999))
                || intro.IsShowing(1, TimeSpan.FromMilliseconds(3000))) throw new Exception();
        });
        foreach (int hold in new[] { 1000, 3000, 15000 })
            Check("开场墙钟边界、generation隔离及切曲重置 " + hold, () =>
            {
                var intro = new TrackIntro(true, hold);
                var start = TimeSpan.FromSeconds(10);
                if (intro.IsShowing(1, start) || intro.ReplacesFallbackTitle(1)) throw new Exception();
                intro.Start(1, "歌曲甲", start);
                var deadline = start + TimeSpan.FromMilliseconds(hold);
                if (!intro.IsShowing(1, start) || !intro.IsShowing(1, deadline - TimeSpan.FromTicks(1))
                    || intro.IsShowing(1, deadline) || intro.IsShowing(2, start)
                    || !intro.ReplacesFallbackTitle(1) || intro.Title != "歌曲甲") throw new Exception();
                intro.Start(2, "歌曲乙", deadline);
                if (intro.IsShowing(1, deadline) || intro.ReplacesFallbackTitle(1)
                    || !intro.IsShowing(2, deadline) || intro.Elapsed(deadline) != TimeSpan.Zero
                    || intro.Title != "歌曲乙") throw new Exception();
                intro.Start(3, null, deadline);
                if (intro.IsShowing(3, deadline) || intro.ReplacesFallbackTitle(3)) throw new Exception();
            });
        Check("开场关闭保持原无词歌名停留语义", () =>
        {
            var intro = new TrackIntro(false, 3000);
            intro.Start(1, "歌曲甲", TimeSpan.Zero);
            var fallback = new NoLyricsFallback();
            fallback.Start("歌曲甲", TimeSpan.FromSeconds(4));
            if (intro.IsShowing(1, TimeSpan.Zero) || intro.ReplacesFallbackTitle(1)
                || fallback.ShouldSwitch(TimeSpan.FromSeconds(8), 5000)
                || !fallback.ShouldSwitch(TimeSpan.FromSeconds(9), 5000)) throw new Exception();
        });
        Check("异步取词不等待开场且结束使用最新进度及偏移", () =>
        {
            var gate = new FollowGeneration(600);
            var intro = new TrackIntro(true, 3000);
            gate.Observe("song-a", 1, TimeSpan.Zero);
            long generation = gate.Generation;
            intro.Start(generation, "歌曲甲", TimeSpan.Zero);
            var pending = new TaskCompletionSource<PixelLyrics.Core.LyricsDocument>();
            if (!gate.IsSettled(TimeSpan.FromMilliseconds(600))
                || !intro.IsShowing(generation, TimeSpan.FromMilliseconds(600))) throw new Exception();
            pending.SetResult(PixelLyrics.Core.LrcParser.Parse("[00:01]首句\n[00:20]当前句\n[00:40]跳转句").Document);
            var lyrics = pending.Task.GetAwaiter().GetResult();
            if (!intro.IsShowing(generation, TimeSpan.FromMilliseconds(2999))
                || intro.IsShowing(generation, TimeSpan.FromSeconds(3))
                || lyrics.GetLineAt(TimeSpan.FromMilliseconds(20500) - TimeSpan.FromMilliseconds(500))?.Text != "当前句"
                || lyrics.GetLineAt(TimeSpan.FromSeconds(45))?.Text != "跳转句") throw new Exception();
            gate.Observe("song-b", 2, TimeSpan.FromSeconds(4));
            intro.Start(gate.Generation, "歌曲乙", TimeSpan.FromSeconds(4));
            if (gate.CanApply(generation, "song-a", 1) || intro.IsShowing(generation, TimeSpan.FromSeconds(4))) throw new Exception();
        });
        Check("开场覆盖无词歌名阶段：早到晚到均不二次停留且时钟一次", () =>
        {
            foreach (int resultMs in new[] { 500, 5000 })
            {
                var intro = new TrackIntro(true, 3000);
                intro.Start(1, "歌曲甲", TimeSpan.Zero);
                var fallback = new NoLyricsFallback();
                fallback.Start("歌曲甲", TimeSpan.FromMilliseconds(resultMs));
                var now = TimeSpan.FromMilliseconds(Math.Max(3000, resultMs));
                if (intro.IsShowing(1, now) || !intro.ReplacesFallbackTitle(1)
                    || !fallback.Active || fallback.ClockSent) throw new Exception();
                fallback.MarkClock();
                if (!fallback.ClockSent || fallback.ShouldSwitch(now + TimeSpan.FromDays(1), 5000)) throw new Exception();
            }
        });
        Check("开场到期不伪造无词或重放标题，暂停也按墙钟结束", () =>
        {
            var intro = new TrackIntro(true, 3000);
            var fallback = new NoLyricsFallback();
            intro.Start(1, "歌曲甲", TimeSpan.Zero);
            if (intro.IsShowing(1, TimeSpan.FromSeconds(30)) || fallback.Active) throw new Exception();
            // 请求尚未完成、取消、超时或网络错误均没有调用 fallback.Start。
            if (!intro.ReplacesFallbackTitle(1) || fallback.ClockSent) throw new Exception();
        });
        foreach (int hold in new[] { 1000, 5000, 60000 })
            Check("无词回退首次计时边界且时钟只切一次 " + hold, () =>
            {
                var fallback = new NoLyricsFallback();
                var started = TimeSpan.FromSeconds(90);
                if (fallback.Active || fallback.ClockSent || fallback.Title != ""
                    || fallback.ShouldSwitch(started + TimeSpan.FromDays(1), hold)) throw new Exception();
                fallback.Start("歌曲甲", started);
                if (!fallback.Active || fallback.ClockSent || fallback.Title != "歌曲甲" || fallback.StartedAt != started)
                    throw new Exception();
                var deadline = started + TimeSpan.FromMilliseconds(hold);
                foreach (var now in new[] { started - TimeSpan.FromTicks(1), started, deadline - TimeSpan.FromTicks(1) })
                    if (fallback.ShouldSwitch(now, hold)) throw new Exception();
                if (!fallback.ShouldSwitch(deadline, hold) || !fallback.ShouldSwitch(deadline + TimeSpan.FromTicks(1), hold))
                    throw new Exception();
                fallback.MarkClock();
                fallback.MarkClock();
                if (!fallback.Active || !fallback.ClockSent || fallback.Title != "歌曲甲" || fallback.StartedAt != started
                    || fallback.ShouldSwitch(deadline, hold) || fallback.ShouldSwitch(deadline + TimeSpan.FromDays(1), hold))
                    throw new Exception();
            });
        Check("无词回退重置及新曲重新计时", () =>
        {
            var fallback = new NoLyricsFallback();
            fallback.Start("歌曲甲", TimeSpan.Zero);
            fallback.MarkClock();
            fallback.Reset();
            if (fallback.Active || fallback.ClockSent || fallback.Title != ""
                || fallback.ShouldSwitch(TimeSpan.FromDays(1), 5000)) throw new Exception();
            fallback.Reset();
            fallback.Start("歌曲甲", TimeSpan.FromSeconds(20));
            if (!fallback.Active || fallback.ClockSent || fallback.Title != "歌曲甲"
                || fallback.StartedAt != TimeSpan.FromSeconds(20)
                || fallback.ShouldSwitch(TimeSpan.FromMilliseconds(24999), 5000)
                || !fallback.ShouldSwitch(TimeSpan.FromSeconds(25), 5000)) throw new Exception();
            fallback.MarkClock();
            fallback.Start("歌曲乙", TimeSpan.FromSeconds(30));
            if (!fallback.Active || fallback.ClockSent || fallback.Title != "歌曲乙"
                || fallback.StartedAt != TimeSpan.FromSeconds(30)
                || fallback.ShouldSwitch(TimeSpan.FromMilliseconds(34999), 5000)
                || !fallback.ShouldSwitch(TimeSpan.FromSeconds(35), 5000)) throw new Exception();
            fallback.Reset();
            if (fallback.Active || fallback.ClockSent || fallback.Title != ""
                || fallback.ShouldSwitch(TimeSpan.FromDays(1), 5000)) throw new Exception();
        });
        foreach (int settle in new[] { 200, 1300, 5000 })
            Check("跟随自定义稳定时间及重复观察不重计时 " + settle, () =>
            {
                var gate = new FollowGeneration(settleMs: settle);
                var started = TimeSpan.FromSeconds(10);
                if (gate.IsSettled(TimeSpan.FromDays(1)) || !gate.Observe("song-a", 1, started)) throw new Exception();
                long generation = gate.Generation;
                var deadline = started + TimeSpan.FromMilliseconds(settle);
                if (gate.IsSettled(started) || gate.IsSettled(deadline - TimeSpan.FromTicks(1))
                    || gate.Observe("song-a", 1, deadline - TimeSpan.FromTicks(1)) || gate.Generation != generation
                    || !gate.IsSettled(deadline) || !gate.IsSettled(deadline + TimeSpan.FromTicks(1))) throw new Exception();
                if (!gate.Observe("song-b", 2, deadline) || gate.CanApply(generation, "song-a", 1)
                    || gate.IsSettled(deadline + TimeSpan.FromMilliseconds(settle) - TimeSpan.FromTicks(1))
                    || !gate.IsSettled(deadline + TimeSpan.FromMilliseconds(settle))) throw new Exception();
            });
        Check("九到十六汉字保持静态且不自动滚动", () =>
        {
            for (int count = 9; count <= 16; count++)
            {
                string text = new('字', count);
                foreach (int ms in new[] { 0, 1350, 2800, 60000 })
                {
                    var plan = LyricDisplayPlan.Create(text, TimeSpan.FromMilliseconds(ms), false);
                    if (plan.Text != text || plan.Effect != PixelBar.Sdk.Protocol.TextDisplayEffect.Static) throw new Exception();
                }
            }
        });
        Check("英文分页保留整词及收尾标点", () =>
        {
            if (!LyricViewport.Pages("You are beautiful", 16).SequenceEqual(new[] { "You are", "beautiful" })
                || !LyricViewport.Pages("Hello beautiful!", 12).SequenceEqual(new[] { "Hello", "beautiful!" })
                || !LyricViewport.Pages("hello   world", 8).SequenceEqual(new[] { "hello", "world" })
                || !LyricViewport.Pages("你好 beautiful 世界", 12).SequenceEqual(new[] { "你好", "beautiful 世", "界" })) throw new Exception();
        });
        Check("英文分页保留撇号音标及连字符", () =>
        {
            foreach (string word in new[] { "don't", "we’re", "café", "cafe\u0301", "well-known" })
            {
                var pages = LyricViewport.Pages("12345678901 " + word, 12);
                if (pages.Count != 2 || pages[1] != word) throw new Exception(word);
            }
            if (!LyricViewport.Pages("one-two-three", 8).SequenceEqual(new[] { "one-two-", "three" })) throw new Exception();
        });
        Check("整词分页遵守双重容量且保持正文及播放时序", () =>
        {
            foreach (int width in new[] { 8, 32, 64 })
            foreach (string text in new[] { "We’re singing beautiful songs, together!", "你好 café cafe\u0301 world! 世界", new string('A', 120), string.Concat(Enumerable.Repeat("é", 60)), "one-two-three-four-five-six" })
            {
                var pages = LyricViewport.Pages(text, width);
                string WithoutSpace(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
                if (WithoutSpace(string.Concat(pages)) != WithoutSpace(text)) throw new Exception();
                foreach (string page in pages)
                {
                    int units = 0;
                    var elements = System.Globalization.StringInfo.GetTextElementEnumerator(page);
                    while (elements.MoveNext()) units += elements.GetTextElement().EnumerateRunes().Any(r => r.Value > 0xFF) ? 2 : 1;
                    if (units > width || System.Text.Encoding.UTF8.GetByteCount(page) > PixelBar.Sdk.Protocol.TextReportEncoder.MaxUtf8Bytes
                        || page.Length == 0 || page != page.Trim()) throw new Exception();
                }
            }
            const string lyric = "You are beautiful";
            if (LyricViewport.Frame(lyric, TimeSpan.FromMilliseconds(2799), 16) != "You are"
                || LyricViewport.Frame(lyric, TimeSpan.FromMilliseconds(2800), 16) != "beautiful"
                || LyricViewport.Frame(lyric, TimeSpan.FromMinutes(1), 16) != "beautiful"
                || LyricViewport.Frame(lyric, TimeSpan.Zero, 16) != "You are") throw new Exception();
        });
        Check("十七汉字分页且首屏停留两秒八", () =>
        {
            string first = new('字', 16);
            string text = first + "尾";
            if (LyricViewport.Pages(text).Count != 2) throw new Exception();
            foreach (int ms in new[] { -1000, 0, 1350, 2799 })
                if (LyricViewport.Frame(text, TimeSpan.FromMilliseconds(ms)) != first) throw new Exception();
            if (LyricViewport.Frame(text, TimeSpan.FromMilliseconds(2800)) != "尾") throw new Exception();
        });
        Check("自定义宽度及停留转传且暂停保持静态", () =>
        {
            string text = "甲乙丙丁尾";
            if (!LyricViewport.Pages(text, widthUnits: 8).SequenceEqual(new[] { "甲乙丙丁", "尾" })
                || LyricViewport.Pages(new string('A', 64), widthUnits: 64).Count != 2) throw new Exception();
            foreach (int hold in new[] { 500, 1300, 10000 })
            {
                foreach (int ms in new[] { -1, 0, hold - 1, hold, hold * 2 })
                {
                    string expected = ms < hold ? "甲乙丙丁" : "尾";
                    if (LyricViewport.Frame(text, TimeSpan.FromMilliseconds(ms), 8, hold) != expected) throw new Exception();
                    foreach (bool paused in new[] { false, true })
                    {
                        var plan = LyricDisplayPlan.Create(text, TimeSpan.FromMilliseconds(ms), paused, widthUnits: 8, pageHoldMs: hold);
                        if (plan.Text != expected || plan.Effect != PixelBar.Sdk.Protocol.TextDisplayEffect.Static) throw new Exception();
                    }
                }
            }
        });
        foreach (int width in new[] { 7, 65, int.MinValue, int.MaxValue })
        {
            foreach (Action action in new Action[]
            {
                () => { LyricViewport.Pages("", width); },
                () => { LyricViewport.Frame("", TimeSpan.Zero, widthUnits: width); },
                () => { LyricDisplayPlan.Create("", TimeSpan.Zero, false, widthUnits: width); }
            })
                Check("拒绝显示宽度 " + width, () =>
                {
                    try { action(); }
                    catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "widthUnits") { return; }
                    throw new Exception();
                });
        }
        foreach (int hold in new[] { 499, 10001, int.MinValue, int.MaxValue })
        {
            foreach (Action action in new Action[]
            {
                () => { LyricViewport.Frame("", TimeSpan.Zero, holdMs: hold); },
                () => { LyricDisplayPlan.Create("", TimeSpan.Zero, true, pageHoldMs: hold); }
            })
                Check("拒绝分页停留 " + hold, () =>
                {
                    try { action(); }
                    catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "holdMs") { return; }
                    throw new Exception();
                });
        }
        Check("尾页保持不循环拖动能回到首屏", () =>
        {
            string text = new string('甲', 16) + new string('乙', 16) + "末尾";
            if (LyricViewport.Frame(text, TimeSpan.FromMilliseconds(2800)) != new string('乙', 16)) throw new Exception();
            foreach (int ms in new[] { 5600, 8400, 60000 })
                if (LyricViewport.Frame(text, TimeSpan.FromMilliseconds(ms)) != "末尾") throw new Exception();
            if (LyricViewport.Frame(text, TimeSpan.Zero) != new string('甲', 16)) throw new Exception();
        });
        Check("分页保持完整字素且不超过报文字节预算", () =>
        {
            foreach (int width in new[] { 8, 32, 64 })
            foreach (string element in new[] { "👨‍👩‍👧‍👦", "e\u0301", "中", "A" })
            {
                string text = string.Concat(Enumerable.Repeat(element, 80));
                var pages = LyricViewport.Pages(text, width);
                if (string.Concat(pages) != text) throw new Exception();
                foreach (string page in pages)
                {
                    if (System.Text.Encoding.UTF8.GetByteCount(page) > PixelBar.Sdk.Protocol.TextReportEncoder.MaxUtf8Bytes) throw new Exception();
                    var elements = System.Globalization.StringInfo.GetTextElementEnumerator(page);
                    while (elements.MoveNext())
                        if (elements.GetTextElement() != element) throw new Exception();
                }
            }
        });
        Check("ASCII宽度边界控制字符过滤及异常大字素", () =>
        {
            if (LyricViewport.Pages(new string('A', 32)).Count != 1
                || LyricViewport.Pages(new string('A', 33)).Count != 2) throw new Exception();
            if (LyricViewport.Frame("甲\n乙\t丙", TimeSpan.Zero) != "甲乙丙") throw new Exception();
            if (LyricViewport.Frame("a" + new string('\u0301', 60), TimeSpan.Zero) != "�") throw new Exception();
            if (LyricViewport.Frame("", TimeSpan.Zero) != "") throw new Exception();
        });
        Check("暂停同一位置保持同页且所有歌词计划均静态", () =>
        {
            string text = new string('甲', 16) + "末尾";
            foreach (int ms in new[] { 0, 2800, 60000 })
            {
                var position = TimeSpan.FromMilliseconds(ms);
                var playing = LyricDisplayPlan.Create(text, position, false);
                var paused = LyricDisplayPlan.Create(text, position, true);
                if (playing != paused || paused.Effect != PixelBar.Sdk.Protocol.TextDisplayEffect.Static) throw new Exception();
            }
        });
        Accept("--online");
        Accept("--online", "--send", "--duration", "3");
        Accept("--online", "--songmid", "m1", "--artist-alias", "歌手（现场）");
        Accept("--send", "--lrc", "test.lrc");
        Reject("--send");
        Reject("--songmid", "m1");
        Reject("--artist-alias", "歌手");
        Reject("--online", "--online");
        Reject("--online", "--songmid", "m1", "--songmid", "m2");
        Reject("--online", "--artist-alias", "");
        foreach (string mode in new[] { "--accessibility", "--desktop-lyrics", "--test-screen", "--screen-sequence" })
            Reject("--online", mode);
        Reject("--online", "--lrc", "test.lrc");
        Reject("--online", "--duration", "0");
        Accept("--online", "--follow", "--settings", "settings.json", "--save-settings");
        Accept("--offset-ms", "-5000");
        Accept("--offset-ms", "+5000");
        Reject("--follow");
        Reject("--online", "--follow", "--songmid", "m1");
        Reject("--online", "--follow", "--artist-alias", "歌手");
        Reject("--online", "--follow", "--follow");
        Reject("--settings");
        Reject("--settings", " ");
        Reject("--settings", "a", "--settings", "b");
        Reject("--save-settings");
        Reject("--settings", "a", "--save-settings", "--save-settings");
        Reject("--offset-ms", "5001");
        Reject("--offset-ms", "-5001");
        Reject("--offset-ms", "0", "--offset-ms", "1");
        Check("显式零偏移与缺省偏移可区分", () =>
        {
            if (ProbeOptions.Parse([]).HasOffset || !ProbeOptions.Parse(["--offset-ms", "0"]).HasOffset)
                throw new Exception();
        });
        string directory = Path.Combine(Path.GetTempPath(), "PixelLyrics-Probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "settings.json");
            var song = new PixelLyrics.Core.SongIdentity("歌曲", "歌手", "专辑", TimeSpan.FromSeconds(123));
            Check("配置不存在返回空", () =>
            {
                ProbeSettings empty = ProbeSettings.Load(file);
                if (empty.OffsetMs != 0 || empty.Bindings.Count != 0 || File.Exists(file)
                    || empty.DisplayWidthUnits != 32 || empty.PageHoldMs != 2800 || empty.ClockStyle != 1
                    || empty.NoLyricsTitleHoldMs != 5000 || empty.PollIntervalMs != 200
                    || empty.TrackSettleMs != 600 || empty.RequestTimeoutMs != 10000) throw new Exception();
            });
            Check("旧配置缺字段使用默认值且读取不改文件", () =>
            {
                foreach (string json in new[] { "{}", "{\"OffsetMs\":123,\"Bindings\":{}}", "{\"DisplayWidthUnits\":8}", "{\"DisplayWidthUnits\":32,\"PageHoldMs\":2800,\"ClockStyle\":1}" })
                {
                    File.WriteAllText(file, json);
                    byte[] before = File.ReadAllBytes(file);
                    var loaded = ProbeSettings.Load(file);
                    if (loaded.DisplayWidthUnits != (json.Contains("\"DisplayWidthUnits\":8") ? 8 : 32)
                        || loaded.PageHoldMs != 2800 || loaded.ClockStyle != 1
                        || loaded.OffsetMs != (json.Contains("OffsetMs") ? 123 : 0) || loaded.Bindings.Count != 0
                        || loaded.NoLyricsTitleHoldMs != 5000 || loaded.PollIntervalMs != 200
                        || loaded.TrackSettleMs != 600 || loaded.RequestTimeoutMs != 10000
                        || !before.SequenceEqual(File.ReadAllBytes(file))) throw new Exception();
                }
            });
            Check("空闲旧配置默认启用5000且读取不改文件", () =>
            {
                File.WriteAllText(file, "{\"OffsetMs\":123}");
                byte[] before = File.ReadAllBytes(file);
                var loaded = ProbeSettings.Load(file);
                if (!loaded.IdleClockEnabled || loaded.IdleClockDelayMs != 5000
                    || !before.SequenceEqual(File.ReadAllBytes(file))) throw new Exception();
            });
            foreach (bool enabled in new[] { false, true })
            foreach (int delay in new[] { 1000, 5000, 7351, 60000 })
                Check("空闲配置保存加载及独立运行快照 " + enabled + "/" + delay, () =>
                {
                    new ProbeSettings { IdleClockEnabled = enabled, IdleClockDelayMs = delay }.Save(file);
                    var loaded = ProbeSettings.Load(file);
                    var runtime = new ProbeSettings();
                    runtime.ApplyRuntimeOverrides(loaded);
                    loaded.IdleClockEnabled = !enabled;
                    loaded.IdleClockDelayMs = 2000;
                    using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file));
                    if (runtime.IdleClockEnabled != enabled || runtime.IdleClockDelayMs != delay
                        || json.RootElement.GetProperty("IdleClockEnabled").GetBoolean() != enabled
                        || json.RootElement.GetProperty("IdleClockDelayMs").GetInt32() != delay) throw new Exception();
                    runtime.ApplyRuntimeOverrides(new ProbeSettings());
                    if (!runtime.IdleClockEnabled || runtime.IdleClockDelayMs != 5000) throw new Exception();
                });
            foreach (string json in new[] { "{\"IdleClockEnabled\":null}", "{\"IdleClockEnabled\":0}",
                "{\"IdleClockEnabled\":\"true\"}", "{\"IdleClockEnabled\":{}}", "{\"IdleClockEnabled\":[]}",
                "{\"IdleClockEnabled\":true,\"IdleClockEnabled\":false}" })
                Check("空闲布尔严格类型及重复字段拒绝 " + json, () =>
                {
                    File.WriteAllText(file, json);
                    try { ProbeSettings.Load(file); } catch (InvalidDataException) { return; }
                    throw new Exception();
                });
            Check("开场旧配置默认关闭且读取不改文件", () =>
            {
                File.WriteAllText(file, "{\"OffsetMs\":123}");
                byte[] before = File.ReadAllBytes(file);
                var settings = ProbeSettings.Load(file);
                if (settings.TrackIntroEnabled || settings.TrackIntroHoldMs != 3000
                    || !before.SequenceEqual(File.ReadAllBytes(file))) throw new Exception();
            });
            foreach (bool enabled in new[] { false, true })
            foreach (int hold in new[] { 1000, 3000, 7351, 15000 })
                Check("开场配置及运行快照 roundtrip " + enabled + "/" + hold, () =>
                {
                    var original = new ProbeSettings { TrackIntroEnabled = enabled, TrackIntroHoldMs = hold };
                    original.Save(file);
                    byte[] before = File.ReadAllBytes(file);
                    var loaded = ProbeSettings.Load(file);
                    var runtime = new ProbeSettings();
                    runtime.ApplyRuntimeOverrides(loaded);
                    loaded.TrackIntroEnabled = !enabled;
                    loaded.TrackIntroHoldMs = hold == 1000 ? 15000 : 1000;
                    using var json = System.Text.Json.JsonDocument.Parse(before);
                    if (runtime.TrackIntroEnabled != enabled || runtime.TrackIntroHoldMs != hold
                        || json.RootElement.GetProperty("TrackIntroEnabled").GetBoolean() != enabled
                        || json.RootElement.GetProperty("TrackIntroHoldMs").GetInt32() != hold
                        || !before.SequenceEqual(File.ReadAllBytes(file))) throw new Exception();
                    runtime.ApplyRuntimeOverrides(new ProbeSettings());
                    if (runtime.TrackIntroEnabled || runtime.TrackIntroHoldMs != 3000) throw new Exception();
                });
            foreach (string value in new[] { "null", "0", "1", "\"true\"", "{}", "[]" })
                Check("开场布尔严格类型拒绝 " + value, () =>
                {
                    File.WriteAllText(file, "{\"TrackIntroEnabled\":" + value + "}");
                    try { ProbeSettings.Load(file); }
                    catch (InvalidDataException) { return; }
                    throw new Exception();
                });
            Check("开场布尔重复字段拒绝", () =>
            {
                File.WriteAllText(file, "{\"TrackIntroEnabled\":true,\"TrackIntroEnabled\":false}");
                try { ProbeSettings.Load(file); }
                catch (InvalidDataException) { return; }
                throw new Exception();
            });
            Check("新增配置边界及自定义值 roundtrip", () =>
            {
                foreach (var values in new[] { (8, 500, 1), (64, 10000, 11), (24, 1700, 6) })
                {
                    new ProbeSettings { DisplayWidthUnits = values.Item1, PageHoldMs = values.Item2, ClockStyle = values.Item3 }.Save(file);
                    var loaded = ProbeSettings.Load(file);
                    if (loaded.DisplayWidthUnits != values.Item1 || loaded.PageHoldMs != values.Item2 || loaded.ClockStyle != values.Item3)
                        throw new Exception();
                    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file));
                    if (document.RootElement.GetProperty("DisplayWidthUnits").GetInt32() != values.Item1
                        || document.RootElement.GetProperty("PageHoldMs").GetInt32() != values.Item2
                        || document.RootElement.GetProperty("ClockStyle").GetInt32() != values.Item3) throw new Exception();
                }
            });
            Check("跟随计时配置上下界及自定义值 roundtrip", () =>
            {
                foreach (var values in new[] { (1000, 100, 200, 2000), (60000, 1000, 5000, 30000), (7300, 350, 1400, 17000) })
                {
                    var settings = new ProbeSettings
                    {
                        NoLyricsTitleHoldMs = values.Item1, PollIntervalMs = values.Item2,
                        TrackSettleMs = values.Item3, RequestTimeoutMs = values.Item4
                    };
                    settings.Save(file);
                    var loaded = ProbeSettings.Load(file);
                    if (loaded.NoLyricsTitleHoldMs != values.Item1 || loaded.PollIntervalMs != values.Item2
                        || loaded.TrackSettleMs != values.Item3 || loaded.RequestTimeoutMs != values.Item4) throw new Exception();
                    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file));
                    if (document.RootElement.GetProperty("NoLyricsTitleHoldMs").GetInt32() != values.Item1
                        || document.RootElement.GetProperty("PollIntervalMs").GetInt32() != values.Item2
                        || document.RootElement.GetProperty("TrackSettleMs").GetInt32() != values.Item3
                        || document.RootElement.GetProperty("RequestTimeoutMs").GetInt32() != values.Item4) throw new Exception();
                }
            });
            Check("运行时覆盖包含时钟及跟随计时且不覆盖其他配置或写文件", () =>
            {
                var settings = new ProbeSettings { OffsetMs = -300, DisplayWidthUnits = 24, PageHoldMs = 1700 };
                settings.SetVerifiedBinding(song, "MID1", "原歌手");
                settings.Save(file);
                byte[] before = File.ReadAllBytes(file);
                var overrides = new ProbeSettings
                {
                    OffsetMs = 400, DisplayWidthUnits = 48, PageHoldMs = 3700, ClockStyle = 11,
                    NoLyricsTitleHoldMs = 7300, PollIntervalMs = 350, TrackSettleMs = 1400, RequestTimeoutMs = 17000
                };
                overrides.SetVerifiedBinding(song, "MID2", "新歌手");
                settings.ApplyRuntimeOverrides(overrides);
                if (settings.ClockStyle != 11 || settings.NoLyricsTitleHoldMs != 7300 || settings.PollIntervalMs != 350
                    || settings.TrackSettleMs != 1400 || settings.RequestTimeoutMs != 17000
                    || settings.OffsetMs != -300 || settings.DisplayWidthUnits != 24 || settings.PageHoldMs != 1700
                    || settings.Bindings.Count != 1 || settings.Bindings[song.Id] != new ProbeSongBinding("MID1", "原歌手")
                    || overrides.Bindings[song.Id] != new ProbeSongBinding("MID2", "新歌手")
                    || !before.SequenceEqual(File.ReadAllBytes(file))) throw new Exception();
                settings.ApplyRuntimeOverrides(new ProbeSettings());
                if (settings.ClockStyle != 1 || settings.NoLyricsTitleHoldMs != 5000 || settings.PollIntervalMs != 200
                    || settings.TrackSettleMs != 600 || settings.RequestTimeoutMs != 10000
                    || settings.OffsetMs != -300 || settings.DisplayWidthUnits != 24 || settings.PageHoldMs != 1700
                    || settings.Bindings.Count != 1 || settings.Bindings[song.Id] != new ProbeSongBinding("MID1", "原歌手")
                    || !before.SequenceEqual(File.ReadAllBytes(file))) throw new Exception();
            });
            foreach (var field in new[]
            {
                ("DisplayWidthUnits", 7, 65), ("PageHoldMs", 499, 10001), ("ClockStyle", 0, 12),
                ("TrackIntroHoldMs", 999, 15001), ("NoLyricsTitleHoldMs", 999, 60001), ("PollIntervalMs", 99, 1001),
                ("TrackSettleMs", 199, 5001), ("RequestTimeoutMs", 1999, 30001), ("IdleClockDelayMs", 999, 60001)
            })
            {
                foreach (string value in new[] { field.Item2.ToString(), field.Item3.ToString(), "null", "true", "\"8\"", "8.5", "2147483648", "{}", "[]" })
                    Check("拒绝新增配置字段 " + field.Item1 + "=" + value, () =>
                    {
                        File.WriteAllText(file, "{\"" + field.Item1 + "\":" + value + "}");
                        try { ProbeSettings.Load(file); }
                        catch (InvalidDataException) { return; }
                        throw new Exception();
                    });
                Check("拒绝新增配置重复字段 " + field.Item1, () =>
                {
                    int valid = field.Item2 + 1;
                    File.WriteAllText(file, "{\"" + field.Item1 + "\":" + valid + ",\"" + field.Item1 + "\":" + valid + "}");
                    try { ProbeSettings.Load(file); }
                    catch (InvalidDataException) { return; }
                    throw new Exception();
                });
                foreach (int value in new[] { field.Item2, field.Item3 })
                    Check("拒绝保存越界配置 " + field.Item1 + "=" + value, () =>
                    {
                        new ProbeSettings().Save(file);
                        byte[] before = File.ReadAllBytes(file);
                        var invalid = new ProbeSettings();
                        switch (field.Item1)
                        {
                            case "DisplayWidthUnits": invalid.DisplayWidthUnits = value; break;
                            case "PageHoldMs": invalid.PageHoldMs = value; break;
                            case "ClockStyle": invalid.ClockStyle = value; break;
                            case "TrackIntroHoldMs": invalid.TrackIntroHoldMs = value; break;
                            case "NoLyricsTitleHoldMs": invalid.NoLyricsTitleHoldMs = value; break;
                            case "PollIntervalMs": invalid.PollIntervalMs = value; break;
                            case "TrackSettleMs": invalid.TrackSettleMs = value; break;
                            case "RequestTimeoutMs": invalid.RequestTimeoutMs = value; break;
                            case "IdleClockDelayMs": invalid.IdleClockDelayMs = value; break;
                        }
                        var runtime = new ProbeSettings();
                        bool rejected = false;
                        try { runtime.ApplyRuntimeOverrides(invalid); }
                        catch (InvalidDataException) { rejected = true; }
                        if (!rejected || runtime.ClockStyle != 1 || runtime.NoLyricsTitleHoldMs != 5000
                            || runtime.PollIntervalMs != 200 || runtime.TrackSettleMs != 600 || runtime.RequestTimeoutMs != 10000)
                            throw new Exception();
                        try { invalid.Save(file); }
                        catch (InvalidDataException)
                        {
                            if (before.SequenceEqual(File.ReadAllBytes(file)) && Directory.GetFiles(directory, "*.tmp").Length == 0) return;
                        }
                        throw new Exception();
                    });
            }
            Check("配置 roundtrip 与完整身份绑定", () =>
            {
                var settings = new ProbeSettings { OffsetMs = -5000, DisplayWidthUnits = 24, PageHoldMs = 1700, ClockStyle = 6 };
                settings.SetVerifiedBinding(song, "MID1", "歌手（现场）");
                settings.Save(file);
                ProbeSettings loaded = ProbeSettings.Load(file);
                if (loaded.OffsetMs != -5000 || loaded.DisplayWidthUnits != 24 || loaded.PageHoldMs != 1700 || loaded.ClockStyle != 6
                    || loaded.Bindings[song.Id] != new ProbeSongBinding("MID1", "歌手（现场）"))
                    throw new Exception();
                var other = new PixelLyrics.Core.SongIdentity(song.Title, song.Artist, "其他专辑", song.Duration);
                if (loaded.Bindings.ContainsKey(other.Id)) throw new Exception();
                loaded.OffsetMs = 5000;
                loaded.Save(file);
                if (ProbeSettings.Load(file).OffsetMs != 5000) throw new Exception();
            });
            Check("启动只保存显式 offset，读取不改旧文件", () =>
            {
                byte[] before = File.ReadAllBytes(file);
                var loaded = ProbeSettings.LoadForRun(ProbeOptions.Parse(["--settings", file, "--save-settings"]));
                if (loaded.OffsetMs != 5000 || !before.SequenceEqual(File.ReadAllBytes(file))) throw new Exception();
                ProbeSettings.LoadForRun(ProbeOptions.Parse(["--settings", file, "--offset-ms", "0", "--save-settings"]));
                if (ProbeSettings.Load(file).OffsetMs != 0) throw new Exception();
                ProbeSettings.LoadForRun(ProbeOptions.Parse(["--settings", file, "--offset-ms", "-300"]));
                if (ProbeSettings.Load(file).OffsetMs != 0) throw new Exception();
            });
            Check("保存失败保留旧配置", () =>
            {
                byte[] before = File.ReadAllBytes(file);
                try { new ProbeSettings { OffsetMs = 5001 }.Save(file); throw new Exception(); }
                catch (InvalidDataException) { }
                using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { new ProbeSettings().Save(file); throw new Exception(); }
                    catch (IOException) { }
                }
                if (!before.SequenceEqual(File.ReadAllBytes(file)) || Directory.GetFiles(directory, "*.tmp").Length != 0)
                    throw new Exception();
            });
            foreach (string json in new[]
            {
                "{", "null", "[]", "{\"OffsetMs\":5001}", "{\"OffsetMs\":-5001}",
                "{\"OffsetMs\":1.5}", "{\"OffsetMs\":\"0\"}", "{\"OffsetMs\":0,\"OffsetMs\":1}",
                "{\"Online\":true}", "{\"Bindings\":null}", "{\"Bindings\":{\"short\":{\"Mid\":\"m\"}}}",
                $"{{\"Bindings\":{{\"{song.Id}\":{{\"Mid\":\" \"}}}}}}",
                $"{{\"Bindings\":{{\"{song.Id}\":{{\"ArtistAlias\":\"歌手\"}}}}}}",
                $"{{\"Bindings\":{{\"{song.Id}\":{{\"Mid\":\"m\",\"ArtistAlias\":\" \"}}}}}}",
                $"{{\"Bindings\":{{\"{song.Id}\":{{\"Mid\":\"m\",\"Lyrics\":\"不允许\"}}}}}}"
            })
            {
                Check("拒绝坏配置 " + json, () =>
                {
                    File.WriteAllText(file, json);
                    try { ProbeSettings.Load(file); }
                    catch (InvalidDataException) { return; }
                    throw new Exception();
                });
            }
            Check("配置读取大小上限", () =>
            {
                File.WriteAllBytes(file, new byte[ProbeSettings.MaxFileBytes + 1]);
                try { ProbeSettings.Load(file); }
                catch (InvalidDataException) { return; }
                throw new Exception();
            });
            Check("配置保存大小上限", () =>
            {
                byte[] before = File.ReadAllBytes(file);
                var settings = new ProbeSettings();
                settings.SetVerifiedBinding(song, new string('m', ProbeSettings.MaxFileBytes));
                try { settings.Save(file); }
                catch (InvalidDataException)
                {
                    if (before.SequenceEqual(File.ReadAllBytes(file))) return;
                }
                throw new Exception();
            });
            Check("不创建不存在的父目录", () =>
            {
                string missing = Path.Combine(directory, "missing", "settings.json");
                try { new ProbeSettings().Save(missing); }
                catch (DirectoryNotFoundException)
                {
                    if (!Directory.Exists(Path.GetDirectoryName(missing))) return;
                }
                throw new Exception();
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine($"Probe 离线参数/配置自测：{passed} 通过，{failed} 失败；未访问 GSMTC、HTTP 或设备。");
        return failed == 0 ? 0 : 1;
    }

    private sealed class MemoryStartupStore : ILoginStartupStore
    {
        internal object? Value;
        internal string? Failure;
        internal bool IgnoreWrite, FailReadAfterWrite;
        internal int Reads, Writes, Deletes;
        public object? Read()
        {
            Reads++;
            if (Failure == "read" || (FailReadAfterWrite && Writes > 0)) throw new UnauthorizedAccessException();
            return Value;
        }
        public void Write(string command)
        {
            if (Failure == "write") throw new UnauthorizedAccessException();
            Writes++;
            if (!IgnoreWrite) Value = command;
        }
        public void Delete()
        {
            if (Failure == "delete") throw new UnauthorizedAccessException();
            Deletes++;
            Value = null;
        }
    }

    private sealed class OfflineHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> respond)
        : System.Net.Http.HttpMessageHandler
    {
        internal int Count { get; private set; }
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            Count++;
            return Task.FromResult(respond(request));
        }
    }
}
