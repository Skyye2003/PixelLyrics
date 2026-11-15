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
        var iterator = StringInfo.GetTextElementEnumerator(text);
        while (iterator.MoveNext())
        {
            string element = iterator.GetTextElement();
            if (element.EnumerateRunes().Any(Rune.IsControl)) continue;
            int size = Encoding.UTF8.GetByteCount(element);
            if (size > TextReportEncoder.MaxUtf8Bytes) { element = "�"; size = 3; }
            int units = element.EnumerateRunes().Any(r => r.Value > 0xFF) ? 2 : 1;
            if (current.Length > 0 && (width + units > widthUnits || bytes + size > TextReportEncoder.MaxUtf8Bytes))
            {
                pages.Add(current.ToString());
                current.Clear(); width = 0; bytes = 0;
            }
            current.Append(element); width += units; bytes += size;
        }
        if (current.Length > 0) pages.Add(current.ToString());
        return pages;
    }
}
