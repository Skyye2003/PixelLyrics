using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PixelLyrics.Core;

namespace PixelLyrics.OnlineProbe;

internal static class Program
{
    internal const string Disclosure = "联网说明：仅在显式指定 --allow-online --title --artist 后，向 QQ音乐 c.y.qq.com 发送歌名及歌手用于分阶段搜索，歌手仍在本地严格核对，向 u.y.qq.com 发送候选 songMID 核对详情，仅综合评分及领先差达标才获取歌词；不发送 Cookie、Authorization 或播放进度。自动选择需 --duration，专辑仅在本地软评分，重要版本冲突拒绝。不登录、不解密；临时请求失败有限重试，限流等待恢复。";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 1 && args[0] == "--self-test") return SelfTests.Run();
        if (!args.Contains("--allow-online", StringComparer.Ordinal))
        {
            Console.WriteLine(Disclosure);
            return 2;
        }
        try
        {
            Options options = Options.Parse(args);
            Console.WriteLine(Disclosure);
            using var client = new OnlineLyricsClient();
            // Each HTTP attempt has its own timeout; caller cancellation covers the full operation.
            using var deadline = new CancellationTokenSource();
            var selection = await client.RecallAsync(options.Title, options.Artist, options.Album,
                options.Duration ?? double.NaN, deadline.Token, requiredMid: options.SongMid);
            if (selection.Winner is not { } winner)
                throw new ProbeException("综合匹配拒绝：" + selection.Category);
            var selected = winner.Details;
            deadline.Token.ThrowIfCancellationRequested();
            var verified = await client.FetchAsync(selected.Mid, deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { selected.Mid, selected.Title, selected.Singers, Album = options.Album, DurationSeconds = options.Duration, ValidLines = verified.Document.Lines.Count, Metadata = verified.Metadata }));
            if (options.Preview)
                foreach (var line in verified.Document.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).Take(3))
                    Console.WriteLine($"[{line.Timestamp:c}] {JsonSerializer.Serialize(line.Text)}");
            if (options.Output is not null)
            {
                // 不创建父目录，不覆盖已有文件；只有显式 --output 才写出。
                await using var file = new FileStream(options.Output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await file.WriteAsync(new UTF8Encoding(false, true).GetBytes(verified.Text));
                Console.WriteLine("已保存验证后的 LRC：" + JsonSerializer.Serialize(Path.GetFullPath(options.Output)));
            }
            return 0;
        }
        catch (Exception ex) when (ex is ProbeException or NoLyricsException or UntimedLyricsException or CandidateMetadataException or JsonException or HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            Console.Error.WriteLine("请求未完成：" + JsonSerializer.Serialize(ex.Message));
            return 1;
        }
    }

}

public class ProbeException(string message) : Exception(message);
public sealed record Song(string Mid, string Name, string Singer);
public sealed record VerifiedLyrics(string Text, LyricsDocument Document, IReadOnlyDictionary<string, string> Metadata);
internal sealed record Options(string Title, string Artist, string? Album, double? Duration, string? SongMid, bool Preview, string? Output)
{
    internal static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (key is "--allow-online" or "--preview")
            {
                if (!flags.Add(key)) throw new ProbeException("重复参数：" + key);
            }
            else if (key is "--title" or "--artist" or "--album" or "--duration" or "--songmid" or "--output")
            {
                if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i]) || !values.TryAdd(key, args[i]))
                    throw new ProbeException("缺失、空白或重复参数：" + key);
            }
            else throw new ProbeException("未知参数或 --self-test 与其他参数混用：" + key);
        }
        if (!flags.Contains("--allow-online") || !values.TryGetValue("--title", out string? title) || !values.TryGetValue("--artist", out string? artist))
            throw new ProbeException("必须显式指定 --allow-online --title --artist。");
        double? duration = null;
        if (values.TryGetValue("--duration", out string? raw))
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || !double.IsFinite(seconds) || seconds <= 0)
                throw new ProbeException("--duration 必须是正数秒。");
            duration = seconds;
        }
        if (Protocol.Normalize(title).Length == 0 || Protocol.Normalize(artist).Length == 0)
            throw new ProbeException("歌名和歌手不可为空。");
        return new(title, artist, values.GetValueOrDefault("--album"), duration, values.GetValueOrDefault("--songmid"), flags.Contains("--preview"), values.GetValueOrDefault("--output"));
    }
}

