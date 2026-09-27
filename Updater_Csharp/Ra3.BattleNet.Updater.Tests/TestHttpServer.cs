using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>截断注入的适用范围，见 <see cref="TestHttpServer.TruncateScope"/>。</summary>
internal enum TruncateScope
{
    /// <summary>只有每个路径的**第一次、不带 Range** 的响应被截断（与 test2 中继的实测行为一致，默认）。</summary>
    FirstOnly,

    /// <summary>每个响应都截断，包括带 Range 的续传 —— 用来测"怎么都补不回来"的那条路。</summary>
    Every,

    /// <summary>凡是**不带 Range** 的响应一律截断，带 Range 的正常发全 —— I-1 的实验室复现。</summary>
    WithoutRange,
}

/// <summary>
/// 极简静态文件服务器（HTTP/1.1）：支持 ETag/If-None-Match 与 Range。
/// 用来做真实的端到端测试，而不是 mock 掉网络层。
/// 说明：明文连接下 HttpClient 会退回 HTTP/1.1，因此这里不覆盖 HTTP/2 路径。
/// </summary>
internal sealed class TestHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _root;
    private readonly CancellationTokenSource _cts = new();

    private int _requests;
    private int _notFound;
    private int _notModified;
    private readonly List<string> _paths = [];
    private readonly Lock _pathsLock = new();
    private int _inFlight;
    private int _peakInFlight;

    public string BaseUrl { get; }

    public int Requests => Volatile.Read(ref _requests);

    public int NotFound => Volatile.Read(ref _notFound);

    public int NotModified => Volatile.Read(ref _notModified);

    /// <summary>按到达顺序记录所有请求的 URL 路径（§3.3 寻址规则的断言依据）。</summary>
    public IReadOnlyList<string> RequestPaths
    {
        get { lock (_pathsLock) return _paths.ToList(); }
    }

    /// <summary>
    /// 「同时在处理中的（被人为延迟的）请求」的历史峰值。
    /// 只在 <see cref="ArtificialDelayMs"/> 窗口内计数：这样串行客户端**不可能**凑出峰值 2
    /// （它必须先收完上一个响应才会发下一个），并发客户端才凑得出。
    /// 用来证明客户端不是线性跑文件的。
    /// </summary>
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    public void ResetPeak() => Volatile.Write(ref _peakInFlight, 0);

    /// <summary>&gt;0 时每个响应前先睡这么久，便于把并发度放大成可观测的时间差。</summary>
    public int ArtificialDelayMs { get; set; }

    /// <summary>最近一次请求的 Accept-Encoding，用于验证客户端确实开启了透明压缩。</summary>
    public string? LastAcceptEncoding { get; private set; }

    /// <summary>&gt;0 时：声明完整 Content-Length，但实际少发这么多字节（模拟响应截断）。</summary>
    public int TruncateBytes { get; set; }

    /// <summary>
    /// 截断的**适用范围**（默认 <see cref="TruncateScope.FirstOnly"/>，与 test2 的中继实测一致）：
    /// 真实链路上「响应被截断」几乎都发生在**不带 Range** 的整份响应上，
    /// 续传请求（带 Range）通常能正常拿全 —— 客户端只要有办法换一条路就能自愈。
    /// </summary>
    public TruncateScope TruncateScope { get; set; } = TruncateScope.FirstOnly;

    /// <summary>
    /// true 时把响应体**整个丢掉**（仍声明完整 Content-Length）—— 模拟 TLS 上小响应被整块缓冲的行为：
    /// 连接截断的异常先于任何字节到达读循环，客户端本地一个字节都没落地。
    /// 这是 I-1 的原始症状，比"少一个字节"更狠，也更能区分"重试"和"原样重放"。
    /// </summary>
    public bool DropBodyEntirely { get; set; }

    private readonly HashSet<string> _truncatedPaths = new(StringComparer.Ordinal);
    private readonly List<string?> _ranges = [];

    /// <summary>按到达顺序记录每个请求的 Range 头（null = 没带）。用来断言"重试确实换了请求形态"。</summary>
    public IReadOnlyList<string?> RequestRanges
    {
        get { lock (_pathsLock) return _ranges.ToList(); }
    }

    /// <summary>是否支持 Range（206）。关掉它就能测「截断后无法续传」的路径。</summary>
    public bool SupportRange { get; set; } = true;

    /// <summary>true 时所有 <c>/patches/</c> 请求都返回 404（模拟"服务端没有该内容对的补丁"）。</summary>
    public bool PatchNotFound { get; set; }

    /// <summary>
    /// 404 响应的正文长度。真实 CDN 会回一整页 HTML（实测 CF 上 28,455 B，
    /// 能占到一次更新流量的 10%）—— 用来验证"错误响应的声明正文被如实计入 payload"。
    /// </summary>
    public int NotFoundBodyBytes { get; set; }

    /// <summary>
    /// true 时对 <c>files/</c>、<c>patches/</c> 的响应做 gzip 压缩并带 <c>Content-Encoding: gzip</c>
    /// （模拟"服务端/边缘给二进制也开了压缩"）。清单不压。
    /// 行为与真实静态服务器一致：客户端要 <c>identity</c>、或请求带 <c>Range</c> 时，都退化为不压缩。
    /// </summary>
    public bool CompressPayloads { get; set; }

    /// <summary>
    /// 敌意变体：即使请求带 <c>Range</c>（续传）也照样压、并且把 Range 打在**压缩后**的字节上，
    /// 同时无视 <c>Accept-Encoding: identity</c>。用来验证"服务端不守规矩时客户端不会静默损坏"。
    /// </summary>
    public bool NaiveRangeOverCompressed { get; set; }

    /// <summary>带 <c>Content-Encoding: gzip</c> 的响应数。</summary>
    public int CompressedResponses => Volatile.Read(ref _compressed);

    /// <summary>**真正写到网线上的**响应体字节数（压缩后）。用来区分"内容字节"与"传输字节"。</summary>
    public long WireBytesSent => Interlocked.Read(ref _wire);

    public void ResetWire() => Interlocked.Exchange(ref _wire, 0);

    private int _compressed;
    private long _wire;

    public TestHttpServer(string root)
    {
        _root = Path.GetFullPath(root);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => Handle(client));
        }
    }

    private void Handle(TcpClient client)
    {
        HandleCore(client);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value
               && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private void HandleCore(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

            var requestLine = reader.ReadLine();
            if (string.IsNullOrEmpty(requestLine)) return;

            string? ifNoneMatch = null, range = null;
            string? header;
            while (!string.IsNullOrEmpty(header = reader.ReadLine()))
            {
                var idx = header.IndexOf(':');
                if (idx < 0) continue;
                var name = header[..idx].Trim();
                var value = header[(idx + 1)..].Trim();

                if (name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)) LastAcceptEncoding = value;
                else if (name.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase)) ifNoneMatch = value;
                else if (name.Equals("Range", StringComparison.OrdinalIgnoreCase)) range = value;
            }

            var parts = requestLine.Split(' ');
            var urlPath = parts.Length > 1 ? parts[1] : "/";
            Interlocked.Increment(ref _requests);
            lock (_pathsLock)
            {
                _paths.Add(urlPath);
                _ranges.Add(range);
            }

            // 人为延迟放在「读请求之后、写响应之前」：并发度会直接体现为时间差与重叠峰值。
            // 计数窗口只包住延迟本身，串行客户端因此不可能出现峰值 2。
            if (ArtificialDelayMs > 0)
            {
                var n = Interlocked.Increment(ref _inFlight);
                InterlockedMax(ref _peakInFlight, n);
                Thread.Sleep(ArtificialDelayMs);
                Interlocked.Decrement(ref _inFlight);
            }

            var file = ResolveFile(urlPath);
            if (PatchNotFound && urlPath.StartsWith("/patches/", StringComparison.OrdinalIgnoreCase))
                file = null;

            if (file is null)
            {
                Interlocked.Increment(ref _notFound);
                WriteResponse(stream, 404, "Not Found", [],
                    NotFoundBodyBytes > 0 ? new byte[NotFoundBodyBytes] : []);
                return;
            }

            // 大文件**必须流式**发出：File.ReadAllBytes 遇到 5.6 GB 会直接爆掉
            //（.NET 单数组上限 ~2 GB），连接被掐断 → 客户端重试后如实报 download_failed。
            // 这不是产品的限制，是测试服务器的幼稚实现 —— 真实静态服务器/CND 都是流式的。
            var length = new FileInfo(file).Length;

            // ETag：小文件按内容算（精确）；大文件按 大小+mtime，免得每次请求哈希 5 GB
            string etag;
            if (length <= 8L * 1024 * 1024)
            {
                using var fs0 = File.OpenRead(file);
                etag = '"' + Convert.ToHexStringLower(MD5.HashData(fs0)) + '"';
            }
            else
            {
                etag = $"\"big-{length:x}-{File.GetLastWriteTimeUtc(file).Ticks:x}\"";
            }

            if (ifNoneMatch is not null && ifNoneMatch == etag)
            {
                Interlocked.Increment(ref _notModified);
                WriteResponse(stream, 304, "Not Modified", [("ETag", etag)], []);
                return;
            }

            if (SupportRange && range is not null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var spec = range[6..].Split('-')[0];
                if (long.TryParse(spec, out var from) && from < length)
                {
                    ServeFile(stream, file, from, length - from, length - from, 206, "Partial Content",
                        [("ETag", etag), ("Accept-Ranges", "bytes"),
                         ("Content-Range", $"bytes {from}-{length - 1}/{length}")]);
                    return;
                }
            }

            var isManifest = urlPath.EndsWith("manifest.xml", StringComparison.OrdinalIgnoreCase);
            var wantsGzip = (LastAcceptEncoding ?? string.Empty).Contains("gzip", StringComparison.OrdinalIgnoreCase);
            var askedIdentity = (LastAcceptEncoding ?? string.Empty).Contains("identity", StringComparison.OrdinalIgnoreCase);

            // gzip 传输（只压载荷，不压清单）。这条路要整块进内存，只用于小文件场景。
            if (CompressPayloads && !isManifest && wantsGzip && (!askedIdentity || NaiveRangeOverCompressed)
                && length <= 64L * 1024 * 1024)
            {
                var gz = Gzip(File.ReadAllBytes(file));
                Interlocked.Increment(ref _compressed);
                WriteResponse(stream, 200, "OK",
                    [("ETag", etag), ("Accept-Ranges", "bytes"), ("Content-Encoding", "gzip")], gz);
                return;
            }

            // 只截断载荷：清单本身要保持完整，否则测的就不是"载荷截断"了
            if (TruncateBytes > 0 && !isManifest && ShouldTruncate(urlPath, range, length))
            {
                // 声明完整长度，但只发 length - TruncateBytes 个字节 → 客户端应判定截断并重试
                // （DropBodyEntirely 时一个字节都不发）
                var sent = DropBodyEntirely ? 0 : length - TruncateBytes;
                ServeFile(stream, file, 0, sent, length, 200, "OK",
                    [("ETag", etag), ("Accept-Ranges", "bytes")]);
                return;
            }

            ServeFile(stream, file, 0, length, length, 200, "OK",
                [("ETag", etag), ("Accept-Ranges", "bytes")]);
        }
    }

    /// <summary>这个响应要不要动手脚。除了"整份丢掉"之外，还得真的截得动（文件比要截的字节多）。</summary>
    private bool ShouldTruncate(string urlPath, string? range, long length)
    {
        var eligible = TruncateScope switch
        {
            TruncateScope.Every => true,
            TruncateScope.WithoutRange => range is null,
            _ => range is null && MarkFirstTruncation(urlPath),
        };

        if (!eligible) return false;
        return DropBodyEntirely ? length > 0 : length > TruncateBytes;
    }

    /// <summary>每个路径只坑第一次（返回 true 表示"这次是新的一发"，可以动手）。</summary>
    private bool MarkFirstTruncation(string urlPath)
    {
        lock (_pathsLock) return _truncatedPaths.Add(urlPath);
    }

    /// <summary>流式发一个文件的一段：head 声明 <paramref name="declaredLength"/>，实际只写 <paramref name="count"/> 字节。</summary>
    private void ServeFile(Stream stream, string file, long offset, long count, long declaredLength,
        int code, string reason, (string, string)[] headers)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {code} {reason}\r\n");
        sb.Append($"Content-Length: {declaredLength}\r\n");
        foreach (var (k, v) in headers) sb.Append($"{k}: {v}\r\n");
        sb.Append("Connection: close\r\n\r\n");

        var head = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(head);
        Interlocked.Add(ref _wire, head.Length);

        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[1 << 20];
        long left = count;
        while (left > 0)
        {
            var n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n <= 0) break;
            stream.Write(buffer, 0, n);
            left -= n;
            Interlocked.Add(ref _wire, n);
        }
        stream.Flush();
    }

    private string? ResolveFile(string urlPath)
    {
        var rel = urlPath.Split('?')[0].TrimStart('/');
        if (rel.Length == 0) rel = "manifest.xml";
        rel = rel.Replace('/', Path.DirectorySeparatorChar);

        var full = Path.GetFullPath(Path.Combine(_root, rel));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(full) ? full : null;
    }

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(data);
        return ms.ToArray();
    }

    private void WriteResponse(Stream stream, int code, string reason, (string, string)[] headers, byte[] body)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {code} {reason}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        foreach (var (k, v) in headers) sb.Append($"{k}: {v}\r\n");
        sb.Append("Connection: close\r\n\r\n");

        var head = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(head);
        if (body.Length > 0) stream.Write(body);
        stream.Flush();
        Interlocked.Add(ref _wire, head.Length + body.Length);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
