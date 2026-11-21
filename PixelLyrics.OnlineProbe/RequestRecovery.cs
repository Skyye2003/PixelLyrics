using System.Net;
using System.Text.Json;

namespace PixelLyrics.OnlineProbe;

public enum RequestStage { Search, Details, Lyrics }
public enum RequestFailureKind { Transient, RateLimited, AccessDenied, Protocol, Identity, Business, Http }

public sealed class LyricsRequestException(RequestFailureKind kind, RequestStage stage, string reason,
    HttpStatusCode? status = null) : ProbeException(reason)
{
    public RequestFailureKind Kind { get; } = kind;
    public RequestStage Stage { get; } = stage;
    public HttpStatusCode? Status { get; } = status;
}

public sealed record RequestNotice(RequestStage Stage, string Message, int Attempt = 0);

// One instance per actual source and run. Deadlines survive song/client changes.
public sealed class RequestRecovery
{
    private readonly TimeProvider clock;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly object sync = new();
    private long limitedAt;
    private TimeSpan limitDuration;
    private readonly SemaphoreSlim requests = new(1, 1);
    private int rateFailures;
    private bool denied;
    private readonly bool autoResume;
    public RequestRecovery(bool autoResume = false) : this(TimeProvider.System, (wait, token) => Task.Delay(wait, token), autoResume) { }
    internal RequestRecovery(TimeProvider clock, Func<TimeSpan, CancellationToken, Task> delay, bool autoResume = false)
    { this.clock = clock; this.delay = delay; this.autoResume = autoResume; }
    public bool AccessDenied { get { lock (sync) return denied; } }
    public TimeSpan Remaining
    {
        get { lock (sync) { var left = limitDuration - clock.GetElapsedTime(limitedAt); return left > TimeSpan.Zero ? left : TimeSpan.Zero; } }
    }
    public void RetryAccess() { lock (sync) denied = false; }

    private sealed class InvalidatedException() : OperationCanceledException;

    internal async Task<T> ExecuteAsync<T>(HttpClient client, Func<HttpRequestMessage> create,
        Func<string, T> parse, RequestStage stage, int timeoutMs, CancellationToken token,
        Func<CancellationToken, Task>? validate = null, Action<RequestNotice>? report = null)
    {
        await requests.WaitAsync(token);
        try { return await ExecuteCoreAsync(client, create, parse, stage, timeoutMs, token, validate, report); }
        finally { requests.Release(); }
    }

