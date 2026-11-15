using System.IO;
using System.Text.Json;
using PixelLyrics.Core;

namespace PixelLyrics.Probe;

internal sealed record ProbeSongBinding(string Mid, string? ArtistAlias = null);

internal sealed class ProbeSettings
{
    internal const int MaxFileBytes = 1024 * 1024;

    public int OffsetMs { get; set; }
    public int DisplayWidthUnits { get; set; } = 32;
    public int PageHoldMs { get; set; } = 2800;
    public int ClockStyle { get; set; } = 1;
    public bool IdleClockEnabled { get; set; } = true;
    public int IdleClockDelayMs { get; set; } = 5000;
    public bool TrackIntroEnabled { get; set; }
    public int TrackIntroHoldMs { get; set; } = 3000;
    public int NoLyricsTitleHoldMs { get; set; } = 5000;
    public int PollIntervalMs { get; set; } = 200;
    public int TrackSettleMs { get; set; } = 600;
    public int RequestTimeoutMs { get; set; } = 10000;
    public Dictionary<string, ProbeSongBinding> Bindings { get; } = new(StringComparer.Ordinal);

    public static ProbeSettings Load(string path)
    {
        string fullPath = LocalPath(path);
        if (!Directory.Exists(Path.GetDirectoryName(fullPath)))
            throw new DirectoryNotFoundException("配置父目录必须已存在。");
        using FileStream? stream = OpenOrMissing(fullPath);
        if (stream is null) return new();
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("配置超过 1MiB 上限。");
        using var data = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (data.Length + count > MaxFileBytes) throw new InvalidDataException("配置超过 1MiB 上限。");
            data.Write(buffer, 0, count);
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(data.ToArray());
            var settings = new ProbeSettings();
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!fields.Add(property.Name)) throw new InvalidDataException("配置字段重复。");
                switch (property.Name)
                {
                    case nameof(OffsetMs): settings.OffsetMs = property.Value.GetInt32(); break;
                    case nameof(DisplayWidthUnits): settings.DisplayWidthUnits = property.Value.GetInt32(); break;
                    case nameof(PageHoldMs): settings.PageHoldMs = property.Value.GetInt32(); break;
                    case nameof(ClockStyle): settings.ClockStyle = property.Value.GetInt32(); break;
                    case nameof(IdleClockEnabled): settings.IdleClockEnabled = property.Value.GetBoolean(); break;
                    case nameof(IdleClockDelayMs): settings.IdleClockDelayMs = property.Value.GetInt32(); break;
                    case nameof(TrackIntroEnabled): settings.TrackIntroEnabled = property.Value.GetBoolean(); break;
                    case nameof(TrackIntroHoldMs): settings.TrackIntroHoldMs = property.Value.GetInt32(); break;
                    case nameof(NoLyricsTitleHoldMs): settings.NoLyricsTitleHoldMs = property.Value.GetInt32(); break;
                    case nameof(PollIntervalMs): settings.PollIntervalMs = property.Value.GetInt32(); break;
                    case nameof(TrackSettleMs): settings.TrackSettleMs = property.Value.GetInt32(); break;
                    case nameof(RequestTimeoutMs): settings.RequestTimeoutMs = property.Value.GetInt32(); break;
                    case nameof(Bindings):
                        foreach (JsonProperty entry in property.Value.EnumerateObject())
                        {
                            string? mid = null, alias = null;
                            var bindingFields = new HashSet<string>(StringComparer.Ordinal);
                            foreach (JsonProperty field in entry.Value.EnumerateObject())
                            {
                                if (!bindingFields.Add(field.Name)) throw new InvalidDataException("绑定字段重复。");
                                switch (field.Name)
                                {
                                    case nameof(ProbeSongBinding.Mid): mid = field.Value.GetString(); break;
                                    case nameof(ProbeSongBinding.ArtistAlias): alias = field.Value.GetString(); break;
                                    default: throw new InvalidDataException("未知绑定字段。");
                                }
                            }
                            if (mid is null || !settings.Bindings.TryAdd(entry.Name, new(mid, alias)))
                                throw new InvalidDataException("绑定缺少 MID 或身份键重复。");
                        }
                        break;
                    default: throw new InvalidDataException("未知配置字段。");
                }
            }
            settings.Validate();
            return settings;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("配置 JSON 格式或字段类型非法。", exception);
        }
    }

    // 在任何联网/设备操作前调用；未显式指定 offset 时保留已加载值，不写入默认偏移。
    public static ProbeSettings LoadForRun(ProbeOptions options)
    {
        if (options.SaveSettings && options.SettingsPath is null)
            throw new ArgumentException("保存配置必须指定路径。", nameof(options));
        ProbeSettings settings = options.SettingsPath is null ? new() : Load(options.SettingsPath);
        if (options.HasOffset)
        {
            settings.OffsetMs = options.OffsetMs;
            settings.Validate();
            if (options.SaveSettings) settings.Save(options.SettingsPath!);
        }
        return settings;
    }

    // 调用方必须先完成当前完整歌曲身份与在线详情的验证，再调用本方法并按授权决定是否 Save。
    public void SetVerifiedBinding(SongIdentity song, string mid, string? artistAlias = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        ValidateBinding(song.Id, new(mid, artistAlias));
        Bindings[song.Id] = new(mid, artistAlias);
    }

    public void Save(string path)
    {
        Validate();
        string fullPath = LocalPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("配置父目录必须已存在。");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { OffsetMs, DisplayWidthUnits, PageHoldMs, ClockStyle, IdleClockEnabled, IdleClockDelayMs, TrackIntroEnabled, TrackIntroHoldMs, NoLyricsTitleHoldMs, PollIntervalMs, TrackSettleMs, RequestTimeoutMs, Bindings }, new JsonSerializerOptions { WriteIndented = true });
        if (json.Length > MaxFileBytes) throw new InvalidDataException("配置超过 1MiB 上限。");
        string temporary = Path.Combine(directory, ".probe-settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal void ApplyRuntimeOverrides(ProbeSettings overrides)
    {
        overrides.Validate();
        TrackIntroEnabled = overrides.TrackIntroEnabled;
        TrackIntroHoldMs = overrides.TrackIntroHoldMs;
        ClockStyle = overrides.ClockStyle;
        IdleClockEnabled = overrides.IdleClockEnabled;
        IdleClockDelayMs = overrides.IdleClockDelayMs;
        NoLyricsTitleHoldMs = overrides.NoLyricsTitleHoldMs;
        PollIntervalMs = overrides.PollIntervalMs;
        TrackSettleMs = overrides.TrackSettleMs;
        RequestTimeoutMs = overrides.RequestTimeoutMs;
    }

    private void Validate()
    {
        if (OffsetMs is < -5000 or > 5000) throw new InvalidDataException("偏移必须在 -5000..5000 毫秒范围内。");
        if (DisplayWidthUnits is < 8 or > 64) throw new InvalidDataException("显示宽度必须在 8..64 单位范围内。");
        if (PageHoldMs is < 500 or > 10000) throw new InvalidDataException("分页停留必须在 500..10000 毫秒范围内。");
        if (ClockStyle is < 1 or > 11) throw new InvalidDataException("时钟样式必须在 1..11 范围内。");
        if (IdleClockDelayMs is < 1000 or > 60000) throw new InvalidDataException("空闲回钟等待必须在 1000..60000 毫秒范围内。");
        if (TrackIntroHoldMs is < 1000 or > 15000) throw new InvalidDataException("新曲歌名开场必须在 1000..15000 毫秒范围内。");
        if (NoLyricsTitleHoldMs is < 1000 or > 60000) throw new InvalidDataException("无词歌名停留必须在 1000..60000 毫秒范围内。");
        if (PollIntervalMs is < 100 or > 1000) throw new InvalidDataException("会话轮询间隔必须在 100..1000 毫秒范围内。");
        if (TrackSettleMs is < 200 or > 5000) throw new InvalidDataException("曲目稳定等待必须在 200..5000 毫秒范围内。");
        if (RequestTimeoutMs is < 2000 or > 30000) throw new InvalidDataException("请求超时必须在 2000..30000 毫秒范围内。");
        foreach (var binding in Bindings) ValidateBinding(binding.Key, binding.Value);
    }

    private static void ValidateBinding(string key, ProbeSongBinding? binding)
    {
        if (key.Length != 64 || key.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("绑定键必须是完整 SongIdentity.Id（64 位十六进制）。");
        if (binding is null || string.IsNullOrWhiteSpace(binding.Mid)) throw new InvalidDataException("绑定 MID 不可为空。");
        if (binding.ArtistAlias is not null && string.IsNullOrWhiteSpace(binding.ArtistAlias))
            throw new InvalidDataException("显式歌手别名不可为空。");
    }

    private static string LocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("配置仅接受本地文件路径。", nameof(path));
        return Path.GetFullPath(path);
    }

    private static FileStream? OpenOrMissing(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
    }
}
