using System.Globalization;
using System.Text;
using PixelBar.Sdk.Protocol;

namespace PixelLyrics.Probe;

// Readable static pages, not firmware scrolling. Width remains an estimate, not pixel measurement.
internal static class LyricViewport
{
    internal const int WidthUnits = 32; // 16 CJK / approximately 32 ASCII, based on documented static text limit.
    internal const int HoldMs = 2800;

    internal static string Frame(string text, TimeSpan lineElapsed, int widthUnits = WidthUnits, int holdMs = HoldMs)
    {
        if (holdMs is < 500 or > 10000) throw new ArgumentOutOfRangeException(nameof(holdMs), "分页停留必须在 500..10000 毫秒范围内。");
        var pages = Pages(text, widthUnits);
        if (pages.Count == 0) return string.Empty;
        // No looping or blank gap: retain the last page until the next lyric line.
        int index = (int)Math.Min(pages.Count - 1, Math.Floor(Math.Max(0, lineElapsed.TotalMilliseconds) / holdMs));
        return pages[index];
    }

    internal static IReadOnlyList<string> Pages(string text, int widthUnits = WidthUnits)
    {
        if (widthUnits is < 8 or > 64) throw new ArgumentOutOfRangeException(nameof(widthUnits), "显示宽度必须在 8..64 单位范围内。");
        var pages = new List<string>();
        var current = new StringBuilder();
        int width = 0, bytes = 0;
        var elements = new List<string>();
        var iterator = StringInfo.GetTextElementEnumerator(text);
        while (iterator.MoveNext())
        {
            string element = iterator.GetTextElement();
            if (element.EnumerateRunes().Any(Rune.IsControl)) continue;
            int size = Encoding.UTF8.GetByteCount(element);
            if (size > TextReportEncoder.MaxUtf8Bytes) { element = "�"; size = 3; }
            elements.Add(element);
        }
        static bool Word(string element) => element.EnumerateRunes().First() is var rune
            && (Rune.IsDigit(rune) || Rune.IsLetter(rune) && rune.Value <= 0x024F);
        static bool Space(string element) => element.EnumerateRunes().All(Rune.IsWhiteSpace);
        static bool Connector(string element) => element is "'" or "’" or "-" or "‐" or "‑";
        static bool Closing(string element) => element is "," or "." or "!" or "?" or ":" or ";"
            or "，" or "。" or "！" or "？" or "：" or "；" or ")" or "]" or "）" or "”";
        static int Units(string element) => element.EnumerateRunes().Any(r => r.Value > 0xFF) ? 2 : 1;
        bool Fits(IEnumerable<string> part, int usedWidth = 0, int usedBytes = 0) =>
            usedWidth + part.Sum(Units) <= widthUnits
            && usedBytes + part.Sum(Encoding.UTF8.GetByteCount) <= TextReportEncoder.MaxUtf8Bytes;
        void Flush()
        {
            if (current.Length > 0) pages.Add(current.ToString());
            current.Clear(); width = 0; bytes = 0;
        }
        void Append(IEnumerable<string> part)
        {
            foreach (string element in part)
            {
                current.Append(element); width += Units(element); bytes += Encoding.UTF8.GetByteCount(element);
            }
        }
        var spaces = new List<string>();
        for (int i = 0; i < elements.Count;)
        {
            if (Space(elements[i])) { spaces.Add(elements[i++]); continue; }
            var token = new List<string> { elements[i++] };
            if (Word(token[0]))
                while (i < elements.Count && (Word(elements[i])
                    || Connector(elements[i]) && i + 1 < elements.Count && Word(elements[i + 1])))
                    token.Add(elements[i++]);
            while (i < elements.Count && Closing(elements[i])) token.Add(elements[i++]);
            var prefix = current.Length > 0 ? spaces : [];
            if (!Fits(prefix.Concat(token), width, bytes))
            {
                Flush();
                prefix = [];
            }
            Append(prefix);
            spaces.Clear();
            if (Fits(token)) { Append(token); continue; }
            // An oversized word must split; prefer the last hyphen that fits.
            while (token.Count > 0)
            {
                int count = 0;
                while (count < token.Count && Fits(token.Take(count + 1), width, bytes)) count++;
                if (count < token.Count)
                {
                    int hyphen = token.Take(count).ToList().FindLastIndex(e => e is "-" or "‐" or "‑");
                    if (hyphen >= 0) count = hyphen + 1;
                }
                Append(token.Take(count));
                token.RemoveRange(0, count);
                if (token.Count > 0) Flush();
            }
        }
        Flush();
        return pages;
    }
}
