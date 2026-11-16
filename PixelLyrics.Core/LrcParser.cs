using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PixelLyrics.Core;

public sealed record LrcParseError(int LineNumber, string Source, string Message);

public sealed record LrcParseResult(LyricsDocument Document, IReadOnlyList<LrcParseError> Errors);

public static class LrcParser
{
    private static readonly Regex Timestamp = new(
        @"^(?<minutes>\d+):(?<seconds>\d{2})(?:[.:](?<fraction>\d{1,7}))?$",
        RegexOptions.CultureInvariant);

    public static LrcParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var reader = new StringReader(text.TrimStart('\uFEFF'));
        return Parse(reader);
    }

    public static LrcParseResult Parse(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 1024, leaveOpen);
        return Parse(reader);
    }

    public static LrcParseResult Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var lines = new List<(TimeSpan Time, string Text, int Number, string Source)>();
        var errors = new List<LrcParseError>();
        long offsetTicks = 0;
        int number = 0;
        string? source;
        while ((source = reader.ReadLine()) is not null)
        {
            number++;
            string input = source.TrimStart('\uFEFF').Trim();
            if (input.Length == 0) continue;
            int cursor = 0;
            var times = new List<TimeSpan>();
            bool hadTag = false;
            while (cursor < input.Length && input[cursor] == '[')
            {
                hadTag = true;
                int close = input.IndexOf(']', cursor + 1);
                if (close < 0)
                {
                    errors.Add(new(number, source, "标签缺少右方括号。"));
                    cursor = input.Length;
                    break;
                }
                string tag = input[(cursor + 1)..close];
                // Bracketed prose after a timestamp belongs to the lyric, not the tag list.
                // Keep malformed time-like tags subject to validation.
                if (times.Count > 0 && !TryTimestamp(tag, out _) && !IsMetadata(tag)
                    && !tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase)
                    && !Regex.IsMatch(tag, @"^\d+:", RegexOptions.CultureInvariant))
                    break;
                cursor = close + 1;
                if (tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(tag[7..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long milliseconds)
                        && milliseconds >= long.MinValue / TimeSpan.TicksPerMillisecond
                        && milliseconds <= long.MaxValue / TimeSpan.TicksPerMillisecond)
                        offsetTicks = milliseconds * TimeSpan.TicksPerMillisecond;
                    else errors.Add(new(number, source, "offset 必须是有效的有符号毫秒整数。"));
                }
                else if (TryTimestamp(tag, out var time))
                    times.Add(time);
                else if (!IsMetadata(tag))
                    errors.Add(new(number, source, $"无效标签：[{tag}]。"));
            }
            if (!hadTag)
                errors.Add(new(number, source, "缺少时间标签。"));
            string content = input[cursor..];
            foreach (var time in times) lines.Add((time, content, number, source));
        }

        var adjusted = new List<LyricLine>();
        foreach (var line in lines)
        {
            try
            {
                // LRC offset 为正时，歌词相对于播放位置提前。
                adjusted.Add(new(TimeSpan.FromTicks(checked(line.Time.Ticks - offsetTicks)), line.Text));
            }
            catch (OverflowException)
            {
                errors.Add(new(line.Number, line.Source, "应用 offset 后时间超出范围。"));
            }
        }
        return new(new LyricsDocument(adjusted), errors.AsReadOnly());
    }

    private static bool IsMetadata(string tag)
    {
        int colon = tag.IndexOf(':');
        if (colon <= 0) return false;
        return tag[..colon].ToLowerInvariant() is "ar" or "ti" or "al" or "by" or "re" or "ve" or "length" or "au";
    }

    private static bool TryTimestamp(string tag, out TimeSpan time)
    {
        time = default;
        var match = Timestamp.Match(tag);
        if (!match.Success
            || !long.TryParse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture, out long minutes)
            || !int.TryParse(match.Groups["seconds"].Value, out int seconds)
            || seconds >= 60)
            return false;
        string fraction = match.Groups["fraction"].Value;
        long fractionTicks = fraction.Length == 0 ? 0 : long.Parse(fraction.PadRight(7, '0'), CultureInfo.InvariantCulture);
        try
        {
            time = TimeSpan.FromTicks(checked(minutes * TimeSpan.TicksPerMinute + seconds * TimeSpan.TicksPerSecond + fractionTicks));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
