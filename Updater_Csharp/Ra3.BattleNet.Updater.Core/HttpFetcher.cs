using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Ra3.BattleNet.Updater.Core;

internal sealed record DownloadOutcome(bool Ok, int StatusCode, long Bytes, string Reason, long WireBytes = 0);

internal sealed record ManifestOutcome(bool Ok, bool NotModified, byte[]? Content, string? ETag, string Reason, string? HttpVersion);

/// <summary>
/// HTTP 访问。要求：HTTP/2 多路复用、连接复用、条件请求、Range 续传（AGENT.md §4.6）。
/// 注意：HTTP/2 在实践上依赖 TLS/ALPN，因此**只对 https 优先 h2**；
/// 明文连接强制 HTTP/1.1（否则会走 h2c 直连，普通静态服务器无法应答）。
/// 协商到的版本会记录到结果 Detail 里，便于确认 h2 真的生效（K7）。
/// </summary>
internal sealed class HttpFetcher : IDisposable
{
    private readonly HttpClient _http;

    /// <summary>响应头超时：等不到头就是服务端有问题（正文另算，见下）。</summary>
    private static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 正文**停滞**超时：只要还在收到数据就不算超时（大文件慢链路也不会被掐死），
    /// 超过这么久一个字节都没动才判失败。这一条与文件大小无关。
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ReArmInterval = TimeSpan.FromSeconds(2);

    private int _requests;
    private long _wire;

    /// <summary>
    /// 本会话**真正读下网线**的字节数（压缩后就是压缩后的大小）。AGENT.md §2.2 要与整包比带宽，
    /// 比的必须是这个数，而不是"解压后写盘的内容字节"—— 开了传输压缩之后两者差 2 倍以上。
    /// </summary>
    public long WireBytes => Interlocked.Read(ref _wire);

    /// <summary>本会话发起的 HTTP 请求数（AGENT.md F8：请求数必须可观测）。</summary>
    public int Requests => Volatile.Read(ref _requests);

    public HttpFetcher(int maxConcurrency)
    {
        var handler = new SocketsHttpHandler
        {
            // 压低连接数上限、复用它 —— 既提速也保护源站
            MaxConnectionsPerServer = Math.Max(2, Math.Min(8, maxConcurrency)),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };

        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ra3BattleNetUpdater/1.0");
    }

    /// <summary>取远端 manifest；带 If-None-Match 时命中即 304（零正文）。</summary>
    public async Task<ManifestOutcome> GetManifestAsync(string url, string? etag, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            using var req = NewRequest(url);
            if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            var version = $"HTTP/{resp.Version}";

            if (resp.StatusCode == HttpStatusCode.NotModified)
                return new ManifestOutcome(true, true, null, etag, string.Empty, version);

            if (!resp.IsSuccessStatusCode)
                return new ManifestOutcome(false, false, null, null, $"manifest HTTP {(int)resp.StatusCode}", version);

            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            // 清单也要计入"这次更新花了多少带宽"：它每次都要下（除非 304），
            // 小增量里它占比很可观（1.3 MB vs 几 MB）。注意主机若自己做了 Content-Encoding，
            // 这里计到的是**解压后**的字节（§2.2 的口径说明见 AGENT.md）。
            Interlocked.Add(ref _wire, bytes.Length);
            return new ManifestOutcome(true, false, bytes, resp.Headers.ETag?.Tag, string.Empty, version);
        }
        catch (Exception ex)
        {
            return new ManifestOutcome(false, false, null, null, ex.Message, null);
        }
    }

    /// <summary>
    /// 下载到 <paramref name="destPath"/>。允许续传：未完成部分留在 <c>destPath.part</c>。
    /// 404 表示"服务端没有这个资源"（补丁不存在），调用方据此回落。
    /// </summary>
    public async Task<DownloadOutcome> DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        var partPath = destPath + ".part";
        var wire = 0L;   // 这一文件真正读下网线的字节（含重试、含续传前那半截）

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Interlocked.Increment(ref _requests);

            var existing = Fs.Exists(partPath) ? Fs.Length(partPath) : 0;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HeadersTimeout);   // 建连 + 响应头；正文阶段会换成停滞看门狗

            try
            {
                using var req = NewRequest(url);
                if (existing > 0)
                {
                    req.Headers.Range = new RangeHeaderValue(existing, null);

                    // 续传必须拿**未压缩**的字节：Range 一旦打在 gzip 流上，客户端拿到的是半截流，
                    // 解压必然失败（而且失败得很难看懂）。代价是"续传的那一次"没享受压缩，
                    // 第一次（非续传）仍然照常压 —— 这比"续传彻底不可用"划算得多。
                    req.Headers.AcceptEncoding.Clear();
                    req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                }

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return new DownloadOutcome(false, 404, 0, UpdateReasons.NoPatch);

                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // 断点信息失效（服务端换了内容或文件被截断）：丢掉重来
                    Fs.Delete(partPath);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    var code = (int)resp.StatusCode;

                    // 确定性的拒绝不该重试：403/404/410 再问三遍还是同一个答案，
                    // 只会让用户多等一个来回。由调用方决定回落（下个源 / 完整下载）。
                    if (attempt == 3 || code is 403 or 404 or 410)
                        return new DownloadOutcome(false, code, 0, $"HTTP {code}");

                    await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }

                // 超时分层（§4.6）：
                //  - 连接：handler 上 5s；
                //  - 响应头：60s（等不到头就是服务端有问题）；
                //  - 正文：**停滞超时**，不是"总预算"。总预算式超时按大小折算是错的 ——
                //    5.6 GB 的 ISO 在 10 Mbps 上要 75 分钟，任何合理上限都会把它掐死；
                //    而"卡住不动"才是真异常。所以每读到数据就把看门狗重新上弦。
                //    整体上限交给会话级 SessionTimeout（§4.6「整体有时限」）。
                cts.CancelAfter(HeadersTimeout);

                var append = existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                long written = append ? existing : 0;
                var lastArm = Stopwatch.GetTimestamp();
                cts.CancelAfter(StallTimeout);   // 进入正文读取，换成停滞看门狗

                using (var fs = Fs.OpenWrite(partPath, append))
                using (var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
                {
                    var buffer = new byte[1 << 16];
                    int read;
                    while ((read = await src.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                        written += read;
                        wire += read;

                        // 重新上弦：每 2 秒最多一次，避免每个 64KB 都动一次定时器
                        if (Stopwatch.GetElapsedTime(lastArm) > ReArmInterval)
                        {
                            cts.CancelAfter(StallTimeout);
                            lastArm = Stopwatch.GetTimestamp();
                        }
                    }
                }

                Fs.Move(partPath, destPath, overwrite: true);
                Interlocked.Add(ref _wire, wire);
                return new DownloadOutcome(true, 200, written, string.Empty, wire);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == 3) return new DownloadOutcome(false, 0, 0, ex.Message);
                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
            }
        }

        return new DownloadOutcome(false, 0, 0, "下载重试超限");
    }

    /// <summary>指数退避：300ms → 600ms（§4.6「重试：指数退避、上限 2–3 次」）。</summary>
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt - 1));

    private static HttpRequestMessage NewRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);

        // 仅 https 走 HTTP/2 优先；明文用 HTTP/1.1（h2c 直连在真实静态服务上不可用）
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            req.Version = HttpVersion.Version20;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        }
        else
        {
            req.Version = HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        return req;
    }

    public void Dispose() => _http.Dispose();
}
