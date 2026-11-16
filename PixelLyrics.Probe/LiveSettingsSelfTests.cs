using System.IO;

namespace PixelLyrics.Probe;

internal static class LiveSettingsSelfTests
{
    internal static void Register(Action<string, Action> check)
    {
        static void Require(bool condition) { if (!condition) throw new InvalidOperationException(); }
        static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);
        check("自动来源：稳定一秒后绑定，暂停交接也需稳定", () =>
        {
            var selector = new PlayerSelector();
            Require(selector.Observe(PlayerMode.Auto, true, false, At(0)) is null);
            Require(selector.Observe(PlayerMode.Auto, true, false, At(.99)) is null);
            Require(selector.Observe(PlayerMode.Auto, true, false, At(1)) == MusicPlayer.QQMusic);
            Require(selector.Observe(PlayerMode.Auto, false, true, At(2)) == MusicPlayer.QQMusic);
            Require(selector.Observe(PlayerMode.Auto, false, true, At(3)) == MusicPlayer.NetEaseMusic);
        });
        check("自动来源：双播放不抢占，未绑定时要求选择", () =>
        {
            var selector = new PlayerSelector();
            Require(selector.Observe(PlayerMode.Auto, true, true, At(0)) is null && selector.Notice.Contains("请选择"));
            selector.Observe(PlayerMode.QQMusic, true, true, At(1));
            Require(selector.Observe(PlayerMode.Auto, true, true, At(2)) == MusicPlayer.QQMusic && selector.Notice.Contains("保持"));
            Require(selector.Observe(PlayerMode.Auto, false, false, At(3)) == MusicPlayer.QQMusic);
        });
        check("自动来源：短暂候选撤销，手选立即生效", () =>
        {
            var selector = new PlayerSelector();
            selector.Observe(PlayerMode.Auto, true, false, At(0));
            selector.Observe(PlayerMode.Auto, false, false, At(.9));
            Require(selector.Observe(PlayerMode.Auto, true, false, At(1)) is null);
            Require(selector.Observe(PlayerMode.NetEaseMusic, true, false, At(1)) == MusicPlayer.NetEaseMusic);
        });
        check("配置模式：新配置自动、旧配置保留来源、CLI默认QQ", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try
            {
                Require(ProbeSettings.Load(path).Mode == PlayerMode.Auto);
                File.WriteAllText(path, """{"Player":"NetEaseMusic"}""");
                Require(ProbeSettings.Load(path).Mode == PlayerMode.NetEaseMusic);
                var settings = ProbeSettings.Load(path); settings.Mode = PlayerMode.Auto; settings.Save(path);
                Require(ProbeSettings.Load(path).Mode == PlayerMode.Auto);
                Require(ProbeOptions.Parse([]).Mode == PlayerMode.QQMusic);
                Require(ProbeOptions.Parse(["--player", "auto"]).Mode == PlayerMode.Auto);
                File.WriteAllText(path, """{"Mode":"99"}""");
                try { ProbeSettings.Load(path); throw new Exception(); } catch (InvalidDataException) { }
            }
            finally { File.Delete(path); }
        });
        check("实时设置：只在循环应用，版本递增，捕获草稿不被后续编辑改变", () =>
        {
            var session = new DesktopSession(); var settings = new ProbeSettings();
            var captured = DesktopParameters.From(settings) with { OffsetMs = -400, Width = 48, Hold = 900,
                Mode = PlayerMode.NetEaseMusic, Player = MusicPlayer.NetEaseMusic, ClockStyle = 8, IdleClock = false, IdleDelay = 2000,
                Intro = true, IntroHold = 5000, NoLyricsHold = 9000, Poll = 1000, Settle = 1200, Timeout = 3000 };
            var pending = session.QueueParameters(captured);
            Require(!pending.IsCompleted && settings.OffsetMs == 0);
            var draft = captured with { OffsetMs = 200 };
            Require(session.ApplyParameters(p => p.ApplyTo(settings)) && pending.Result);
            Require(session.Latest.ParameterVersion == 1 && session.Latest.Active == captured && draft != captured);
            Require(DesktopParameters.From(settings) == captured);
            var next = session.QueueParameters(draft); session.ApplyParameters(p => p.ApplyTo(settings));
            Require(next.Result && session.Latest.ParameterVersion == 2);
        });
        check("实时设置：重复提交拒绝，停止优先且不会重启", () =>
        {
            var session = new DesktopSession(); var values = DesktopParameters.From(new());
            var pending = session.QueueParameters(values);
            bool rejected = false;
            try { session.QueueParameters(values); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected); session.Stop();
            Require(!pending.Result && !session.ApplyParameters(_ => throw new Exception()));
            session.End(); Require(!session.QueueParameters(values).Result && !session.Latest.Running);
        });
        check("实时设置：应用异常有确认失败，联网锁止保持", () =>
        {
            var session = new DesktopSession(); session.BlockNetwork(session.Latest.RunId, "测试锁止");
            var pending = session.QueueParameters(DesktopParameters.From(new()));
            try { session.ApplyParameters(_ => throw new IOException()); } catch (IOException) { }
            Require(pending.IsFaulted && session.Latest.Active is null && session.Latest.NetworkBlocked);
            _ = pending.Exception;
            var next = session.QueueParameters(DesktopParameters.From(new())); session.ApplyParameters(_ => { });
            Require(next.Result && session.Latest.NetworkBlocked && session.Latest.Check.Contains("测试锁止"));
        });
        check("参数比较：自动模式不受旧播放器字段影响", () =>
        {
            var old = new ProbeSettings { Mode = PlayerMode.Auto, Player = MusicPlayer.NetEaseMusic };
            Require(DesktopParameters.From(old) == DesktopParameters.From(new()));
        });
        check("来源切换：同曲目媒体版本变化也拒绝旧请求", () =>
        {
            var session = new DesktopSession(); var run = session.Latest.RunId;
            var old = new TrackVersion(run, 1, "same-song", 1);
            session.Update(s => s with { Track = old });
            var attempt = session.BeginAttempt(old); Require(session.CanApply(attempt));
            session.Update(s => s with { Track = new(run, 2, "same-song", 2), Source = MusicPlayer.NetEaseMusic, Busy = false });
            Require(!session.CanApply(attempt));
            session.Complete(attempt, "old result", At(1));
            Require(session.Latest.Check != "old result");
        });
        check("实时设置：保存合并两种来源最新绑定", () =>
        {
            var session = new DesktopSession(); var live = new ProbeSettings(); var disk = new ProbeSettings();
            string key = new('a', 64); string other = new('b', 64);
            live.Bindings[key] = new("latest"); live.NetEaseBindings[key] = "123";
            disk.Bindings[key] = new("old"); disk.Bindings[other] = new("keep");
            session.RememberBindings(live); session.MergeBindings(disk);
            Require(disk.Bindings[key].Mid == "latest" && disk.Bindings[other].Mid == "keep" && disk.NetEaseBindings[key] == "123");
        });
        check("实时设置：开场按原起点改期限，结束后不重播", () =>
        {
            var intro = new TrackIntro(true, 3000); intro.Start(1, "song", At(0));
            intro.Configure(true, 5000, At(2)); Require(intro.IsShowing(1, At(4)));
            Require(!intro.IsShowing(1, At(5))); intro.Configure(true, 10000, At(6));
            Require(!intro.IsShowing(1, At(6)));
            intro.Start(2, "song", At(7)); intro.Configure(false, 10000, At(8));
            intro.Configure(true, 10000, At(9)); Require(!intro.IsShowing(2, At(9)));
        });
        check("实时设置：启用开场不为当前曲目补播", () =>
        {
            var intro = new TrackIntro(false, 3000); intro.Start(1, "song", At(0));
            intro.Configure(true, 5000, At(1)); Require(!intro.IsShowing(1, At(1)));
            intro.Start(2, "next", At(2)); Require(intro.IsShowing(2, At(2)));
        });
        check("实时设置：空闲修改重新等待，禁用取消待触发", () =>
        {
            var idle = new IdleClock(true, 5000); idle.Observe(1, At(0), true);
            idle.Configure(true, 2000); Require(!idle.Observe(1, At(4), true));
            Require(!idle.Observe(1, At(5), true) && idle.Observe(1, At(6), true));
            idle.Configure(false, 1000); Require(!idle.Observe(1, At(20), true));
        });
        check("实时设置：稳定期限和无词期限保留原起点", () =>
        {
            var gate = new FollowGeneration(600); gate.Observe("song", 1, At(0)); gate.Configure(2000);
            Require(!gate.IsSettled(At(1)) && gate.IsSettled(At(2)) && gate.Generation == 1);
            var fallback = new NoLyricsFallback(); fallback.Start("song", At(0));
            Require(!fallback.ShouldSwitch(At(2), 5000) && fallback.ShouldSwitch(At(2), 1000));
            fallback.MarkClock(); Require(!fallback.ShouldSwitch(At(3), 9000));
        });
    }
}
