using System.Net;
using System.Net.Http;
using System.IO;
using PixelLyrics.OnlineProbe;

namespace PixelLyrics.Probe;

internal static class RecoverySelfTests
{
    internal static void Register(Action<string, Action> check)
    {
        static void Require(bool value) { if (!value) throw new Exception(); }
        foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.RequestTimeout })
            check("恢复：临时HTTP失败重试后成功 " + status, () =>
            {
                var waits = new List<TimeSpan>();
                var recovery = new RequestRecovery(TimeProvider.System, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
                int calls = 0;
                using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(++calls < 3 ? status : HttpStatusCode.OK) { Content = new StringContent("ok") })));
                Require(recovery.ExecuteAsync(http, Request, text => text, RequestStage.Lyrics, 2000, default).GetAwaiter().GetResult() == "ok");
                Require(calls == 3 && waits.SequenceEqual(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) }));
            });
        check("恢复：格式异常只重取一次且下一首可继续", () =>
        {
            int calls = 0;
            var recovery = Fast();
            using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") }); }));
            try { recovery.ExecuteAsync<string>(http, Request, _ => throw new ProbeException("private response"), RequestStage.Lyrics, 2000, default).GetAwaiter().GetResult(); throw new Exception(); }
            catch (LyricsRequestException error) { Require(error.Kind == RequestFailureKind.Protocol && !error.Message.Contains("private") && calls == 2); }
            recovery.ExecuteAsync(http, Request, text => text, RequestStage.Lyrics, 2000, default).GetAwaiter().GetResult();
            Require(calls == 3);
        });
        check("恢复：限流退避遵守来源范围与服务器时间", () =>
        {
            var clock = new Clock();
            var recovery = new RequestRecovery(clock, (_, _) => Task.CompletedTask);
            int calls = 0;
            using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)); }));
            for (int i = 0; i < 6; i++)
            {
                try { recovery.ExecuteAsync(http, Request, text => text, RequestStage.Lyrics, 2000, default).GetAwaiter().GetResult(); } catch (LyricsRequestException) { }
                Require(recovery.Remaining.TotalSeconds == new[] { 60, 120, 240, 480, 900, 900 }[i]);
                try { recovery.ExecuteAsync(http, Request, text => text, RequestStage.Search, 2000, default).GetAwaiter().GetResult(); } catch (LyricsRequestException) { }
                Require(calls == i + 1);
                clock.Advance(recovery.Remaining);
            }
            Require(Fast().Remaining == TimeSpan.Zero);
            using var server = new HttpClient(new Handler((_, _) => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new(TimeSpan.FromHours(1)); return Task.FromResult(response); }));
            try { recovery.ExecuteAsync(server, Request, text => text, RequestStage.Lyrics, 2000, default).GetAwaiter().GetResult(); } catch (LyricsRequestException) { }
            Require(recovery.Remaining == TimeSpan.FromHours(1));
        });
        check("恢复：限流到期只恢复失败阶段并支持日期格式", () =>
        {
            var clock = new Clock();
            var recovery = new RequestRecovery(clock, (wait, token) => { token.ThrowIfCancellationRequested(); clock.Advance(wait); return Task.CompletedTask; }, true);
            int details = 0, lyrics = 0;
            using var http = new HttpClient(new Handler((request, _) =>
            {
                bool detail = request.RequestUri!.AbsolutePath == "/details";
                if (detail) details++; else lyrics++;
                var response = new HttpResponseMessage(!detail && lyrics == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK) { Content = new StringContent("ok") };
                if (response.StatusCode == HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new(clock.GetUtcNow().AddSeconds(3));
                return Task.FromResult(response);
            }));
            recovery.ExecuteAsync(http, () => new(HttpMethod.Get, "https://example.invalid/details"), text => text, RequestStage.Details, 2000, default).GetAwaiter().GetResult();
            Require(recovery.ExecuteAsync(http, Request, text => text, RequestStage.Lyrics, 2000, default).GetAwaiter().GetResult() == "ok");
            Require(details == 1 && lyrics == 2 && clock.GetTimestamp() == TimeSpan.FromSeconds(3).Ticks);
        });
        check("恢复：超时最多三次且身份不符不重试", () =>
        {
            int calls = 0;
            var recovery = Fast();
            using var timeout = new HttpClient(new Handler((_, _) => { calls++; throw new TaskCanceledException(); }));
            try { recovery.ExecuteAsync(timeout, Request, text => text, RequestStage.Search, 2000, default).GetAwaiter().GetResult(); throw new Exception(); }
            catch (LyricsRequestException error) { Require(error.Kind == RequestFailureKind.Transient && calls == 3); }
            calls = 0;
            using var identity = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }); }));
            try { recovery.ExecuteAsync<string>(identity, Request, _ => throw new LyricsRequestException(RequestFailureKind.Identity, RequestStage.Details, "ID mismatch"), RequestStage.Details, 2000, default).GetAwaiter().GetResult(); throw new Exception(); }
            catch (LyricsRequestException error) { Require(error.Kind == RequestFailureKind.Identity && calls == 1); }
        });
        check("恢复：访问限制手动恢复且不影响其他来源", () =>
        {
            var recovery = Fast(); int calls = 0;
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(++calls == 1 ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = new StringContent("ok") })));
            for (int i = 0; i < 2; i++) try { recovery.ExecuteAsync(http, Request, text => text, RequestStage.Lyrics, 2000, default).GetAwaiter().GetResult(); } catch (LyricsRequestException) { }
            Require(calls == 1 && recovery.AccessDenied && !Fast().AccessDenied);
            recovery.RetryAccess();
            Require(recovery.ExecuteAsync(http, Request, text => text, RequestStage.Lyrics, 2000, default).GetAwaiter().GetResult() == "ok");
        });
        check("恢复：重试等待可取消且旧结果不能设置访问限制", () =>
        {
            using var cancel = new CancellationTokenSource(); int calls = 0;
            var recovery = new RequestRecovery(TimeProvider.System, (_, token) => { cancel.Cancel(); return Task.FromCanceled(token); });
            using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }));
            try { recovery.ExecuteAsync(http, Request, text => text, RequestStage.Lyrics, 2000, cancel.Token).GetAwaiter().GetResult(); throw new Exception(); } catch (OperationCanceledException) { Require(calls == 1); }
            bool current = true;
            using var old = new HttpClient(new Handler((_, _) => { current = false; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); }));
            recovery = Fast();
            try { recovery.ExecuteAsync(old, Request, text => text, RequestStage.Lyrics, 2000, default, _ => current ? Task.CompletedTask : throw new OperationCanceledException()).GetAwaiter().GetResult(); throw new Exception(); } catch (OperationCanceledException) { Require(!recovery.AccessDenied); }
        });
        check("恢复：限流等待禁用人工核对，访问限制允许手动恢复", () =>
        {
            var session = new DesktopSession();
            session.Update(s => s with { Track = new(s.RunId, 1, "song", 1), Settled = true, RecoveryWaiting = true });
            Require(!session.CanRecheck(TimeSpan.Zero));
            session.Update(s => s with { RecoveryWaiting = false, RecoveryManual = true });
            Require(session.QueueRecheck(session.Latest.Track, TimeSpan.Zero));
            Require(session.TakeRecheck(session.Latest.Track, TimeSpan.Zero));
        });
        check("恢复：安全日志轮转且清理控制字符", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            try
            {
                Directory.CreateDirectory(directory);
                for (int i = 0; i < 4; i++) { File.WriteAllBytes(Path.Combine(directory, "safety.log"), new byte[SafetyLog.MaximumBytes]); SafetyLog.Append("safe\nentry", directory); }
                Require(Directory.GetFiles(directory).Length == 3);
                Require(File.ReadAllText(Path.Combine(directory, "safety.log")).Contains("safe entry"));
            }
            finally { Directory.Delete(directory, true); }
        });
    }
    private static RequestRecovery Fast() => new(TimeProvider.System, (_, _) => Task.CompletedTask);
    private static HttpRequestMessage Request() => new(HttpMethod.Get, "https://example.invalid/lyrics");
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        internal void Advance(TimeSpan value) => ticks += value.Ticks;
    }
}
