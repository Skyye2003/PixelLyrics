using System.IO;

namespace PixelLyrics.Probe;

internal static class LifecycleSelfTests
{
    internal static void Register(Action<string, Action> check)
    {
        static void Require(bool value) { if (!value) throw new InvalidOperationException(); }
        check("设备：缺失和多设备拒绝绑定，唯一设备稳定一秒后恢复", () =>
        {
            var screen = ScreenOutput.Waiting((_, _) => throw new Exception("不应写入"));
            Require(!screen.ObserveDevices([], TimeSpan.Zero));
            Require(!screen.ObserveDevices(["a", "b"], TimeSpan.Zero));
            Require(!screen.ObserveDevices(["a"], TimeSpan.Zero));
            Require(!screen.ObserveDevices(["a"], TimeSpan.FromMilliseconds(999)));
            Require(screen.ObserveDevices(["a"], TimeSpan.FromSeconds(1)));
            Require(!screen.ObserveDevices(["b"], TimeSpan.FromSeconds(2)));
            Require(screen.ObserveDevices(["b"], TimeSpan.FromSeconds(3)));
        });
        check("设备：一次重绑定后仍写入失败，必须观察到拔除才能恢复", () =>
        {
            int writes = 0;
            var screen = ScreenOutput.Waiting((_, _) => { writes++; throw new IOException(); });
            screen.ObserveDevices(["a"], TimeSpan.Zero);
            Require(screen.ObserveDevices(["a"], TimeSpan.FromSeconds(1)));
            Require(!screen.ShowClock(1) && writes == 1);
            Require(!screen.ObserveDevices(["a"], TimeSpan.FromSeconds(2)));
            Require(screen.ObserveDevices(["a"], TimeSpan.FromSeconds(3)));
            Require(!screen.ShowClock(1) && writes == 2);
            Require(!screen.ObserveDevices(["a"], TimeSpan.FromSeconds(9)));
            Require(!screen.ShowClock(1) && writes == 2);
            screen.ObserveDevices([], TimeSpan.FromSeconds(10));
            screen.ObserveDevices(["a"], TimeSpan.FromSeconds(11));
            Require(screen.ObserveDevices(["a"], TimeSpan.FromSeconds(12)));
        });
        check("设备：恢复后同一句歌词必须重发，回钟只发送一个报告", () =>
        {
            var packets = new List<byte[]>();
            var screen = ScreenOutput.Waiting((_, packet) => packets.Add(packet));
            screen.ObserveDevices(["a"], TimeSpan.Zero);
            screen.ObserveDevices(["a"], TimeSpan.FromSeconds(1));
            screen.ShowLyric("test"); Require(packets.Count == 2);
            screen.ObserveDevices([], TimeSpan.FromSeconds(2));
            screen.ShowLyric("test"); Require(packets.Count == 2);
            screen.ObserveDevices(["a"], TimeSpan.FromSeconds(3));
            screen.ObserveDevices(["a"], TimeSpan.FromSeconds(4));
            screen.ShowLyric("test"); Require(packets.Count == 4);
            Require(screen.ShowClock(1) && packets.Count == 5 && screen.ClockRequested && !screen.HasDisplayableLyrics);
        });
        check("生命周期：暂停立即拒绝请求结果，停止后恢复不重新启动", () =>
        {
            var session = new DesktopSession();
            var track = new TrackVersion(session.Latest.RunId, 1, "song", 1);
            session.Update(s => s with { Track = track, Settled = true });
            var attempt = session.BeginAttempt(track); Require(session.CanApply(attempt));
            session.Lifecycle.Pause(); Require(!session.CanApply(attempt));
            Require(session.BeginAttempt(track) is null);
            session.Stop(); session.Lifecycle.Resume();
            Require(!session.CanApply(attempt) && session.Latest.Stopping);
        });
        check("生命周期：通知唤醒长轮询，关机确认不等待界面线程", () =>
        {
            var lifecycle = new RunLifecycle();
            var waiting = lifecycle.WaitAsync(30000, default);
            lifecycle.DeviceChanged(); Require(waiting.Wait(1000));
            var shutdown = Task.Run(lifecycle.EndSession);
            Require(SpinWait.SpinUntil(() => lifecycle.IsPaused, 1000));
            lifecycle.Acknowledge(); Require(shutdown.Wait(1000));
            int revision = lifecycle.Revision;
            lifecycle.Resume(); Require(!lifecycle.IsPaused && lifecycle.Revision > revision);
        });
        check("自动运行：仅首次加载或保存开启时启动，普通保存和重新加载不重启", () =>
        {
            Require(RunLifecycle.ShouldAutoStart(true, false, false, true, true));
            Require(RunLifecycle.ShouldAutoStart(false, true, false, true, true));
            Require(!RunLifecycle.ShouldAutoStart(false, true, true, true, true));
            Require(!RunLifecycle.ShouldAutoStart(false, false, false, true, true));
            Require(!RunLifecycle.ShouldAutoStart(false, true, true, false, true));
            Require(!RunLifecycle.ShouldAutoStart(true, false, false, true, false));
        });
        check("配置：旧配置默认关闭自动运行，新授权往返保存且保留歌曲绑定", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try
            {
                File.WriteAllText(path, "{}"); Require(!ProbeSettings.Load(path).AutoRun);
                var settings = ProbeSettings.Load(path); settings.AutoRun = true;
                settings.Bindings[new string('A', 64)] = new("mid"); settings.NetEaseBindings[new string('A', 64)] = "123";
                settings.Save(path);
                var loaded = ProbeSettings.Load(path);
                Require(loaded.AutoRun && loaded.Bindings[new string('A', 64)].Mid == "mid" && loaded.NetEaseBindings[new string('A', 64)] == "123");
                File.WriteAllText(path, "{\"AutoRun\":\"yes\"}");
                try { ProbeSettings.Load(path); throw new Exception(); } catch (InvalidDataException) { }
            }
            finally { File.Delete(path); }
        });
    }
}
