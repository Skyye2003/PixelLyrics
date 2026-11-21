using System.Text;
using System.Text.RegularExpressions;

namespace PixelLyrics.OnlineProbe;

public enum MatchCategory { Accepted, NoCandidates, NoMatch, BelowThreshold, Ambiguous, InvalidMetadata, TitleConflict, VersionConflict, ArtistConflict, DurationConflict }
public sealed record SongScore(SongDetails Details, double Score, MatchCategory Category, string Reason);
public sealed record SongSelection(MatchCategory Category, SongScore? Winner, IReadOnlyList<SongScore> Ranked);

public static class SongMatcher
{
    public const double MinimumScore = 88;
    public const double MinimumLead = 5;
    public const double ReleaseTagMaximumDurationDeltaSeconds = 5;

    // Manual confirmation supplements metadata; it never overrides a conflict or missing recording identity.
    public static SongScore EvaluateManual(SongDetails details, string title, string artist, string? album, double seconds)
    {
        var score = Evaluate(details, title, artist, album, seconds, allowArtistSubset: true);
        if (score.Category != MatchCategory.Accepted) return score;
        bool live = Title(title).Versions.Split('/').Contains("LIVE")
            || Regex.IsMatch(album ?? "", Versions[0].Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!live) return score;
        string identity = Key(album ?? "");
        bool specific = Regex.IsMatch(album ?? "", @"演唱会|演唱會|巡回|巡迴|音乐会|音樂會|CONCERT|TOUR",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string recording = Regex.Replace(album ?? "", @"演唱会|演唱會|巡回|巡迴|音乐会|音樂會|CONCERT|TOUR|LIVE|现场|現場", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        specific &= Key(recording).Length >= 4;
        if (!specific || identity != Key(details.Album))
            return new(details, 0, MatchCategory.VersionConflict, "缺少一致的具体演出或录音版本信息");
        if (Math.Abs(seconds - details.Interval) > ReleaseTagMaximumDurationDeltaSeconds)
            return new(details, 0, MatchCategory.DurationConflict, "同一录音版本要求时长差不超过 5 秒");
        return score;
    }

    public static SongScore EvaluateTrial(SongDetails details, string title, string artist, string? album, double seconds)
    {
        var expected = Title(title); var actual = Title(details.Title);
        SongScore Reject(MatchCategory category, string reason) => new(details, 0, category, reason);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(details.Title)) return Reject(MatchCategory.InvalidMetadata, "缺少歌名，不能试用");
        bool Live(string name, string? record) => Regex.IsMatch(name + " " + record, Versions[0].Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool expectedLive = Live(title, album), actualLive = Live(details.Title, details.Album);
        if (expectedLive != actualLive) return Reject(MatchCategory.VersionConflict, "录音室和现场版本不能混用");
        // Non-live manual trials leave title, credit and arrangement differences to listening confirmation.
        if (expectedLive && (expected.Body != actual.Body || expected.Versions != actual.Versions))
            return Reject(MatchCategory.VersionConflict, "现场录音版本冲突");
        if (!double.IsFinite(seconds) || !double.IsFinite(details.Interval) || seconds <= 0 || details.Interval <= 0 || Math.Abs(seconds - details.Interval) > 5)
            return Reject(MatchCategory.DurationConflict, "时长差超过 5 秒，不能试用");
        if (expectedLive && !string.IsNullOrWhiteSpace(album) && !string.IsNullOrWhiteSpace(details.Album) && Key(album) != Key(details.Album))
            return Reject(MatchCategory.VersionConflict, "演出或录音专辑不同，不能试用");
        return new(details, 100, MatchCategory.Accepted, "试用后确认时间轴");
    }

    private static string Fold(string value) => value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    private static string Key(string value) => string.Concat(Fold(value).EnumerateRunes()
        .Where(Rune.IsLetterOrDigit).Select(r => r.ToString()));
    private static readonly Regex Brackets = new(@"\(([^()]*)\)|\[([^\[\]]*)\]|【([^【】]*)】", RegexOptions.CultureInvariant);
    private static readonly (string Tag, string Pattern)[] Versions =
    [
        ("LIVE", @"\bLIVE\b|现场|現場"),
        ("INSTRUMENTAL", @"\bINSTRUMENTAL\b|\bKARAOKE\b|伴奏|纯音乐|純音樂|OFF[\s-]*VOCAL"),
        ("REMIX", @"\bREMIX\b|混音"),
        ("RERECORDED", @"\bRE[\s-]*RECORDED\b|\bRERECORDING\b|重录|重錄|重新录制|重新錄製"),
        ("ACOUSTIC", @"\bACOUSTIC\b|不插电|不插電"),
        ("DEMO", @"\bDEMO\b"),
        ("REMASTER", @"\bREMASTER(?:ED)?\b|重制|重製"),
        ("EDIT", @"\bRADIO[\s-]*EDIT\b|\bEXTENDED\b|\bTV[\s-]*(?:SIZE|VERSION)\b|剪辑版|剪輯版")
    ];

    private static (string Body, string Versions, string ReleaseTags) Title(string value)
    {
        string folded = Fold(value);
        var releaseTags = new HashSet<string>(StringComparer.Ordinal);
        // 仅识别明确括号内由分隔符隔开的完整 token，不删除主体或未知附加词。
        folded = Brackets.Replace(folded, match =>
        {
            string content = match.Groups.Cast<Group>().Skip(1).Single(g => g.Success).Value;
            var tokens = Regex.Split(content, @"[|/;,、]");
            var retained = tokens.Where(token =>
            {
                if (token.Trim() != "典藏") return true;
                releaseTags.Add("典藏");
                return false;
            });
            return "(" + string.Join("|", retained) + ")";
        });
        var tags = Versions.Where(v => Regex.IsMatch(folded, v.Pattern, RegexOptions.CultureInvariant))
            .Select(v => v.Tag).OrderBy(v => v, StringComparer.Ordinal).ToArray();
        // 歌名括号内的地点、年份及未识别版本仍属于主体，绝不全局剥括号。
        foreach (var v in Versions) folded = Regex.Replace(folded, v.Pattern, "", RegexOptions.CultureInvariant);
        if (tags.Any()) folded = Regex.Replace(folded, @"\b(?:VERSION|VER)\b|版本|版", "", RegexOptions.CultureInvariant);
        return (Key(folded), string.Join("/", tags), string.Join("/", releaseTags.OrderBy(t => t, StringComparer.Ordinal)));
    }

    internal static string RecallBaseTitle(string value)
    {
        string body = Brackets.Replace(value.Normalize(NormalizationForm.FormKC), " ");
        foreach (var version in Versions)
            body = Regex.Replace(body, version.Pattern, " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        body = Regex.Replace(body, @"\b(?:VERSION|VER)\b|版本|版", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        body = Regex.Replace(body, @"\s+", " ").Trim(' ', '-', '—');
        return string.IsNullOrWhiteSpace(body) ? value : body;
    }

    private static List<HashSet<string>> Artists(string value)
    {
        string folded = Fold(value);
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < folded.Length; i++)
        {
            char c = folded[i];
            if (c is '(' or '[' or '【') depth++;
            if (c is ')' or ']' or '】') depth--;
            if (depth == 0 && c is '/' or '、' or ',' or ';' or '&' or '＆')
            {
                parts.Add(folded[start..i]); start = i + 1;
            }
        }
        parts.Add(folded[start..]);
        return parts.Select(p =>
        {
            var names = Brackets.Matches(p).SelectMany(m => m.Groups.Cast<Group>().Skip(1)
                .Where(g => g.Success).Select(g => Key(g.Value))).ToHashSet(StringComparer.Ordinal);
            names.Add(Key(Brackets.Replace(p, "")));
            names.Remove("");
            return names;
        }).Where(p => p.Count > 0).ToList();
    }

    private static bool SameArtists(string expected, IReadOnlyList<string> actual, bool allowSubset = false)
    {
        var left = Artists(expected);
        var right = actual.SelectMany(Artists).ToList();
        if (left.Count == 0 || right.Count > 20 || (allowSubset ? left.Count >= right.Count : left.Count != right.Count)) return false;
        // 一对一匹配，避免共享别名或重复歌手被重复使用。
        bool Pair(int index, HashSet<int> used)
        {
            if (index == left.Count) return true;
            for (int i = 0; i < right.Count; i++)
                if (!used.Contains(i) && left[index].Overlaps(right[i]))
                {
                    used.Add(i);
                    if (Pair(index + 1, used)) return true;
                    used.Remove(i);
                }
            return false;
        }
        return left.Count <= 20 && Pair(0, []);
    }

    public static bool IsPlausibleCandidate(Song candidate, string title) =>
        string.IsNullOrWhiteSpace(candidate.Name) || Title(candidate.Name).Body == Title(title).Body;

    public static SongScore Evaluate(SongDetails details, string title, string artist, string? album,
        double seconds, string? artistAlias = null, bool allowArtistSubset = false)
    {
        bool releaseDifference = false;
        SongScore Reject(MatchCategory category, string reason) => new(details, 0, category,
            reason + (releaseDifference ? "；发行标签差异未获接受：必须歌名主体、重要版本、歌手一致，双方专辑非空且一致，时长差不超过 "
                + ReleaseTagMaximumDurationDeltaSeconds + "s" : ""));
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)
            || string.IsNullOrWhiteSpace(details.Title) || details.Singers.Count == 0
            || details.Singers.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(details.Album)
            || !double.IsFinite(seconds) || seconds <= 0 || !double.IsFinite(details.Interval) || details.Interval <= 0)
            return Reject(MatchCategory.InvalidMetadata, "元数据不完整");
        var expected = Title(title); var actual = Title(details.Title);
        releaseDifference = expected.ReleaseTags != actual.ReleaseTags;
        if (expected.Body.Length == 0 || expected.Body != actual.Body)
            return Reject(MatchCategory.TitleConflict, "歌名主体冲突");
        if (expected.Versions != actual.Versions)
            return Reject(MatchCategory.VersionConflict, "重要版本冲突（含一方缺省）");
        double delta = Math.Abs(seconds - details.Interval);
        bool sameAlbum = !string.IsNullOrWhiteSpace(album) && Key(album).Length > 0 && Key(album) == Key(details.Album);
        bool partialArtists = !SameArtists(artistAlias ?? artist, details.Singers);
        if (partialArtists)
        {
            if (!allowArtistSubset || !SameArtists(artistAlias ?? artist, details.Singers, allowSubset: true))
                return Reject(MatchCategory.ArtistConflict, "歌手身份冲突");
            // A shorter playback artist list requires stronger recording metadata.
            if (!sameAlbum || delta > ReleaseTagMaximumDurationDeltaSeconds || Key(title) != Key(details.Title))
                return Reject(MatchCategory.InvalidMetadata, "当前播放器的歌手名单可能不完整，缺少一致的完整版本、专辑或时长证据");
        }
        if (releaseDifference && !sameAlbum)
            return Reject(MatchCategory.TitleConflict, "发行标签差异拒绝：双方专辑必须非空且一致（当前专辑不同或未知）");
        if (releaseDifference && delta > ReleaseTagMaximumDurationDeltaSeconds)
            return Reject(MatchCategory.DurationConflict, $"发行标签差异拒绝：时长差 {delta:F3}s 超过 {ReleaseTagMaximumDurationDeltaSeconds}s");
        if (delta > Math.Max(20, seconds * .12))
            return Reject(MatchCategory.DurationConflict, "时长差距过大");
        double durationScore = 20 * Math.Max(0, 1 - Math.Max(0, delta - 1) / 14);
        double albumScore = sameAlbum ? 5 : 0;
        double score = 75 + durationScore + albumScore;
        return new(details, score, score >= MinimumScore ? MatchCategory.Accepted : MatchCategory.BelowThreshold,
            $"综合评分 {score:F2}/100；歌名主体及版本一致；{(partialArtists ? "播放歌手名单为候选子集，完整版本、专辑及时长已核验" : "歌手身份一致")}；专辑{(albumScore > 0 ? "一致" : "不同或未知（软评分）")}；时长差 {delta:F3}s"
            + (releaseDifference ? $"；发行标签差异接受：歌名主体、重要版本及歌手一致，双方专辑非空且一致，时长差 ≤ {ReleaseTagMaximumDurationDeltaSeconds}s" : "")
            + (artistAlias is null ? "" : "；采用显式歌手别名"));
    }

