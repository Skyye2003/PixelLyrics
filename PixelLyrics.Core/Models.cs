using System.Security.Cryptography;
using System.Text;

namespace PixelLyrics.Core;

public sealed record SongIdentity
{
    public string Title { get; }
    public string Artist { get; }
    public string Album { get; }
    public TimeSpan Duration { get; }
    public string Id { get; }

    public SongIdentity(string title, string artist, string album, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(artist);
        ArgumentNullException.ThrowIfNull(album);
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        Title = title;
        Artist = artist;
        Album = album;
        Duration = duration;
        // 长度前缀 UTF-8 字段和时长 ticks 防止字符串拼接歧义。
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(title);
            writer.Write(artist);
            writer.Write(album);
            writer.Write(duration.Ticks);
        }
        Id = Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }
}

public sealed record LyricsBinding(string SongId, string LyricsPath);

public sealed record PixelLyricsSettings
{
    // 正值让歌词延后显示。
    public TimeSpan GlobalOffset { get; init; } = TimeSpan.Zero;
    public IReadOnlyList<LyricsBinding> Bindings { get; init; } = Array.Empty<LyricsBinding>();
}
