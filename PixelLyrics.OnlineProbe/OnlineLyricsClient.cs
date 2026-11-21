using System.Net.Http.Json;
using System.Text.Json;

namespace PixelLyrics.OnlineProbe;

public sealed record SongDetails(string Mid, string Title, IReadOnlyList<string> Singers, string Album, double Interval)
{
    public string? MatchReason(string title, string artist, string album, double seconds, string? artistAlias = null)
    {
        var score = SongMatcher.Evaluate(this, title, artist, album, seconds, artistAlias);
        return score.Category == MatchCategory.Accepted ? score.Reason : null;
    }
}

public sealed class OnlineLyricsClient : ILyricsSource
{
    private readonly HttpClient client;
    private readonly int timeoutMs;
    public RequestRecovery Recovery { get; set; } = new();
    public Func<CancellationToken, Task>? BeforeRequest { get; set; }
    public Action<RequestNotice>? Report { get; set; }

    public OnlineLyricsClient(int timeoutMs = 10000) : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null,
        ConnectTimeout = TimeSpan.FromMilliseconds(timeoutMs)
    }, timeoutMs) { }

    internal OnlineLyricsClient(HttpMessageHandler handler, int timeoutMs = 10000)
    {
        if (timeoutMs is < 2000 or > 30000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        this.timeoutMs = timeoutMs;
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        client.DefaultRequestHeaders.Referrer = new Uri("https://y.qq.com/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
    }

    public async Task<IReadOnlyList<Song>> SearchAsync(string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("歌名不可为空。", nameof(title));
        return await Recovery.ExecuteAsync(client, () => new HttpRequestMessage(HttpMethod.Get,
            "https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg?key=" + Uri.EscapeDataString(title) + "&format=json"),
            Protocol.Candidates, RequestStage.Search, timeoutMs, cancellationToken, BeforeRequest, Report);
    }

    public const int MaximumSearchRequests = 3;
    public const int MaximumCandidates = 20;

    public async Task<SongSelection> RecallAsync(string title, string artist, string? album, double seconds,
        CancellationToken token = default, Action? ensureNetworkAllowed = null,
        string? requiredMid = null, string? artistAlias = null, Func<Task>? beforeRequest = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var details = new List<SongDetails>();
        void CheckNetwork()
        {
            token.ThrowIfCancellationRequested();
            ensureNetworkAllowed?.Invoke();
            token.ThrowIfCancellationRequested();
        }
        var queries = new[] { title + " " + (artistAlias ?? artist),
            SongMatcher.RecallBaseTitle(title) + " " + (artistAlias ?? artist), title }
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumSearchRequests);
        foreach (string query in queries)
        {
            if (beforeRequest is not null) await beforeRequest();
            CheckNetwork();
            var candidates = await SearchAsync(query, token);
            // 搜索摘要可能省略版本、地点或歌手，不在详情核验前按摘要过滤。
            var fresh = candidates.Where(c => requiredMid is null || c.Mid == requiredMid)
                .Select(c => c.Mid).Distinct(StringComparer.Ordinal).Where(mid => !seen.Contains(mid)).ToArray();
            if (seen.Count + fresh.Length > MaximumCandidates)
                return new(MatchCategory.InvalidMetadata, null, []);
            foreach (string mid in fresh)
            {
                seen.Add(mid);
                if (mid.Length > 64 || mid.Any(c => !char.IsAsciiLetterOrDigit(c))) continue;
                if (beforeRequest is not null) await beforeRequest();
                CheckNetwork();
                try { details.Add(await GetDetailsAsync(mid, token)); }
                catch (CandidateMetadataException) { /* 坏元数据仅跳过当前候选。 */ }
            }
            token.ThrowIfCancellationRequested();
            var selection = SongMatcher.Rank(details, title, artist, album, seconds, artistAlias);
            // 整批候选核验完再判断歧义，严格匹配且时长差不超过一秒才提前停止。
            if (selection.Winner is { } winner && Math.Abs(winner.Details.Interval - seconds) <= 1)
                return selection;
        }
        return details.Count == 0 && seen.Count > 0 ? new(MatchCategory.InvalidMetadata, null, [])
            : SongMatcher.Rank(details, title, artist, album, seconds, artistAlias);
    }

    public async Task<SongDetails> GetDetailsAsync(string mid, CancellationToken cancellationToken = default)
    {
        ValidateMid(mid);
        return await Recovery.ExecuteAsync(client, () => Request("music.pf_song_detail_svr", "get_song_detail_yqq", new { song_mid = mid, song_type = 0 }),
            json => ParseDetails(json, mid), RequestStage.Details, timeoutMs, cancellationToken, BeforeRequest, Report);
    }

    // Explicit opt-in diagnostics: metadata only; never fetch lyrics or return account/file credentials.
    public async Task<JsonElement> InspectMetadataAsync(string mid, CancellationToken token)
    {
        ValidateMid(mid);
        using var request = Request("music.pf_song_detail_svr", "get_song_detail_yqq", new { song_mid = mid, song_type = 0 });
        using var doc = JsonDocument.Parse(await SendAsync(request, token));
        Protocol.Codes(doc.RootElement, true);
        var req = Protocol.Object(doc.RootElement, "req_1");
        Protocol.Codes(req, true);
        var track = Protocol.Object(Protocol.Object(req, "data"), "track_info");
        var safe = new Dictionary<string, JsonElement>();
        foreach (string key in new[] { "id", "mid", "name", "title", "subtitle", "interval", "type", "index_cd", "index_album", "time_public" })
            if (track.TryGetProperty(key, out var value)) safe[key] = value.Clone();
        foreach (string key in new[] { "album", "singer" })
        {
            if (!track.TryGetProperty(key, out var value)) continue;
            Dictionary<string, JsonElement> Select(JsonElement item) => item.EnumerateObject()
                .Where(p => new[] { "id", "mid", "name", "title", "subtitle" }.Contains(p.Name))
                .ToDictionary(p => p.Name, p => p.Value.Clone());
            safe[key] = key == "singer" ? JsonSerializer.SerializeToElement(value.EnumerateArray().Select(Select).ToArray())
                : JsonSerializer.SerializeToElement(Select(value));
        }
        return JsonSerializer.SerializeToElement(safe);
    }

    public async Task<VerifiedLyrics> FetchAsync(string mid, CancellationToken cancellationToken = default)
    {
        ValidateMid(mid);
        return await Recovery.ExecuteAsync(client, () => Request("music.musichallSong.PlayLyricInfo", "GetPlayLyricInfo",
            new { songMID = mid, songID = 0, qrc = 0, trans = 0, roma = 0 }),
            Protocol.Lyrics, RequestStage.Lyrics, timeoutMs, cancellationToken, BeforeRequest, Report);
    }

    private static void ValidateMid(string mid)
    {
        if (string.IsNullOrWhiteSpace(mid) || mid.Length > 64 || mid.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ProbeException("songMID 格式无效。");
    }

    private static HttpRequestMessage Request(string module, string method, object param) => new(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
    {
        Content = JsonContent.Create(new
        {
            comm = new { cv = 4747474, ct = 24, format = "json", inCharset = "utf-8", outCharset = "utf-8", notice = 0, platform = "yqq.json", needNewCode = 1, uin = 0 },
            req_1 = new { module, method, param }
        })
    };

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        return await Recovery.ExecuteAsync(client, () => {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            if (request.Content is not null) copy.Content = new StringContent(request.Content.ReadAsStringAsync(token).GetAwaiter().GetResult(), System.Text.Encoding.UTF8, "application/json");
            return copy;
        }, json => json, RequestStage.Details, timeoutMs, token, BeforeRequest, Report);
    }

    internal static SongDetails ParseDetails(string json, string expectedMid)
    {
        using var doc = JsonDocument.Parse(json);
        Protocol.Codes(doc.RootElement, true);
        var request = Protocol.Object(doc.RootElement, "req_1");
        Protocol.Codes(request, true);
        try
        {
        var track = Protocol.Object(Protocol.Object(request, "data"), "track_info");
        string Required(JsonElement element, string key) => Protocol.String(element, key) is string value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ProbeException("详情缺少字段：" + key);
        string mid = Required(track, "mid");
        if (mid != expectedMid) throw new LyricsRequestException(RequestFailureKind.Identity, RequestStage.Details, "歌曲详情 ID 与请求不一致");
        string title = Required(track, "title");
        string album = Required(Protocol.Object(track, "album"), "title");
        if (!track.TryGetProperty("interval", out var interval) || interval.ValueKind != JsonValueKind.Number || !interval.TryGetDouble(out double seconds)
            || !double.IsFinite(seconds) || seconds <= 0) throw new ProbeException("详情时长无效。");
        if (!track.TryGetProperty("singer", out var singers) || singers.ValueKind != JsonValueKind.Array || singers.GetArrayLength() == 0)
            throw new ProbeException("详情歌手缺失。");
        return new(mid, title, singers.EnumerateArray().Select(s => Required(s, "name")).ToArray(), album, seconds);
        }
        catch (Exception ex) when (ex is ProbeException and not LyricsRequestException or InvalidOperationException)
        {
            throw new CandidateMetadataException("单候选详情元数据无效。");
        }
    }

    public void Dispose() => client.Dispose();
}
