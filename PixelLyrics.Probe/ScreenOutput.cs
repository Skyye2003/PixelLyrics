using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PixelBar.Sdk;
using PixelBar.Sdk.Devices;
using PixelBar.Sdk.Protocol;

namespace PixelLyrics.Probe;

internal sealed class ScreenOutput
{
    private readonly Action<string, byte[]> send;
    private string? path;
    internal bool ReconnectEnabled { get; private set; }
    internal bool Ready => path is not null && !HasFailed;
    internal string ConnectionStatus { get; private set; } = "等待连接 USB 歌词屏";
    private TimeSpan nextScan;
    private TimeSpan candidateSince;
    private string? candidate;
    private string? failedPath;
    private int failures;
    private int deviceRevision = -1;
    internal static ScreenOutput Waiting(Action<string, byte[]>? transport = null) => new(null, transport) { ReconnectEnabled = true };
    internal bool RefreshConnection(TimeSpan now, int revision)
    {
        if (!ReconnectEnabled) return Ready;
        if (revision == deviceRevision && now < nextScan) return Ready;
        deviceRevision = revision;
        string[] paths;
        try { paths = PixelBarClient.ListDevices().Where(IsStrict).Select(d => d.DevicePath).ToArray(); }
        catch { paths = []; }
        bool ready = ObserveDevices(paths, now);
        nextScan = now + TimeSpan.FromSeconds(candidate is not null && !ready ? 1 : 5);
        return ready;
    }
    internal bool ObserveDevices(string[] paths, TimeSpan now)
    {
        string? unique = paths.Length == 1 ? paths[0] : null;
        if (failedPath is not null && !paths.Contains(failedPath)) { failedPath = null; failures = 0; }
        if (unique == path && Ready) return true;
        path = null; lastText = null; ownsLyrics = false; HasDisplayableLyrics = false; ClockRequested = false;
        if (unique is null)
        {
            candidate = null;
            ConnectionStatus = paths.Length > 1 ? "检测到多个歌词屏，请仅连接一个" : "歌词屏已断开，等待连接";
            return false;
        }
        if (unique == failedPath && failures >= 2)
        {
            ConnectionStatus = "USB 连续写入失败，请拔插歌词屏后恢复";
            return false;
        }
        if (candidate != unique) { candidate = unique; candidateSince = now; }
        if (now - candidateSince < TimeSpan.FromSeconds(1))
        { ConnectionStatus = "正在确认歌词屏连接"; return false; }
        path = unique; HasFailed = false; candidate = null;
        ConnectionStatus = "歌词屏已连接";
        return true;
    }
    private void Failed(Exception ex)
    {
        failedPath = path; failures++; HasFailed = true; candidate = null;
        ConnectionStatus = "USB 写入失败，正在重新校验连接；实际显示未知";
        Program.ReportDesktopLog($"USB 写入失败：{ex.GetType().Name}；连续失败 {failures} 次；实际显示未知。");
    }
    private void WriteText(string text, TextDisplayEffect effect)
    {
        if (path is null) throw new InvalidOperationException();
        send(path, effect == TextDisplayEffect.Static
            ? TextLayoutReportEncoder.EncodeStatic(PixelTextAlignment.Left)
            : TextLayoutReportEncoder.EncodeScroll(TextScrollDirection.RightToLeft));
        send(path, TextReportEncoder.Encode(text));
    }
    public bool HasFailed { get; private set; }
    public int SuccessfulWrites { get; private set; }
    private string? lastText;
    private TextDisplayEffect lastEffect;
    private string? lastLineKey;
    private TimeSpan lastElapsed;
    private bool wasPaused;
    private bool ownsLyrics;
    internal bool HasDisplayableLyrics { get; private set; }
    internal bool ClockRequested { get; private set; }
    internal int DisplayWidthUnits { get; set; } = 32;
    internal int PageHoldMs { get; set; } = 2800;

    public bool ShowClock(int style)
    {
        // Validate before transport; switching styles does not set the device time.
        PatternReportEncoder.Encode(style);
        if (!Ready) return false;
        try
        {
            send(path!, PatternReportEncoder.Encode(style));
            SuccessfulWrites++;
            ownsLyrics = false;
            ClockRequested = true;
            HasDisplayableLyrics = false;
            lastText = null;
            lastLineKey = null;
            return true;
        }
        catch (Exception ex)
        {
            Failed(ex);
            return false;
        }
    }

    private ScreenOutput(string? path, Action<string, byte[]>? transport = null) { this.path = path; send = transport ?? BoundedHidTransport.Send; }

    public static ScreenOutput? Enumerate(bool enableOutput)
    {
        try
        {
            var devices = PixelBarClient.ListDevices();
            Console.WriteLine($"SDK 屏幕端点：{devices.Count}（枚举会打开 HID 查询句柄，不发送文字）");
            foreach (var device in devices)
                Console.WriteLine($"  {device.DisplayName}\n  {device.DevicePath}\n  UsagePage={device.UsagePage:X4} Usage={device.Usage:X4} Input={device.InputReportLength} Output={device.OutputReportLength}");
            if (!enableOutput) return null;
            var strict = devices.Where(IsStrict).ToArray();
            if (strict.Length != 1)
            {
                Console.Error.WriteLine($"严格匹配设备为 {strict.Length} 个，拒绝 USB 输出。");
                return null;
            }
            Console.WriteLine($"已显式启用 USB，固定绑定：{strict[0].DevicePath}");
            return new ScreenOutput(strict[0].DevicePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"设备枚举失败，不启用 USB：{ex.GetType().Name}");
            return null;
        }
    }

