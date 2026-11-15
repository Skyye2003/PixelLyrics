using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PixelBar.Sdk;
using PixelBar.Sdk.Devices;
using PixelBar.Sdk.Protocol;

namespace PixelLyrics.Probe;

internal sealed class ScreenOutput
{
    private readonly PixelBarClient client;
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
        if (HasFailed) return false;
        try
        {
            client.ShowClock(style);
            SuccessfulWrites++;
            ownsLyrics = false;
            ClockRequested = true;
            HasDisplayableLyrics = false;
            lastText = null;
            lastLineKey = null;
            return true;
        }
        catch (Exception)
        {
            HasFailed = true;
            return false;
        }
    }

    private ScreenOutput(string path) => client = new PixelBarClient(path);

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
            client.ShowText(text, TextDisplayEffect.Scroll, PixelTextAlignment.Left, TextScrollDirection.RightToLeft);
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
        if (HasFailed || (!force && text == lastText && effect == lastEffect)) return false;
        try
        {
            client.ShowText(text, effect, PixelTextAlignment.Left, TextScrollDirection.RightToLeft);
            SuccessfulWrites++;
            Console.WriteLine($"USB 写入完成 #{SuccessfulWrites}，UTF-8字节数={Encoding.UTF8.GetByteCount(text)}（不代表屏幕可见，需人工确认）。");
            lastText = text;
            lastEffect = effect;
            return true;
        }
        catch (Exception ex)
        {
            HasFailed = true;
            Console.Error.WriteLine($"USB 输出异常（{ex.GetType().Name}），本次进程停止所有写屏，不重试、不重选设备；屏上可能仍有旧内容。");
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
