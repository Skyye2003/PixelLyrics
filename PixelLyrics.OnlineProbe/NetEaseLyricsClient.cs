using System.Globalization;
using System.Text.Json;
using PixelLyrics.Core;

namespace PixelLyrics.OnlineProbe;

// Site endpoints: anonymous requests only, no cookies, redirects, proxy or retries.
public sealed class NetEaseLyricsClient : ILyricsSource
{
    private readonly HttpClient client;
    private bool stopped;
    public NetEaseLyricsClient(int timeoutMs = 10000) : this(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null,
        ConnectTimeout = TimeSpan.FromMilliseconds(timeoutMs) }, timeoutMs) { }
    internal NetEaseLyricsClient(HttpMessageHandler handler, int timeoutMs = 10000)
    {
        if (timeoutMs is < 2000 or > 30000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        client = new(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
    }
    private static void ValidateId(string id)
    {
        if (id.Length is 0 or > 19 || !id.All(char.IsAsciiDigit) || !long.TryParse(id, out long value) || value <= 0)
            throw new ProbeException("网易云歌曲 ID 无效。");
    }
    private async Task<T> Request<T>(string path, CancellationToken token, Func<JsonElement, T> parse,
        Dictionary<string, string>? form = null)
    {
        if (stopped) throw new ProbeException("在线客户端已停止，禁止后续请求。");
        try
        {
            using var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, "https://music.163.com" + path);
            if (form is not null) request.Content = new FormUrlEncodedContent(form);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
            Protocol.CheckHttp(response.StatusCode);
            using var doc = JsonDocument.Parse(Protocol.Utf8(await response.Content.ReadAsByteArrayAsync(token)));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code)
                || !code.TryGetInt32(out int value) || value != 200)
                throw new ProbeException("网易云接口业务码异常。");
            return parse(root);
        }
        catch (NoLyricsException) { throw; }
        catch (CandidateMetadataException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { stopped = true; throw new ProbeException("网易云响应格式错误。"); }
        catch { stopped = true; throw; }
    }
    public Task<SongDetails> GetDetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return Request("/api/song/detail/?id=" + id + "&ids=%5B" + id + "%5D", cancellationToken, root => ParseDetails(root, id));
    }
    internal static SongDetails ParseDetails(JsonElement root, string expectedId)
    {
        if (!root.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array || songs.GetArrayLength() != 1)
            throw new CandidateMetadataException("网易云详情缺少唯一歌曲。");
        var song = songs[0];
        string Required(JsonElement item, string name) => Protocol.String(item, name) is { Length: > 0 } s
            && !string.IsNullOrWhiteSpace(s) ? s : throw new CandidateMetadataException("网易云详情字段缺失。");
        if (!song.TryGetProperty("id", out var id) || !id.TryGetInt64(out long number)
            || number.ToString(CultureInfo.InvariantCulture) != expectedId) throw new ProbeException("网易云详情 ID 不一致。");
        var album = song.TryGetProperty("album", out var a) ? a : song.GetProperty("al");
        var artists = song.TryGetProperty("artists", out var ar) ? ar : song.GetProperty("ar");
        var duration = song.TryGetProperty("duration", out var d) ? d : song.GetProperty("dt");
        if (artists.ValueKind != JsonValueKind.Array || artists.GetArrayLength() == 0
            || !duration.TryGetDouble(out double ms) || !double.IsFinite(ms) || ms <= 0)
            throw new CandidateMetadataException("网易云详情时长或歌手无效。");
        return new(expectedId, Required(song, "name"), artists.EnumerateArray().Select(s => Required(s, "name")).ToArray(),
            Required(album, "name"), ms / 1000d);
    }
    public Task<VerifiedLyrics> FetchAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return Request("/api/song/lyric?id=" + id + "&lv=-1&kv=0&tv=0&rv=0", cancellationToken, ParseLyrics);
    }
    internal static VerifiedLyrics ParseLyrics(JsonElement root)
    {
        foreach (string flag in new[] { "nolyric", "uncollected" })
            if (root.TryGetProperty(flag, out var value) && value.ValueKind == JsonValueKind.True) throw new NoLyricsException("网易云明确返回无歌词。");
        if (!root.TryGetProperty("lrc", out var lrc) || lrc.ValueKind != JsonValueKind.Object
            || !lrc.TryGetProperty("lyric", out var lyric) || lyric.ValueKind != JsonValueKind.String)
            throw new ProbeException("网易云响应缺少 LRC。");
        string text = lyric.GetString()!;
        if (string.IsNullOrWhiteSpace(text)) throw new NoLyricsException("网易云返回空歌词。");
        // NetEase's web player reads this observed tag as 0 seconds (parseFloat("00.00-1")).
        // Only normalize the verified spelling, at a line's leading tag; keep other malformed tags invalid.
        string normalized = string.Join("\n", text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line =>
            line.StartsWith("[00:00.00-1]", StringComparison.Ordinal) ? "[00:00.00]" + line[12..] : line));
        var parsed = LrcParser.Parse(normalized);
        if (parsed.Errors.Count != 0 || !parsed.Document.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Text)))
            throw new ProbeException("网易云 LRC 无效。");
        return new(text, parsed.Document, new Dictionary<string, string>());
    }
    public async Task<SongSelection> RecallAsync(string title, string artist, string? album, double seconds,
        CancellationToken token = default, Action? ensureNetworkAllowed = null, string? requiredMid = null,
        string? artistAlias = null, Func<Task>? beforeRequest = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var details = new List<SongDetails>();
        async Task Check() { token.ThrowIfCancellationRequested(); if (beforeRequest is not null) await beforeRequest(); ensureNetworkAllowed?.Invoke(); token.ThrowIfCancellationRequested(); }
        foreach (string query in new[] { title + " " + (artistAlias ?? artist), SongMatcher.RecallBaseTitle(title) + " " + (artistAlias ?? artist), title }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await Check();
            string[] ids = await Request("/api/search/get", token, root =>
            {
                var result = Protocol.Object(root, "result");
                if (!result.TryGetProperty("songs", out var songs))
                {
                    if (result.TryGetProperty("songCount", out var count) && count.TryGetInt32(out int n) && n == 0) return [];
                    throw new ProbeException("网易云搜索列表缺失。");
                }
                if (songs.ValueKind != JsonValueKind.Array) throw new ProbeException("网易云搜索格式错误。");
                return songs.EnumerateArray().Select(s => s.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture)).ToArray();
            }, new() { ["s"] = query, ["type"] = "1", ["limit"] = "20", ["offset"] = "0" });
            foreach (string id in ids.Distinct().Where(id => requiredMid is null || id == requiredMid))
            {
                if (!seen.Add(id)) continue;
                if (seen.Count > OnlineLyricsClient.MaximumCandidates) return new(MatchCategory.InvalidMetadata, null, []);
                await Check();
                try { details.Add(await GetDetailsAsync(id, token)); } catch (CandidateMetadataException) { }
            }
            var selection = SongMatcher.Rank(details, title, artist, album, seconds, artistAlias);
            if (selection.Winner is { } winner && Math.Abs(winner.Details.Interval - seconds) <= 1) return selection;
        }
        return details.Count == 0 && seen.Count > 0 ? new(MatchCategory.InvalidMetadata, null, [])
            : SongMatcher.Rank(details, title, artist, album, seconds, artistAlias);
    }
    public void Dispose() => client.Dispose();
}