    private static bool IsStrict(HidEndpoint endpoint)
    {
        // SDK 内部使用 Contains；本层额外要求完整硬件 ID token，防止前缀误匹配。
        string pattern = $@"(?:^|[#&\\])vid_{PixelBarUsbIds.VendorId:x4}(?=[&#\\]|$)";
        string product = $@"(?:^|[#&\\])pid_{PixelBarUsbIds.ProductId:x4}(?=[&#\\]|$)";
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        return Regex.IsMatch(endpoint.DevicePath, pattern, options)
            && Regex.IsMatch(endpoint.DevicePath, product, options)
            && Regex.IsMatch(endpoint.DevicePath, @"(?:^|[#&\\])col02(?=[&#\\]|$)", options)
            && (endpoint.UsagePage == PixelBarUsbIds.UsagePage || endpoint.UsagePage == 0xFF14)
            && endpoint.Usage == 1
            && endpoint.DevicePath.Contains("mi_04", StringComparison.OrdinalIgnoreCase)
            && endpoint.InputReportLength == 64 && endpoint.OutputReportLength == 64;
    }

    public bool Test()
    {
        Console.WriteLine("将发送固定测试文字，请肉眼确认屏幕内容；发送成功不等于显示正确或 P0 验证通过。");
        if (!Send("PixelLyrics 测试"))
        {
            Console.Error.WriteLine("固定文字测试发送失败；退出码 5，不代表测试通过。");
            return false;
        }
        Console.WriteLine("SDK 调用已返回，显示效果尚需人工确认。");
        return true;
    }

    public async Task TestNativeScrollAsync(CancellationToken token)
    {
        const string text = "原生滚动测试一二三四五六七八九十";
        try
        {
            token.ThrowIfCancellationRequested();
            WriteText(text, TextDisplayEffect.Scroll);
            ownsLyrics = true;
            lastText = null;
            Console.WriteLine("原生向左滚动测试已发送一次，保持12秒；没有循环重发文字。请观察完整文字及两侧显示。");
            await Task.Delay(TimeSpan.FromSeconds(12), token);
        }
        finally
        {
            ClearLyrics();
        }
    }

    public void ShowLyric(string? text, TimeSpan? lineElapsed = null, bool paused = false, string? lineKey = null)
    {
        var elapsed = lineElapsed ?? TimeSpan.Zero;
        var frame = LyricDisplayPlan.Create(text ?? string.Empty, elapsed, paused, DisplayWidthUnits, PageHoldMs);
        if (string.IsNullOrWhiteSpace(frame.Text))
        {
            ClearLyrics();
            return;
        }
        bool restart = lastLineKey != lineKey || wasPaused != paused
            || elapsed < lastElapsed - TimeSpan.FromMilliseconds(500)
            || elapsed > lastElapsed + TimeSpan.FromSeconds(2);
        if (Send(frame.Text, frame.Effect, restart))
        {
            ownsLyrics = true;
            ClockRequested = false;
        }
        HasDisplayableLyrics = !HasFailed && !string.IsNullOrWhiteSpace(frame.Text);
        lastLineKey = lineKey;
        lastElapsed = elapsed;
        wasPaused = paused;
    }

    public void ClearLyrics()
    {
        HasDisplayableLyrics = false;
        if (!ownsLyrics) return;
        // SDK 拒绝空串，以一个空格替换旧歌词；固件是否真正清屏仍需人工确认。
        if (Send(" ")) ownsLyrics = false;
        lastLineKey = null;
        lastElapsed = TimeSpan.Zero;
        wasPaused = false;
    }

    private bool Send(string text, TextDisplayEffect effect = TextDisplayEffect.Static, bool force = false)
    {
        if (!Ready || (!force && text == lastText && effect == lastEffect)) return false;
        try
        {
            WriteText(text, effect);
            SuccessfulWrites++;
            Console.WriteLine($"USB 写入完成 #{SuccessfulWrites}，UTF-8字节数={Encoding.UTF8.GetByteCount(text)}（不代表屏幕可见，需人工确认）。");
            lastText = text;
            lastEffect = effect;
            return true;
        }
        catch (Exception ex)
        {
            Failed(ex);
            return false;
        }
    }

    internal static string FitText(string text)
    {
        var result = new StringBuilder();
        int bytes = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (element.EnumerateRunes().Any(rune => Rune.IsControl(rune))) continue;
            int size = Encoding.UTF8.GetByteCount(element);
            if (bytes + size > TextReportEncoder.MaxUtf8Bytes) break;
            result.Append(element);
            bytes += size;
        }
        return result.ToString();
    }
}
