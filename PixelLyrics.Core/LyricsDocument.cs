namespace PixelLyrics.Core;

public sealed record LyricLine(TimeSpan Timestamp, string Text);

public sealed class LyricsDocument
{
    public IReadOnlyList<LyricLine> Lines { get; }

    public LyricsDocument(IEnumerable<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        // LINQ 的稳定排序保留同时间歌词的输入顺序。
        Lines = Array.AsReadOnly(lines.OrderBy(line => line.Timestamp).ToArray());
    }

    // 同时间多行时返回输入顺序中的最后一行。
    public LyricLine? GetLineAt(TimeSpan position)
    {
        int low = 0, high = Lines.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (Lines[middle].Timestamp <= position)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found < 0 ? null : Lines[found];
    }
}
