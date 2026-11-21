using System.Net;
using System.Text;
using System.Text.Json;

namespace PixelLyrics.OnlineProbe;

internal static class SelfTests
{
    internal static int Run()
    {
        int passed = 0, failed = 0;
        void Test(string name, Action check)
        {
            try { check(); passed++; Console.WriteLine("通过：" + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("失败：" + name + " " + ex.Message); }
        }
        static void Assert(bool value) { if (!value) throw new Exception("断言不成立。"); }
        static void Reject(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is ProbeException or CandidateMetadataException or NoLyricsException or JsonException or DecoderFallbackException) { return; }
            throw new Exception("应当拒绝但未拒绝。");
        }
        static string Response(string lyric) => JsonSerializer.Serialize(new { code = 0, req_1 = new { code = 0, data = new { lyric } } });
        const string search = """{"code":0,"data":{"song":{"itemlist":[{"mid":"m1","name":"Ａ  Song","singer":"Singer"},{"mid":"m2","name":"A Song","singer":"Other"},{"mid":"m3","name":"A Song"}]}}}""";
        const string lrc = "[ti:标题]\n[ar:歌手]\n[00:01.00]第一句\n[00:02.00]第二句";
        Test("NFKC、Trim、空白折叠与大小写", () => Assert(Protocol.Normalize("  Ａ\t song\n") == "A SONG"));
        Test("严格歌名歌手匹配", () => Assert(Protocol.Search(search, "a song", " singer ").Single().Mid == "m1"));
        Test("不接受歌名模糊匹配", () => Assert(Protocol.Search(search, "song", "Singer").Count == 0));
        Test("不接受歌手模糊匹配", () => Assert(Protocol.Search(search, "A Song", "Sing").Count == 0));
        Test("缺失歌手拒绝", () => Assert(Protocol.Search("""{"code":0,"data":{"song":{"itemlist":[{"mid":"m","name":"a"}]}}}""", "a", "s").Count == 0));
        Test("多个匹配保留不自动选", () => Assert(Protocol.Search(search.Replace("Other", "Singer"), "A Song", "Singer").Count == 2));
        Test("未命中 MID 不在匹配结果", () => Assert(!Protocol.Search(search, "A Song", "Singer").Any(s => s.Mid == "m2")));
        Test("搜索顶层错误码", () => Reject(() => Protocol.Search(search.Replace("\"code\":0", "\"code\":1"), "a", "b")));
        Test("搜索嵌套错误码", () => Reject(() => Protocol.Search(search.Replace("\"song\":{", "\"song\":{\"code\":9,"), "a", "b")));
        Test("缺失业务码", () => Reject(() => Protocol.Search("{}", "a", "b")));
        Test("字符串业务码拒绝", () => Reject(() => Protocol.Search(search.Replace("\"code\":0", "\"code\":\"0\""), "a", "b")));
        Test("明文 LRC 与行数", () => Assert(Protocol.Lyrics(Response(lrc)).Document.Lines.Count == 2));
        Test("报告 LRC 元数据", () => Assert(Protocol.Lyrics(Response(lrc)).Metadata["ti"] == "标题"));
        Test("普通 Base64 有效", () => Assert(Protocol.Lyrics(Response(Convert.ToBase64String(Encoding.UTF8.GetBytes(lrc)))).Text == lrc));
        Test("Base64 无效拒绝", () => Reject(() => Protocol.Lyrics(Response("%%%not-base64"))));
        Test("Base64 非 UTF8 拒绝", () => Reject(() => Protocol.Lyrics(Response(Convert.ToBase64String([0xff, 0xfe, 0x80])))));
        Test("HTTP 响应非 UTF8 拒绝", () => Reject(() => Protocol.Utf8([0xff])));
        Test("无词拒绝", () => Reject(() => Protocol.Lyrics(Response(""))));
        Test("只有元数据拒绝", () => Reject(() => Protocol.Lyrics(Response("[ti:标题]"))));
        Test("缺少 lyric 拒绝", () => Reject(() => Protocol.Lyrics("""{"code":0,"req_1":{"code":0,"data":{}}}""")));
        Test("歌词顶层错误码", () => Reject(() => Protocol.Lyrics(Response(lrc).Replace("\"code\":0", "\"code\":3"))));
        Test("歌词 req_1 错误码", () => Reject(() => Protocol.Lyrics("""{"code":0,"req_1":{"code":4,"data":{"lyric":""}}}""")));
        Test("歌词 data 错误码", () => Reject(() => Protocol.Lyrics("""{"code":0,"req_1":{"code":0,"data":{"code":7,"lyric":""}}}""")));
        Test("req_1 缺 code 拒绝", () => Reject(() => Protocol.Lyrics("""{"code":0,"req_1":{"data":{"lyric":""}}}""")));
        Test("非 LRC 明文拒绝", () => Reject(() => Protocol.Lyrics(Response("普通文字"))));
        Test("Base64 非 LRC 拒绝", () => Reject(() => Protocol.Lyrics(Response(Convert.ToBase64String(Encoding.UTF8.GetBytes("普通文字"))))));
        Test("损坏时间戳拒绝", () => Reject(() => Protocol.Lyrics(Response("[00:99]错误"))));
        Test("加密标志拒绝", () => Reject(() => Protocol.Lyrics(Response(lrc).Replace("\"lyric\":", "\"encrypted\":1,\"lyric\":"))));
        Test("crypt=1 拒绝", () => Reject(() => Protocol.Lyrics(Response(lrc).Replace("\"lyric\":", "\"crypt\":1,\"lyric\":"))));
        Test("crypt=0 qrc=0 普通LRC接受", () => Assert(Protocol.Lyrics(Response(lrc).Replace("\"lyric\":", "\"crypt\":0,\"qrc\":0,\"lyric\":")).Document.Lines.Count == 2));
        Test("候选保留不同歌手供人工核对", () => Assert(Protocol.Candidates(search).Count == 3));
        Test("QRC 数据拒绝", () => Reject(() => Protocol.Lyrics(Response(lrc).Replace("\"lyric\":", "\"qrc\":\"aabbcc\",\"lyric\":"))));
        Test("HTTP 200 接受", () => Protocol.CheckHttp(HttpStatusCode.OK));
        Test("HTTP 302 拒绝", () => Reject(() => Protocol.CheckHttp(HttpStatusCode.Redirect)));
        Test("HTTP 403 拒绝", () => Reject(() => Protocol.CheckHttp(HttpStatusCode.Forbidden)));
        Test("缺失歌手参数拒绝", () => Reject(() => Options.Parse(["--allow-online", "--title", "a"])));
        Test("缺失授权参数拒绝", () => Reject(() => Options.Parse(["--title", "a", "--artist", "b"])));
        Test("可选参数解析", () => Assert(Options.Parse(["--allow-online", "--title", "a", "--artist", "b", "--album", "c", "--duration", "123.5", "--songmid", "m", "--preview", "--output", "x.lrc"]).Duration == 123.5));
        Test("非法时长拒绝", () => Reject(() => Options.Parse(["--allow-online", "--title", "a", "--artist", "b", "--duration", "NaN"])));
        Test("未知参数拒绝", () => Reject(() => Options.Parse(["--allow-online", "--title", "a", "--artist", "b", "--cookie", "x"])));
        Test("自测不能混用联网参数", () => Reject(() => Options.Parse(["--allow-online", "--self-test"])));
        Test("重复授权拒绝", () => Reject(() => Options.Parse(["--allow-online", "--allow-online"])));
        const string detail = """{"code":0,"req_1":{"code":0,"data":{"track_info":{"mid":"m1","title":"Ａ Song","singer":[{"name":"Singer (Live)"}],"album":{"title":"Album"},"interval":120}}}}""";
        Test("详情字段完整解析", () => Assert(OnlineLyricsClient.ParseDetails(detail, "m1").Interval == 120));
        Test("详情MID不一致拒绝", () => Reject(() => OnlineLyricsClient.ParseDetails(detail, "m2")));
        Test("详情业务码拒绝", () => Reject(() => OnlineLyricsClient.ParseDetails(detail.Replace("\"code\":0", "\"code\":403"), "m1")));
        Test("详情缺少track_info拒绝", () => Reject(() => OnlineLyricsClient.ParseDetails(detail.Replace("track_info", "missing"), "m1")));
        Test("详情缺少专辑拒绝", () => Reject(() => OnlineLyricsClient.ParseDetails(detail.Replace("\"Album\"", "\"\""), "m1")));
        Test("详情零时长拒绝", () => Reject(() => OnlineLyricsClient.ParseDetails(detail.Replace("120", "0"), "m1")));
        var details = OnlineLyricsClient.ParseDetails(detail, "m1");
        Test("详情NFKC与2秒边界接受", () => Assert(details.MatchReason("A Song", "Singer (Live)", "Album", 122) is not null));
        Test("时长渐进容忍超过2秒", () => Assert(details.MatchReason("A Song", "Singer (Live)", "Album", 122.001) is not null));
        Test("仅歌手括号附加别名", () => Assert(details.MatchReason("A Song", "Singer", "Album", 120) is not null));
        Test("显式单曲歌手别名", () => Assert(details.MatchReason("A Song", "Singer", "Album", 120, "Singer (Live)") is not null));
        Test("同录音不同专辑容忍", () => Assert(details.MatchReason("A Song", "Singer (Live)", "Other", 120) is not null));
        Test("标题不一致拒绝", () => Assert(details.MatchReason("A Song (Live)", "Singer (Live)", "Album", 120) is null));
        Test("未知专辑仅失去软分", () => Assert(details.MatchReason("A Song", "Singer (Live)", "", 120) is not null));
        Test("HTTP401拒绝", () => Reject(() => Protocol.CheckHttp(HttpStatusCode.Unauthorized)));
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
            Test($"HTTP{(int)status} 后客户端不再请求", () =>
            {
                var handler = new FakeHandler(_ => new HttpResponseMessage(status));
                using var client = new OnlineLyricsClient(handler);
                Reject(() => client.SearchAsync("test").GetAwaiter().GetResult());
                Reject(() => client.SearchAsync("test").GetAwaiter().GetResult());
                Assert(handler.Count == 1);
            });
        Test("公共客户端请求协议与完整流程（仅内存假响应）", () =>
        {
            var handler = new FakeHandler(request =>
            {
                Assert(!request.Headers.Contains("Cookie") && request.Headers.Authorization is null);
                string body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                string result;
                if (request.Method == HttpMethod.Get) result = search;
                else if (body.Contains("get_song_detail_yqq"))
                {
                    Assert(body.Contains("music.pf_song_detail_svr") && body.Contains("song_mid"));
                    result = detail;
                }
                else
                {
                    using var json = JsonDocument.Parse(body);
                    Assert(json.RootElement.GetProperty("req_1").GetProperty("param").GetProperty("qrc").GetInt32() == 0);
                    result = Response(lrc);
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result, Encoding.UTF8, "application/json") };
            });
            using var client = new OnlineLyricsClient(handler);
            Assert(client.SearchAsync("test").GetAwaiter().GetResult().Count == 3);
            Assert(client.GetDetailsAsync("m1").GetAwaiter().GetResult().Mid == "m1");
            Assert(client.FetchAsync("m1").GetAwaiter().GetResult().Document.Lines.Count == 2);
            Assert(handler.Count == 3);
        });
        var free = new SongDetails("free", "Free Free Free", ["東京スカパラダイスオーケストラ", "幾田りら"], "Album", 254);
        const string freeArtist = "東京スカパラダイスオーケストラ (东京斯卡乐团)/幾田りら (ikura)";
        Test("用户实例无需硬编码自动接受", () => Assert(SongMatcher.Rank([free], "Free Free Free", freeArtist, "Album", 254.233).Winner?.Details.Mid == "free"));
        Test("多人顺序分隔符及括号别名", () => Assert(SongMatcher.Evaluate(free, "Ｆｒｅｅ—Free Free", "ikura、东京斯卡乐团", "Album", 254).Category == MatchCategory.ArtistConflict
            && SongMatcher.Evaluate(free, "Ｆｒｅｅ—Free Free", "幾田りら & 東京スカパラダイスオーケストラ", "Album", 254).Category == MatchCategory.Accepted));
        Test("括号别名双向但不凭空翻译", () => Assert(SongMatcher.Evaluate(free with { Singers = ["东京斯卡乐团", "ikura"] }, "Free Free Free", freeArtist, "Album", 254).Category == MatchCategory.Accepted));
        Test("翻唱硬拒绝", () => Assert(SongMatcher.Evaluate(free with { Singers = ["Other"] }, free.Title, freeArtist, "Album", 254).Category == MatchCategory.ArtistConflict));
        foreach (string version in new[] { "Live", "现场", "伴奏", "instrumental", "remix", "重录", "Acoustic", "Demo", "Remastered" })
        {
            Test("版本缺省拒绝 " + version, () => Assert(SongMatcher.Evaluate(free with { Title = free.Title + " (" + version + ")" }, free.Title, freeArtist, "Album", 254).Category == MatchCategory.VersionConflict));
            Test("源版本缺省不乱混 " + version, () => Assert(SongMatcher.Evaluate(free, free.Title + " (" + version + ")", freeArtist, "Album", 254).Category == MatchCategory.VersionConflict));
        }
        Test("Live和现场同义版本", () => Assert(SongMatcher.Evaluate(free with { Title = free.Title + " (Live)" }, free.Title + " (现场)", freeArtist, "Album", 254).Category == MatchCategory.Accepted));
        var release = new SongDetails("002gixhP1FUQje", "如果你也听说 (Live|典藏)", ["那英"], "2024 第9期", 334);
        Test("那英用户案例发行标签在严格约束下接受", () =>
        {
            var result = SongMatcher.Rank([release], "如果你也听说 (Live)", "那英", "2024 第9期", 336.958);
            Assert(result.Winner?.Details.Mid == release.Mid);
            Assert(result.Winner!.Reason.Contains("发行标签差异接受") && result.Winner.Reason.Contains("2.958s"));
        });
        foreach (bool reverse in new[] { false, true })
        {
            var tagged = free with { Title = "示例曲 (Live|典藏)" };
            string source = reverse ? tagged.Title : "示例曲 (Live)";
            var candidate = reverse ? tagged with { Title = "示例曲 (Live)" } : tagged;
            SongScore Check(double delta = 0, string? album = "Album", string? singer = null, string? candidateTitle = null) =>
                SongMatcher.Evaluate(candidateTitle is null ? candidate : candidate with { Title = candidateTitle },
                    source, singer ?? freeArtist, album, 254 + delta);
            Test($"发行标签双向接受 {reverse}", () => Assert(Check().Category == MatchCategory.Accepted));
            Test($"发行标签时长5秒边界 {reverse}", () => Assert(Check(5).Category == MatchCategory.Accepted
                && Check(-5).Category == MatchCategory.Accepted));
            Test($"发行标签时长超过5秒拒绝 {reverse}", () => Assert(Check(5.001).Category == MatchCategory.DurationConflict
                && Check(-5.001).Reason.Contains("超过 5s")));
            foreach (string? album in new string?[] { null, "", " ", "Other", "---" })
                Test($"发行标签未知或不同专辑拒绝 {reverse} [{album}]", () => Assert(Check(album: album).Category == MatchCategory.TitleConflict
                    && Check(album: album).Reason.Contains("专辑必须非空且一致")));
            Test($"发行标签歌手冲突拒绝 {reverse}", () => Assert(Check(singer: "Other").Category == MatchCategory.ArtistConflict
                && Check(singer: "Other").Reason.Contains("发行标签差异未获接受")));
            Test($"发行标签不能掩盖Live缺省 {reverse}", () => Assert(Check(candidateTitle: reverse ? "示例曲" : "示例曲 (典藏)").Category == MatchCategory.VersionConflict));
            foreach (string version in new[] { "Remix", "伴奏", "重录" })
                Test($"发行标签不能掩盖版本冲突 {reverse} {version}", () => Assert(Check(candidateTitle: "示例曲 (" + version + (reverse ? ")" : "|典藏)")).Category == MatchCategory.VersionConflict));
            foreach (string extra in new[] { "未知", "Tokyo 2024", "Tokyo 2025", "Beijing 2024" })
                Test($"发行标签保留未知词地点年份 {reverse} {extra}", () => Assert(Check(candidateTitle: "示例曲 (Live|" + extra + (reverse ? ")" : "|典藏)")).Category == MatchCategory.TitleConflict));
        }
        foreach (string bracket in new[] { "(Live|典藏)", "（Live｜典藏）", "[Live / 典藏]", "【典藏、Live】", "(Live;典藏)", "(Live,典藏)", "(Live)(典藏)" })
            Test("发行标签明确括号分隔token " + bracket, () => Assert(SongMatcher.Evaluate(free with { Title = "示例曲 " + bracket },
                "示例曲 (Live)", freeArtist, "Album", 254).Category == MatchCategory.Accepted));
        foreach (string title in new[] { "示例曲典藏 (Live)", "典藏示例曲 (Live)", "示例曲 (Live|典藏版)", "示例曲 (Live|珍贵典藏)", "示例曲 Live|典藏", "示例曲 (Live 典藏)", "示例曲 (Live|典藏未知)" })
            Test("典藏非完整括号token不删除 " + title, () => Assert(SongMatcher.Evaluate(free with { Title = title },
                "示例曲 (Live)", freeArtist, "Album", 254).Category == MatchCategory.TitleConflict));
        Test("主体典藏保留且同名可匹配", () => Assert(SongMatcher.Evaluate(free with { Title = "典藏 (Live|典藏)" },
            "典藏 (Live)", freeArtist, "Album", 254).Category == MatchCategory.Accepted));
        Test("发行标签差异候选可进入召回但未知词不能", () => Assert(SongMatcher.IsPlausibleCandidate(new Song("generic", "示例曲 (Live|典藏)", "Singer"), "示例曲 (Live)")
            && !SongMatcher.IsPlausibleCandidate(new Song("generic", "示例曲 (Live|未知)", "Singer"), "示例曲 (Live)")));
        Test("发行标签相同保持原有专辑软评分", () => Assert(SongMatcher.Evaluate(release, release.Title, "那英", "", 334).Category == MatchCategory.Accepted));
        Test("发行标签候选专辑缺失仍拒绝", () => Assert(SongMatcher.Evaluate(release with { Album = "" }, "如果你也听说 (Live)",
            "那英", "2024 第9期", 336.958).Category == MatchCategory.InvalidMetadata));
        Test("发行标签不抹除不同年份地点", () => Assert(SongMatcher.Evaluate(free with { Title = "示例曲 (Live|Tokyo 2024|典藏)" },
            "示例曲 (Live|Tokyo 2025)", freeArtist, "Album", 254).Category == MatchCategory.TitleConflict
            && SongMatcher.Evaluate(free with { Title = "示例曲 (Live|Tokyo 2024|典藏)" },
            "示例曲 (Live|Beijing 2024)", freeArtist, "Album", 254).Category == MatchCategory.TitleConflict));
        Test("未知歌名括号不剥除", () => Assert(SongMatcher.Evaluate(free with { Title = free.Title + " (Tokyo 2024)" }, free.Title, freeArtist, "Album", 254).Category == MatchCategory.TitleConflict));
        Test("多人不能缺人或重复占用", () => Assert(SongMatcher.Evaluate(free, free.Title, "幾田りら", "Album", 254).Category == MatchCategory.ArtistConflict
            && SongMatcher.Evaluate(free, free.Title, "幾田りら/幾田りら", "Album", 254).Category == MatchCategory.ArtistConflict));
        Test("时长巨大差距拒绝", () => Assert(SongMatcher.Evaluate(free, free.Title, freeArtist, "Album", 400).Category == MatchCategory.DurationConflict));
        Test("时长评分渐进且阈值生效", () => Assert(SongMatcher.Evaluate(free, free.Title, freeArtist, "Album", 257).Score > SongMatcher.Evaluate(free, free.Title, freeArtist, "Album", 262).Score
            && SongMatcher.Rank([free], free.Title, freeArtist, "Album", 264).Category == MatchCategory.BelowThreshold));
        Test("同分候选歧义", () => Assert(SongMatcher.Rank([free, free with { Mid = "two" }], free.Title, freeArtist, "Album", 254).Category == MatchCategory.Ambiguous));
        Test("领先不足拒绝", () => Assert(SongMatcher.Rank([free, free with { Mid = "two", Interval = 257 }], free.Title, freeArtist, "Album", 254).Category == MatchCategory.Ambiguous));
        Test("领先达到边界自动选择且与输入顺序无关", () => Assert(SongMatcher.Rank([free with { Mid = "two", Album = "Other" }, free], free.Title, freeArtist, "Album", 254).Winner?.Details.Mid == "free"));
        Test("第二名低于阈值仍参与领先差", () => Assert(SongMatcher.Rank([free with { Interval = 262 }, free with { Mid = "two", Interval = 263 }], free.Title, freeArtist, "Album", 254).Category == MatchCategory.Ambiguous));
        Test("空候选与硬冲突分类", () => Assert(SongMatcher.Rank([], free.Title, freeArtist, "Album", 254).Category == MatchCategory.NoCandidates
            && SongMatcher.Rank([free with { Title = "Other" }], free.Title, freeArtist, "Album", 254).Category == MatchCategory.TitleConflict));
        Test("缺字段候选跳过后同客户端继续", () =>
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(detail.Replace("\"Album\"", "\"\""), Encoding.UTF8) });
            using var client = new OnlineLyricsClient(handler);
            for (int i = 0; i < 2; i++)
            {
                try { client.GetDetailsAsync("m1").GetAwaiter().GetResult(); throw new Exception("应跳过"); }
                catch (CandidateMetadataException) { }
            }
            Assert(handler.Count == 2);
        });
        Test("自动选择分数阈值精确边界", () =>
        {
            Assert(SongMatcher.Rank([free], free.Title, freeArtist, "Album", 263.4).Category == MatchCategory.Accepted);
            Assert(SongMatcher.Rank([free], free.Title, freeArtist, "Album", 263.401).Category == MatchCategory.BelowThreshold);
        });
        Test("标题主体不是子串匹配", () => Assert(SongMatcher.Evaluate(free, "Free", freeArtist, "Album", 254).Category == MatchCategory.TitleConflict));
        Test("搜索业务错误不阻止下一曲", () =>
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":9}", Encoding.UTF8) });
            using var client = new OnlineLyricsClient(handler);
            Reject(() => client.SearchAsync("test").GetAwaiter().GetResult());
            Reject(() => client.SearchAsync("test").GetAwaiter().GetResult());
            Assert(handler.Count == 2);
        });
        Test("无歌词分类不伪装成权限错误且允许下一曲", () =>
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response(""), Encoding.UTF8) });
            using var client = new OnlineLyricsClient(handler);
            for (int i = 0; i < 2; i++)
            {
                try { client.FetchAsync("m1").GetAwaiter().GetResult(); throw new Exception(); }
                catch (NoLyricsException) { }
            }
            Assert(handler.Count == 2);
        });
        Test("详情业务异常不阻止下一曲", () =>
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(detail.Replace("\"code\":0", "\"code\":403"), Encoding.UTF8) });
            using var client = new OnlineLyricsClient(handler);
            Reject(() => client.GetDetailsAsync("m1").GetAwaiter().GetResult());
            Reject(() => client.GetDetailsAsync("m1").GetAwaiter().GetResult());
            Assert(handler.Count == 2);
        });
        static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) };
        static string CandidateResponse(params string[] mids) => JsonSerializer.Serialize(new { code = 0, data = new { song = new { itemlist = mids.Select(mid => new { mid, name = "Last Dance", singer = "摘要不可信" }) } } });
        static string DetailResponse(string mid, string singer = "徐佳莹", string title = "Last Dance (Live)") => JsonSerializer.Serialize(new
        {
            code = 0, req_1 = new { code = 0, data = new { track_info = new { mid, title, singer = new[] { new { name = singer } }, album = new { title = "歌手·当打之年 第7期" }, interval = 289 } } }
        });
        Test("Last Dance 分阶段召回真实版本并跨查询MID去重、提前停止", () =>
        {
            var keys = new List<string>();
            var mids = new List<string>();
            var handler = new FakeHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    string key = Uri.UnescapeDataString(request.RequestUri!.Query.Split("key=")[1].Split('&')[0]);
                    keys.Add(key);
                    return JsonResponse(keys.Count == 1 ? CandidateResponse("wrong") : CandidateResponse("wrong", "004TwGgS0j6pI5", "004TwGgS0j6pI5"));
                }
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                string mid = body.RootElement.GetProperty("req_1").GetProperty("param").GetProperty("song_mid").GetString()!;
                mids.Add(mid);
                return JsonResponse(DetailResponse(mid, mid == "wrong" ? "伍佰" : "徐佳莹"));
            });
            using var client = new OnlineLyricsClient(handler);
            var result = client.RecallAsync("Last Dance (Live)", "徐佳莹", "歌手·当打之年 第7期", 289.939).GetAwaiter().GetResult();
            Assert(result.Winner?.Details.Mid == "004TwGgS0j6pI5" && handler.Count == 4);
            Assert(keys.SequenceEqual(new[] { "Last Dance (Live) 徐佳莹", "Last Dance 徐佳莹" }));
            Assert(mids.SequenceEqual(new[] { "wrong", "004TwGgS0j6pI5" }));
        });
        Test("完整批次歧义不抢先选首个，原标题阶段仍受请求上限", () =>
        {
            int searches = 0;
            var handler = new FakeHandler(request => request.Method == HttpMethod.Get
                ? JsonResponse(++searches == 1 ? CandidateResponse("one", "two") : CandidateResponse("one"))
                : JsonResponse(DetailResponse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("one") ? "one" : "two")));
            using var client = new OnlineLyricsClient(handler);
            Assert(client.RecallAsync("Last Dance (Live)", "徐佳莹", "", 289.939).GetAwaiter().GetResult().Category == MatchCategory.Ambiguous);
            Assert(searches == 3 && handler.Count == 5);
        });
        Test("无版本标题去重查询且首批可信命中只搜索一次", () =>
        {
            int searches = 0, checks = 0;
            var handler = new FakeHandler(request => request.Method == HttpMethod.Get
                ? JsonResponse(++searches > 0 ? CandidateResponse("good") : CandidateResponse())
                : JsonResponse(DetailResponse("good", title: "Last Dance")));
            using var client = new OnlineLyricsClient(handler);
            var result = client.RecallAsync("Last Dance", "徐佳莹", "", 289.939,
                beforeRequest: () => { checks++; return Task.CompletedTask; }).GetAwaiter().GetResult();
            Assert(result.Winner is not null && searches == 1 && handler.Count == 2 && checks == 2);
        });
        Test("异步媒体检查阻止下一阶段请求", () =>
        {
            int checks = 0;
            var handler = new FakeHandler(_ => JsonResponse(CandidateResponse()));
            using var client = new OnlineLyricsClient(handler);
            try
            {
                client.RecallAsync("Last Dance (Live)", "徐佳莹", "", 289.939,
                    beforeRequest: () => ++checks == 2 ? Task.FromException(new OperationCanceledException()) : Task.CompletedTask).GetAwaiter().GetResult();
                throw new Exception();
            }
            catch (OperationCanceledException) { }
            Assert(handler.Count == 1 && checks == 2);
        });
        Test("预先取消不发首个请求", () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var handler = new FakeHandler(_ => throw new Exception());
            using var client = new OnlineLyricsClient(handler);
            try { client.RecallAsync("Last Dance (Live)", "徐佳莹", "", 289.939, cancellation.Token).GetAwaiter().GetResult(); throw new Exception(); }
            catch (OperationCanceledException) { }
            Assert(handler.Count == 0);
        });
        Test("版本缺省仍硬拒绝且查询关键词去重", () =>
        {
            int searches = 0;
            var handler = new FakeHandler(request => request.Method == HttpMethod.Get
                ? JsonResponse(++searches > 0 ? CandidateResponse("studio") : CandidateResponse())
                : JsonResponse(DetailResponse("studio", title: "Last Dance")));
            using var client = new OnlineLyricsClient(handler);
            Assert(client.RecallAsync("Last Dance (Live)", "徐佳莹", "", 289.939).GetAwaiter().GetResult().Category == MatchCategory.VersionConflict);
            Assert(searches == 3 && handler.Count == 4);
        });
        Test("跨阶段候选累计上限二十，溢出不截断后自动误选", () =>
        {
            int searches = 0;
            var handler = new FakeHandler(request => request.Method == HttpMethod.Get
                ? JsonResponse(++searches == 1 ? CandidateResponse(Enumerable.Range(0, 20).Select(i => "m" + i).ToArray()) : CandidateResponse("overflow"))
                : JsonResponse("{\"code\":0,\"req_1\":{\"code\":0,\"data\":{\"track_info\":{}}}}"));
            using var client = new OnlineLyricsClient(handler);
            Assert(client.RecallAsync("Last Dance (Live)", "徐佳莹", "", 289.939).GetAwaiter().GetResult().Category == MatchCategory.InvalidMetadata);
            Assert(handler.Count == 22 && searches == 2);
        });
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
            Test("召回第二阶段权限错误停止后续请求 " + status, () =>
            {
                var handler = new FakeHandler(request => request.RequestUri!.Query.Contains("%28Live%29")
                    ? JsonResponse(CandidateResponse()) : new(status));
                using var client = new OnlineLyricsClient(handler);
                Reject(() => client.RecallAsync("Last Dance (Live)", "徐佳莹", "", 289.939).GetAwaiter().GetResult());
                Reject(() => client.SearchAsync("again").GetAwaiter().GetResult());
                Assert(handler.Count == 2);
            });
        foreach (bool cancel in new[] { true, false })
            Test("阶段边界取消或停网不再请求 " + cancel, () =>
            {
                using var cancellation = new CancellationTokenSource();
                bool blocked = false;
                var handler = new FakeHandler(_ =>
                {
                    if (cancel) cancellation.Cancel(); else blocked = true;
                    return JsonResponse(CandidateResponse());
                });
                using var client = new OnlineLyricsClient(handler);
                try
                {
                    client.RecallAsync("Last Dance (Live)", "徐佳莹", "", 289.939, cancellation.Token,
                        () => { if (blocked) throw new OperationCanceledException(); }).GetAwaiter().GetResult();
                    throw new Exception("应取消");
                }
                catch (OperationCanceledException) { }
                Assert(handler.Count == 1);
            });
        Console.WriteLine($"离线自测完成：{passed} 通过，{failed} 失败；仅内存假响应，未联网、未写出歌词。");
        return failed == 0 ? 0 : 1;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(respond(request));
        }
    }
}
