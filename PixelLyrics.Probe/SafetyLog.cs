using System.IO;
using System.Text;

namespace PixelLyrics.Probe;

internal static class SafetyLog
{
    internal const int MaximumBytes = 1024 * 1024;
    private static readonly object Sync = new();
    internal static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelLyrics", "Logs");
    internal static void Append(string message, string? directory = null)
    {
        lock (Sync)
        {
            try
            {
                directory ??= DirectoryPath;
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "safety.log");
                string line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] "
                    + new string(message.Take(400).Select(c => char.IsControl(c) ? ' ' : c).ToArray()) + Environment.NewLine;
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > MaximumBytes)
                {
                    string older = Path.Combine(directory, "safety.2.log");
                    string previous = Path.Combine(directory, "safety.1.log");
                    if (File.Exists(previous)) File.Move(previous, older, true);
                    File.Move(path, previous, true);
                }
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { /* Logging failure must not interrupt playback or hide the window log. */ }
        }
    }
}