internal static class Protocol
{
    internal static string Normalize(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ").ToUpperInvariant();
    internal static string Utf8(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);
    internal static void CheckHttp(HttpStatusCode status)
    {
        if ((int)status < 200 || (int)status >= 300) throw new ProbeException($"HTTP {(int)status}；不跟随重定向。");
    }

    internal static void Codes(JsonElement element, bool required = false)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ProbeException("响应层级不是对象。");
        if (required && !element.TryGetProperty("code", out _)) throw new ProbeException("缺少业务 code。");
        // 检查所有实际出现的嵌套 code，缺失的可选 code 不臆造为接口事实。
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("code") && (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int code) || code != 0))
                throw new ProbeException("业务 code 非零或格式错误。");
            if (property.Value.ValueKind == JsonValueKind.Object) Codes(property.Value);
            else if (property.Value.ValueKind == JsonValueKind.Array)
                foreach (var child in property.Value.EnumerateArray())
                    if (child.ValueKind == JsonValueKind.Object) Codes(child);
        }
    }

    internal static JsonElement Object(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new ProbeException("缺少对象：" + name);
        return value;
    }
    internal static string? String(JsonElement parent, string name) => parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static List<Song> Candidates(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Codes(doc.RootElement, true);
        var song = Object(Object(doc.RootElement, "data"), "song");
        if (!song.TryGetProperty("itemlist", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new ProbeException("缺少搜索列表。");
        var result = new List<Song>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var mid = String(item, "mid");
            if (!string.IsNullOrWhiteSpace(mid)) result.Add(new(mid, String(item, "name") ?? "", String(item, "singer") ?? ""));
        }
        return result;
    }

    internal static List<Song> Search(string json, string title, string artist)
    {
        using var doc = JsonDocument.Parse(json);
        Codes(doc.RootElement, true);
        var song = Object(Object(doc.RootElement, "data"), "song");
        if (!song.TryGetProperty("itemlist", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new ProbeException("缺少搜索列表。");
        var found = new List<Song>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new ProbeException("搜索候选格式错误。");
            string? mid = String(item, "mid"), name = String(item, "name"), singer = String(item, "singer");
            if (string.IsNullOrWhiteSpace(mid) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(singer)) continue;
            if (Normalize(name) == Normalize(title) && Normalize(singer) == Normalize(artist)) found.Add(new(mid, name, singer));
        }
        // 完全相同的重复项不构成多个版本；相同 MID 的矛盾元数据拒绝。
        var distinct = found.Distinct().ToList();
        if (distinct.GroupBy(s => s.Mid).Any(g => g.Count() > 1)) throw new ProbeException("同一 MID 的搜索元数据矛盾。");
        return distinct;
    }

    internal static VerifiedLyrics Lyrics(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Codes(doc.RootElement, true);
        var request = Object(doc.RootElement, "req_1");
        Codes(request, true);
        var data = Object(request, "data");
        foreach (var p in data.EnumerateObject())
        {
            string key = p.Name.ToLowerInvariant();
            if (key.Contains("encrypt", StringComparison.Ordinal) || key is "crypt" or "qrc" or "is_qrc" or "isqrc")
            {
                bool inactive = p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.False
                    || (p.Value.ValueKind == JsonValueKind.String && (p.Value.GetString() is "" or "0"))
                    || (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int n) && n == 0);
                if (!inactive) throw new ProbeException("响应带加密/QRC 数据或标志，拒绝处理。");
            }
        }
        string raw = String(data, "lyric") ?? throw new ProbeException("缺少歌词。");
        if (string.IsNullOrWhiteSpace(raw)) throw new NoLyricsException("无歌词。");
        string text = raw.TrimStart('\uFEFF').Trim();
        if (!text.StartsWith('['))
        {
            try { text = Utf8(Convert.FromBase64String(text)).TrimStart('\uFEFF').Trim(); }
            catch (FormatException) { throw new ProbeException("不是明文 LRC 或普通 Base64。"); }
            catch (DecoderFallbackException) { throw new ProbeException("Base64 内容不是严格 UTF-8。"); }
        }
        var parsed = LrcParser.Parse(text);
        if (parsed.Errors.Count != 0 || !parsed.Document.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Text)))
            throw new ProbeException($"非有效普通 LRC（解析错误 {parsed.Errors.Count}）。");
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, @"(?m)^\s*\[(ti|ar|al|by|re|ve|length|au|offset):([^\]\r\n]*)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            metadata[match.Groups[1].Value] = match.Groups[2].Value;
        return new(text, parsed.Document, metadata);
    }
}