    internal async Task<T> ExecuteCoreAsync<T>(HttpClient client, Func<HttpRequestMessage> create,
        Func<string, T> parse, RequestStage stage, int timeoutMs, CancellationToken token,
        Func<CancellationToken, Task>? validate = null, Action<RequestNotice>? report = null)
    {
        int transientRetries = 0, protocolRetries = 0, attempts = 0;
        async Task Validate() { token.ThrowIfCancellationRequested(); if (validate is not null) { try { await validate(token); } catch (OperationCanceledException) { throw new InvalidatedException(); } } token.ThrowIfCancellationRequested(); }
        while (true)
        {
            await Validate();
            if (AccessDenied) throw new LyricsRequestException(RequestFailureKind.AccessDenied, stage, "接口访问受限，请重新尝试");
            if (Remaining > TimeSpan.Zero)
            {
                if (!autoResume) throw new LyricsRequestException(RequestFailureKind.RateLimited, stage, "接口限流，等待恢复");
                await delay(Remaining < TimeSpan.FromSeconds(1) ? Remaining : TimeSpan.FromSeconds(1), token);
                continue;
            }
            attempts++;
            LyricsRequestException failure;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(timeoutMs);
                using var request = create();
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token);
                await Validate();
                var status = response.StatusCode;
                if (status == HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter;
                    TimeSpan? serverWait = retry?.Delta ?? (retry?.Date - clock.GetUtcNow());
                    lock (sync)
                    {
                        var local = TimeSpan.FromSeconds(60 * Math.Pow(2, Math.Min(rateFailures++, 4)));
                        if (local > TimeSpan.FromMinutes(15)) local = TimeSpan.FromMinutes(15);
                        limitDuration = serverWait is { } value && value >= TimeSpan.Zero ? value : local;
                        limitedAt = clock.GetTimestamp();
                    }
                    throw new LyricsRequestException(RequestFailureKind.RateLimited, stage, "HTTP 429，请求受限", status);
                }
                if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    lock (sync) denied = true;
                    throw new LyricsRequestException(RequestFailureKind.AccessDenied, stage, $"HTTP {(int)status}，接口访问受限", status);
                }
                if ((int)status is < 200 or >= 300)
                    throw new LyricsRequestException(status is HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError
                        or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                        ? RequestFailureKind.Transient : RequestFailureKind.Http, stage, $"HTTP {(int)status}", status);
                var result = parse(Protocol.Utf8(await response.Content.ReadAsByteArrayAsync(deadline.Token)));
                await Validate();
                lock (sync) rateFailures = 0;
                if (attempts > 1) report?.Invoke(new(stage, "请求已恢复", attempts));
                return result;
            }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested && error is not InvalidatedException)
            { failure = new(RequestFailureKind.Transient, stage, "请求超时"); }
            catch (HttpRequestException) { failure = new(RequestFailureKind.Transient, stage, "网络连接失败"); }
            catch (LyricsRequestException error) { failure = error; }
            catch (ProbeException error)
            { failure = Classify(error, stage); }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
            { failure = new(RequestFailureKind.Protocol, stage, "接口响应格式或编码错误"); }
            await Validate();
            if (failure.Kind == RequestFailureKind.AccessDenied) { lock (sync) denied = true; }
            if (failure.Kind == RequestFailureKind.RateLimited && autoResume)
            {
                report?.Invoke(new(stage, $"请求受限，{Math.Ceiling(Remaining.TotalSeconds)} 秒后恢复", attempts));
                continue;
            }
            TimeSpan wait;
            if (failure.Kind == RequestFailureKind.Transient && transientRetries < 2)
                wait = TimeSpan.FromSeconds(++transientRetries == 1 ? 2 : 5);
            else if (failure.Kind == RequestFailureKind.Protocol && protocolRetries++ == 0)
                wait = TimeSpan.FromSeconds(2);
            else throw failure;
            if (attempts >= 3) throw failure;
            // Mixed failures share the same maximum of three total attempts.
            report?.Invoke(new(stage, $"{failure.Message}，{wait.TotalSeconds:0} 秒后重试", attempts));
            await delay(wait, token);
        }
    }

    private static LyricsRequestException Classify(ProbeException error, RequestStage stage)
    {
        if (error.Message is "网易云详情 ID 不一致。" or "详情 MID 与请求不一致。")
            return new(RequestFailureKind.Identity, stage, "歌曲详情 ID 与请求不一致");
        if (error.Message is "业务 code 非零或格式错误。" or "网易云接口业务码异常。")
            return new(RequestFailureKind.Business, stage, "接口业务码异常");
        if (error is NetEaseLrcException lrc) return new(RequestFailureKind.Protocol, stage, "网易云 LRC 解析失败：" + lrc.Diagnostic);
        string reason = error.Message switch
        {
            "不是明文 LRC 或普通 Base64。" => "歌词不是明文 LRC 或有效 Base64",
            "Base64 内容不是严格 UTF-8。" => "歌词编码不是有效 UTF-8",
            "响应带加密/QRC 数据或标志，拒绝处理。" => "歌词响应不是受支持的普通 LRC",
            "缺少歌词。" or "网易云响应缺少 LRC。" => "响应缺少普通 LRC",
            _ => "接口字段或歌词格式校验失败"
        };
        return new(RequestFailureKind.Protocol, stage, reason);
    }
}
