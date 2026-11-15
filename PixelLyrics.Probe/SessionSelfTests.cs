namespace PixelLyrics.Probe;

internal static class SessionSelfTests
{
    internal static void Register(Action<string, Action> check)
    {
        static void Require(bool value) { if (!value) throw new InvalidOperationException(); }
        static (DesktopSession Session, TrackVersion Track) Ready()
        {
            var session = new DesktopSession();
            var track = new TrackVersion(session.Latest.RunId, 1, "song", 2);
            session.Update(s => s with { Track = track, Settled = true });
            return (session, track);
        }
        check("会话：旧 run 迟到结果及锁止不影响新运行", () =>
        {
            var (a, track) = Ready();
            var old = a.BeginAttempt(track);
            var (b, _) = Ready();
            b.BlockNetwork(track.RunId);
            Require(!b.CanApply(old) && !b.Latest.NetworkBlocked);
            a.End();
            Require(!a.CanApply(old));
        });
        check("会话：曲目 generation/revision 变化使排队命令过期", () =>
        {
            foreach (bool revision in new[] { false, true })
            {
                var (s, t) = Ready();
                Require(s.QueueRecheck(t, TimeSpan.Zero));
                var next = revision ? t with { Revision = 3 } : t with { Generation = 2 };
                s.Update(x => x with { Track = next });
                Require(!s.TakeRecheck(next, TimeSpan.Zero));
            }
        });
        check("会话：attempt 隔离，旧完成不能覆盖新请求", () =>
        {
            var (s, t) = Ready();
            var first = s.BeginAttempt(t);
            s.Complete(first, "完成", TimeSpan.Zero);
            var second = s.BeginAttempt(t);
            s.Complete(first, "旧完成", TimeSpan.Zero);
            Require(!s.CanApply(first) && s.CanApply(second) && s.Latest.Busy);
        });
        check("会话：连点单槽、忙碌、三秒冷却互锁", () =>
        {
            var (s, t) = Ready();
            Require(s.QueueRecheck(t, TimeSpan.Zero));
            Require(!s.QueueRecheck(t, TimeSpan.Zero));
            Require(s.TakeRecheck(t, TimeSpan.Zero));
            var a = s.BeginAttempt(t);
            Require(!s.QueueRecheck(t, TimeSpan.Zero));
            s.Complete(a, "完成", TimeSpan.Zero);
            Require(!s.QueueRecheck(t, TimeSpan.FromSeconds(2)));
            Require(s.QueueRecheck(t, TimeSpan.FromSeconds(3)));
        });
        check("会话：旧曲目迟到权限错误锁止整个 run 且拒绝旧歌词", () =>
        {
            var (s, t) = Ready();
            var a = s.BeginAttempt(t);
            var next = t with { Generation = 2, Revision = 4 };
            s.Update(x => x with { Track = next, Busy = false });
            s.BlockNetwork(t.RunId);
            s.Complete(a, "旧成功", TimeSpan.Zero);
            Require(s.Latest.NetworkBlocked && !s.CanApply(a));
            Require(!s.QueueRecheck(next, TimeSpan.FromSeconds(30)) && s.BeginAttempt(next) is null);
        });
        foreach (string status in new[] { "401", "403" })
            check("会话：权限 " + status + " 锁止不能由手动核对清除", () =>
            {
                var (s, t) = Ready();
                Require(s.QueueRecheck(t, TimeSpan.Zero));
                s.BlockNetwork(t.RunId);
                Require(!s.TakeRecheck(t, TimeSpan.Zero));
                s.Update(x => x with { Track = t with { Generation = 3 }, Busy = false });
                Require(!s.CanRecheck(TimeSpan.FromDays(1)) && s.Latest.NetworkBlocked);
            });
        check("会话：核对不重播开场且不移除 MID，结束查询当前进度", () =>
        {
            var (s, t) = Ready();
            var intro = new TrackIntro(true, 3000);
            intro.Start(t.Generation, "标题", TimeSpan.Zero, "歌手");
            var settings = new ProbeSettings();
            settings.Bindings.Add(new string('a', 64), new("existing-mid"));
            Require(s.QueueRecheck(t, TimeSpan.FromSeconds(5)) && s.TakeRecheck(t, TimeSpan.FromSeconds(5)));
            var a = s.BeginAttempt(t);
            s.Complete(a, "已核对", TimeSpan.FromSeconds(6));
            var document = PixelLyrics.Core.LrcParser.Parse("[00:01.00]首句\n[00:30.00]当前句").Document;
            Require(!intro.IsShowing(t.Generation, TimeSpan.FromSeconds(6))
                && settings.Bindings.Values.Single().Mid == "existing-mid"
                && document.GetLineAt(TimeSpan.FromSeconds(31))?.Text == "当前句");
        });
        check("会话：停止撤销命令并拒绝应用及新请求", () =>
        {
            var (s, t) = Ready();
            Require(s.QueueRecheck(t, TimeSpan.Zero));
            s.Stop();
            Require(!s.TakeRecheck(t, TimeSpan.Zero) && s.BeginAttempt(t) is null);
            s.End();
            Require(!s.CanRecheck(TimeSpan.FromDays(1)));
        });
        check("会话：不稳定或缺少会话不可核对", () =>
        {
            var (s, t) = Ready();
            s.Update(x => x with { Settled = false });
            Require(!s.QueueRecheck(t, TimeSpan.Zero));
            s.Update(x => x with { Track = null, Settled = true });
            Require(!s.QueueRecheck(t, TimeSpan.Zero));
        });
        check("参数：保存捕获值不等于保存期间编辑的新 Draft", () =>
        {
            var settings = new ProbeSettings { OffsetMs = 100 };
            var captured = DesktopParameters.From(settings);
            settings.OffsetMs = 200;
            var draft = DesktopParameters.From(settings);
            var saved = captured;
            Require(draft != saved && draft.Differences(saved) == "偏移" && saved.OffsetMs == 100);
        });
        check("参数：绑定不参与比较且 Active 使用合成参数", () =>
        {
            var settings = new ProbeSettings();
            var saved = DesktopParameters.From(settings);
            settings.Bindings.Add(new string('a', 64), new("mid"));
            Require(saved == DesktopParameters.From(settings));
            settings.OffsetMs = -300;
            settings.DisplayWidthUnits = 40;
            settings.ApplyRuntimeOverrides(new() { PollIntervalMs = 400 });
            var active = DesktopParameters.From(settings);
            Require(active.OffsetMs == -300 && active.Width == 40 && active.Poll == 400);
            Require(active.Differences(saved) == "偏移、宽度、轮询");
        });
    }
}