    public static SongSelection Rank(IEnumerable<SongDetails> candidates, string title, string artist,
        string? album, double seconds, string? artistAlias = null)
    {
        var all = candidates.GroupBy(c => c.Mid, StringComparer.Ordinal)
            .Select(g => g.Distinct().Count() == 1 ? Evaluate(g.First(), title, artist, album, seconds, artistAlias)
                : new SongScore(g.First(), 0, MatchCategory.InvalidMetadata, "同一 MID 元数据矛盾"))
            .OrderByDescending(s => s.Score).ThenBy(s => s.Details.Mid, StringComparer.Ordinal).ToArray();
        if (all.Length == 0) return new(MatchCategory.NoCandidates, null, all);
        var viable = all.Where(s => s.Category is MatchCategory.Accepted or MatchCategory.BelowThreshold).ToArray();
        if (viable.Length == 0) return new(all.Select(s => s.Category).Distinct().Count() == 1
            ? all[0].Category : MatchCategory.NoMatch, null, all);
        if (viable[0].Score < MinimumScore) return new(MatchCategory.BelowThreshold, null, all);
        if (viable.Length > 1 && viable[0].Score - viable[1].Score < MinimumLead)
            return new(MatchCategory.Ambiguous, null, all);
        return new(MatchCategory.Accepted, viable[0], all);
    }
}

public sealed class CandidateMetadataException(string message) : Exception(message);
public sealed class NoLyricsException(string message) : Exception(message);
