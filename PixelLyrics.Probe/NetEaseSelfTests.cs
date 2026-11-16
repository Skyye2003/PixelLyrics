using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.IO;
using PixelLyrics.Core;
using PixelLyrics.OnlineProbe;

namespace PixelLyrics.Probe;

internal static class NetEaseSelfTests
{
    internal static void Register(Action<string, Action> check)
    {
        static void Require(bool value) { if (!value) throw new Exception(); }
        check("网易云：播放器与ID隔离", () =>
        {
            Require(ProbeOptions.Parse(["--player", "netease"]).Player == MusicPlayer.NetEaseMusic);
            Require(ProbeOptions.Parse([]).Player == MusicPlayer.QQMusic);
            Require(MusicPlayer.NetEaseMusic.Matches("CloudMusic.exe") && !MusicPlayer.NetEaseMusic.Matches("QQMusic.exe"));
            Require(MusicPlayers.NetEaseId(["NCM-1", "NCM-2"]) is null);
            Require(MusicPlayers.NetEaseId(["NCM-454549729"]) == "454549729");
        });
        check("网易云：详情毫秒换算与ID核验", () =>
        {
            string json = JsonSerializer.Serialize(new { code = 200, songs = new[] { new { id = 123, name = "Song", artists = new[] { new { name = "Artist" } }, album = new { name = "Album" }, duration = 237113 } } });
            using var doc = JsonDocument.Parse(json);
            Require(NetEaseLyricsClient.ParseDetails(doc.RootElement, "123").Interval == 237.113);
            try { NetEaseLyricsClient.ParseDetails(doc.RootElement, "456"); } catch (ProbeException) { return; }
            throw new Exception();
        });
        check("网易云：普通LRC解析", () =>
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = "[00:01.00]test\n[00:02.00]line" } }));
            Require(NetEaseLyricsClient.ParseLyrics(doc.RootElement).Document.Lines.Count == 2);
        });
        check("网易云：已核实零秒标签兼容且保留原文", () =>
        {
            // Real API timestamp structure for 2024659883; lyric text replaced with synthetic text.
            string text = "[00:00.00-1]credit\r\n[02:39.000]a\r\n[02:40.000]b\r\n[02:41.000]c\r\n[02:42.000]d";
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = text } }));
            var result = NetEaseLyricsClient.ParseLyrics(doc.RootElement);
            Require(result.Text == text && result.Document.Lines.Count == 5);
            Require(result.Document.Lines[0].Timestamp == TimeSpan.Zero && result.Document.Lines[0].Text == "credit");
            Require(result.Document.Lines.Skip(1).Select(l => l.Timestamp.TotalSeconds).SequenceEqual(new double[] { 159, 160, 161, 162 }));
            Require(LrcParser.Parse(text).Errors.Count != 0);
        });
        foreach (string tag in new[] { "[00:00.00-2]", "[00:12.00-1]", "[00:00.00-1x]" })
            check("网易云：未知后缀仍拒绝 " + tag, () =>
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = tag + "test\n[00:02.00]line" } }));
                try { NetEaseLyricsClient.ParseLyrics(doc.RootElement); } catch (ProbeException) { return; }
                throw new Exception();
            });
        check("网易云：异常时间标签拒绝且错误在完成后保留", () =>
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = "[00:00.00-2]test\n[00:02.00]line" } }));
            bool rejected = false;
            try { NetEaseLyricsClient.ParseLyrics(doc.RootElement); } catch (ProbeException) { rejected = true; }
            Require(rejected);
            var session = new DesktopSession();
            var track = new TrackVersion(session.Latest.RunId, 1, "song", 1);
            session.Update(s => s with { Track = track });
            var attempt = session.BeginAttempt(track);
            string reason = FollowRunner.SafeFailureReason(new("网易云 LRC 无效。"));
            session.BlockNetwork(session.Latest.RunId, reason);
            session.Complete(attempt, "ignored", TimeSpan.Zero);
            Require(session.Latest.Check.Contains(reason) && session.Latest.NetworkBlocked);
        });
        check("网易云：解析诊断保留行号及数字标签，不泄露歌词", () =>
        {
            foreach (string bad in new[] { "[00:12.00-2]private lyric", "[private lyric]private lyric", "private lyric" })
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = "[00:01.00]ok\n" + bad } }));
                try { NetEaseLyricsClient.ParseLyrics(doc.RootElement); }
                catch (NetEaseLrcException ex)
                {
                    string reason = FollowRunner.SafeFailureReason(ex);
                    Require(reason.Contains("第 2 行") && !reason.Contains("private") && !reason.Contains("ok"));
                    if (bad.StartsWith("[00:")) Require(reason.Contains("[00:12.00-2]"));
                    continue;
                }
                throw new Exception();
            }
        });
        check("网易云：真实括号正文不作为标签解析", () =>
        {
            // Public song 2677396501, line 8; keep the observed bracketed annotation.
            string text = "[00:00.48][2022.8. 开学典礼前]\n[00:01.00][00:02.00]test";
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = text } }));
            var result = NetEaseLyricsClient.ParseLyrics(doc.RootElement);
            Require(result.Document.Lines.Count == 3);
            Require(result.Document.Lines[0].Timestamp == TimeSpan.FromMilliseconds(480));
            Require(result.Document.Lines[0].Text == "[2022.8. 开学典礼前]");
            Require(LrcParser.Parse("[00:01.00][00:12.00-2]test").Errors.Count == 1);
        });
        check("网易云：无时间轴署名文本不锁止客户端", () =>
        {
            var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = "作曲 : Example\n\n编曲 Arranger: Example\n乐队 Orchestra: Example\n" } })) }));
            using var client = new NetEaseLyricsClient(handler);
            for (int i = 0; i < 2; i++)
            {
                try { client.FetchAsync("1913532412").GetAwaiter().GetResult(); }
                catch (UntimedLyricsException) { continue; }
                throw new Exception();
            }
            Require(handler.Calls == 2);
        });
        check("网易云：无标签正文混入定时歌词仍报错", () =>
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = "credit\n[00:01.00]test" } }));
            try { NetEaseLyricsClient.ParseLyrics(doc.RootElement); }
            catch (NetEaseLrcException ex) { Require(ex.Diagnostic.Contains("第 1 行")); return; }
            throw new Exception();
        });
        check("网易云：明确无歌词", () =>
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { code = 200, nolyric = true }));
            try { NetEaseLyricsClient.ParseLyrics(doc.RootElement); } catch (NoLyricsException) { return; }
            throw new Exception();
        });
        foreach (string body in new[] { "<html>login</html>", JsonSerializer.Serialize(new { code = 301 }), JsonSerializer.Serialize(new { code = 429 }), JsonSerializer.Serialize(new { code = 200 }) })
            check("网易云：异常响应锁止 " + body, () =>
            {
                var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
                using var client = new NetEaseLyricsClient(handler);
                for (int i = 0; i < 2; i++)
                {
                    bool rejected = false;
                    try { client.FetchAsync("123").GetAwaiter().GetResult(); } catch (ProbeException) { rejected = true; }
                    Require(rejected);
                }
                Require(handler.Calls == 1);
            });
        check("网易云：HTTP限流后禁止追加请求", () =>
        {
            var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
            using var client = new NetEaseLyricsClient(handler);
            for (int i = 0; i < 2; i++)
            {
                bool rejected = false;
                try { client.FetchAsync("123").GetAwaiter().GetResult(); } catch (ProbeException) { rejected = true; }
                Require(rejected);
            }
            Require(handler.Calls == 1);
        });
        check("网易云：请求超时", () =>
        {
            var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK); });
            using var client = new NetEaseLyricsClient(handler, 2000);
            try { client.FetchAsync("123").GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { Require(handler.Calls == 1); return; }
            throw new Exception();
        });
        check("网易云：搜索候选歧义不取词", () =>
        {
            var handler = new Handler((request, _) =>
            {
                bool search = request.RequestUri!.AbsolutePath.Contains("search");
                string id = request.RequestUri.Query.Contains("id=123&") ? "123" : "456";
                string body = search
                    ? JsonSerializer.Serialize(new { code = 200, result = new { songs = new[] { new { id = 123 }, new { id = 456 } } } })
                    : JsonSerializer.Serialize(new { code = 200, songs = new[] { new { id = int.Parse(id), name = "Song", artists = new[] { new { name = "Artist" } }, album = new { name = "Album" }, duration = 237113 } } });
                Require(!request.RequestUri.AbsolutePath.Contains("lyric"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            });
            using var client = new NetEaseLyricsClient(handler);
            var result = client.RecallAsync("Song", "Artist", "Album", 237.113).GetAwaiter().GetResult();
            Require(result.Winner is null);
        });
        check("网易云：取消传播", () =>
        {
            var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK); });
            using var client = new NetEaseLyricsClient(handler);
            using var cancel = new CancellationTokenSource(30);
            try { client.FetchAsync("123", cancel.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { Require(handler.Calls == 1); return; }
            throw new Exception();
        });
        check("网易云：旧配置默认QQ，绑定分离保存重载", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "netease-settings-" + Guid.NewGuid() + ".json");
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new { OffsetMs = 100 }));
                var settings = ProbeSettings.Load(path);
                Require(settings.Player == MusicPlayer.QQMusic);
                var song = new SongIdentity("Song", "Artist", "Album", TimeSpan.FromMilliseconds(237113));
                settings.SetVerifiedBinding(song, "QQmid");
                settings.NetEaseBindings[song.Id] = "123";
                settings.Player = MusicPlayer.NetEaseMusic;
                settings.Save(path);
                var restored = ProbeSettings.Load(path);
                Require(restored.Player == MusicPlayer.NetEaseMusic && restored.Bindings[song.Id].Mid == "QQmid"
                    && restored.NetEaseBindings[song.Id] == "123");
                Require(DesktopParameters.From(restored).Differences(DesktopParameters.From(restored) with { Player = MusicPlayer.QQMusic }) == "播放器");
            }
            finally { File.Delete(path); }
        });
        foreach (bool conflict in new[] { false, true })
            check("网易云：详情核验后才取词，冲突拒绝 " + conflict, () =>
            {
                string details = JsonSerializer.Serialize(new { code = 200, songs = new[] { new { id = 123, name = "Song", artists = new[] { new { name = "Artist" } }, album = new { name = "Album" }, duration = 237113 } } });
                string lyrics = JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = "[00:01.00]test" } });
                var handler = new Handler((r, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(r.RequestUri!.AbsolutePath.Contains("detail") ? details : lyrics) }));
                using var client = new NetEaseLyricsClient(handler);
                var song = new SongIdentity(conflict ? "Other" : "Song", "Artist", "Album", TimeSpan.FromMilliseconds(237113));
                try
                {
                    var loaded = FollowRunner.LoadAsync(song, null, CancellationToken.None, testClient: client,
                        player: MusicPlayer.NetEaseMusic, sourceSongId: "123").GetAwaiter().GetResult();
                    Require(!conflict && loaded.Mid == "123" && handler.Calls == 2);
                }
                catch (FollowRunner.MatchUnavailableException) { Require(conflict && handler.Calls == 1); }
            });
        check("网易云：曲目变化阻止后续请求", () =>
        {
            string details = JsonSerializer.Serialize(new { code = 200, songs = new[] { new { id = 123, name = "Song", artists = new[] { new { name = "Artist" } }, album = new { name = "Album" }, duration = 237113 } } });
            var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(details) }));
            using var client = new NetEaseLyricsClient(handler);
            int checks = 0;
            Task Guard() => ++checks == 1 ? Task.CompletedTask : throw new OperationCanceledException();
            try
            {
                FollowRunner.LoadAsync(new("Song", "Artist", "Album", TimeSpan.FromMilliseconds(237113)), null,
                    CancellationToken.None, testClient: client, player: MusicPlayer.NetEaseMusic, sourceSongId: "123", beforeRequest: Guard).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { Require(handler.Calls == 1); return; }
            throw new Exception();
        });
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return send(request, token); }
    }
}
