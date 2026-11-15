namespace PixelLyrics.OnlineProbe;

public interface ILyricsSource : IDisposable
{
    Task<SongSelection> RecallAsync(string title, string artist, string? album, double seconds,
        CancellationToken token = default, Action? ensureNetworkAllowed = null,
        string? requiredMid = null, string? artistAlias = null, Func<Task>? beforeRequest = null);
    Task<SongDetails> GetDetailsAsync(string id, CancellationToken cancellationToken = default);
    Task<VerifiedLyrics> FetchAsync(string id, CancellationToken cancellationToken = default);
}
