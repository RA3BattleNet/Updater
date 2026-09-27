using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Ra3.BattleNet.Updater.Client;

internal sealed record DownloadOutcome(bool Ok, int StatusCode, long Bytes, string Reason, long PayloadBytes = 0);

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
    private long _payload;
    private long _wireIn;
    private long _wireOut;

    /// <summary>
    /// 本会话**真正上网的字节**（M-1、AGENT.md §2.1 F8）—— 分收/发两个方向，单位为字节。
    /// 口径：在**连接层的传输流**上计数（见 <see cref="CountingStream"/>），因此
    /// <list type="bullet">
    /// <item>含 TLS 记录、HTTP 头、压缩后的正文、以及所有重试与续传的往返；</item>
    /// <item>**不含**解压后的内容长度（那是 <see cref="PayloadBytes"/>）——两者别混用；</item>
    /// <item>走代理时数的是"本进程 ↔ 代理"那一段（实测 `ConnectCallback` 拿到的是**代理端点**）。</item>
    /// </list>
    /// </summary>
    public long WireReceivedBytes => Interlocked.Read(ref _wireIn);

    /// <inheritdoc cref="WireReceivedBytes"/>
    public long WireSentBytes => Interlocked.Read(ref _wireOut);

    /// <summary>收 + 发（一次更新"上网了多少字节"的唯一可信口径）。</summary>
    public long WireBytes => WireReceivedBytes + WireSentBytes;

    /// <summary>
    /// 连接层计数流：包在传输流**外面**（.NET 是在它之上才叠 <c>SslStream</c>），
    /// 所以数到的是加密后的真实字节，而不是解压后的内容。
    /// </summary>
    private sealed class CountingStream(Stream inner, HttpFetcher owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            if (n > 0) Interlocked.Add(ref owner._wireIn, n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n > 0) Interlocked.Add(ref owner._wireIn, n);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Interlocked.Add(ref owner._wireOut, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
            Interlocked.Add(ref owner._wireOut, buffer.Length);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 本会话**响应正文读取字节数** —— 具体口径（别再叫它 "wire"）：
    /// <list type="bullet">
    /// <item>成功响应：正文流里实际读到的字节（重试、续传的各段都算）；</item>
    /// <item>清单：读到的是**透明解压后**的长度（主机若发了 br/gzip，这里会比网线上的大）；</item>
    /// <item>错误响应（404/5xx）：只记服务端**声明**的正文长度 —— 我们提前中断，未必全收到；</item>
    /// <item>304：0。</item>
    /// </list>
    /// 所以它**不等于**真实网线字节，只能当"内容 + 可见载荷"的估计。
    /// 要做带宽验收（§2.1 F8）必须拿边缘/CDN 的出口统计（见 OPEN_ISSUES M-1）。
    /// </summary>
    public long PayloadBytes => Interlocked.Read(ref _payload);

    /// <summary>错误正文：只记服务端声明的长度（我们不会去读它，读了也只是浪费）。</summary>
    private void CountDeclaredBody(HttpResponseMessage resp) =>
        Interlocked.Add(ref _payload, resp.Content.Headers.ContentLength ?? 0);

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

            // 连接层计数（M-1）：自己开 TCP 并把传输流包一层计数器。
            // 实测：配了代理时 ctx.DnsEndPoint 给的就是**代理端点**（系统代理也一样），
            // 所以这里照 ctx 连就行，不引入"装了计数器就不能走代理"的副作用。
            ConnectCallback = async (ctx, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.AddressFamily, ct)
                        .ConfigureAwait(false);
                    await socket.ConnectAsync(addrs, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    return new CountingStream(new NetworkStream(socket, ownsSocket: true), this);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
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
            {
                CountDeclaredBody(resp);
                return new ManifestOutcome(false, false, null, null, $"manifest HTTP {(int)resp.StatusCode}", version);
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            // 清单也要计入：它每次都要下（除非 304），小增量里占比很可观。
            // 注意：主机若自己做了 Content-Encoding，这里计到的是**解压后**的长度。
            Interlocked.Add(ref _payload, bytes.Length);
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
        var bodyRead = 0L;        // 这一文件从正文流里读到的字节（含重试、含续传的各段）
        var noProgress = false;   // 上一轮一个字节都没落地

        // 记账：无论成功、重试耗尽还是部分落地，读到的字节都算进会话 Payload（只记一次）
        DownloadOutcome Done(DownloadOutcome o)
        {
            if (o.PayloadBytes > 0) Interlocked.Add(ref _payload, o.PayloadBytes);
            return o;
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Interlocked.Increment(ref _requests);

            var existing = Fs.Exists(partPath) ? Fs.Length(partPath) : 0;

            // 续传策略（I-1）：
            //  - 本地已有字节 → Range: bytes=existing-（老行为）；
            //  - 本地 0 字节，但上一轮**一个字节都没落地** → 仍显式发 Range: bytes=0-。
            //    为什么：真实链路（尤其 TLS）上小响应可能整块缓冲在传输层，连接截断的异常
            //    先于任何字节交给读循环 → .part 还是 0 → 老逻辑不发 Range → 每轮都从头 GET，
            //    3 次重试全烧在同一个坑里，最后报"下载失败"。
            //    bytes=0- 对正常服务端只是"要全量"（回 206 + 完整正文），
            //    但对"只在非 Range 请求上出错"的中间设备是**另一条路**，于是一次重试才真的
            //    是另一次尝试，而不是把第一次原样重放。
            var useRange = existing > 0 || noProgress;
            noProgress = false;
            var progressed = false;   // 本轮有没有真的落地字节（catch 里要用，所以声明在 try 外）

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HeadersTimeout);   // 建连 + 响应头；正文阶段会换成停滞看门狗

            try
            {
                using var req = NewRequest(url);
                if (useRange)
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
                {
                    // 又一次"探空补丁"：这是**正常路径**（服务端会主动弃用比目标还大的补丁），
                    // 但它的错误页正文是真金白银 —— 实测 CF 上每次 28,455 B、占一次更新流量的 10%。
                    CountDeclaredBody(resp);
                    return Done(new DownloadOutcome(false, 404, 0, UpdateReasons.NoPatch, bodyRead));
                }

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
                    CountDeclaredBody(resp);
                    if (attempt == 3 || code is 403 or 404 or 410)
                        return Done(new DownloadOutcome(false, code, 0, $"HTTP {code}", bodyRead));

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
                var baseLen = append ? existing : 0;
                long written = baseLen;

                // 服务端声明的正文长度（206 时是"剩余"长度）。注意：handler 自动解压时
                // .NET 会把这个头去掉（值变 null），所以压缩响应天然跳过下面的长度核对。
                var declared = resp.Content.Headers.ContentLength;

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
                        bodyRead += read;
                        progressed = true;

                        // 重新上弦：每 2 秒最多一次，避免每个 64KB 都动一次定时器
                        if (Stopwatch.GetElapsedTime(lastArm) > ReArmInterval)
                        {
                            cts.CancelAfter(StallTimeout);
                            lastArm = Stopwatch.GetTimestamp();
                        }
                    }
                }

                // 「干净关闭式」截断：服务端声明了长度，却提前把连接关掉 —— 这类响应看着像成功，
                // 其实少了尾巴。必须当失败，**且保留 .part**（已收到的字节是真字节，下一轮续传接着要）。
                // 不这么做的后果是静默产出一个坏文件，直到 §4.3⑥ 校验才炸，白下一遍。
                if (declared is >= 0 && written < baseLen + declared.Value)
                {
                    if (!progressed) noProgress = true;   // 一个字节都没落地 → 下轮显式 bytes=0-
                    if (attempt == 3)
                        return Done(new DownloadOutcome(false, 0, written, "响应被截断（长度不足）", bodyRead));

                    await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }

                Fs.Move(partPath, destPath, overwrite: true);
                return Done(new DownloadOutcome(true, 200, written, string.Empty, bodyRead));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 一个字节都没读到（TLS 上小响应被整块缓冲时就是这种）：下一轮改用显式 Range
                if (!progressed) noProgress = true;

                if (attempt == 3) return Done(new DownloadOutcome(false, 0, 0, ex.Message, bodyRead));
                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
            }
        }

        return Done(new DownloadOutcome(false, 0, 0, "下载重试超限", bodyRead));
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
