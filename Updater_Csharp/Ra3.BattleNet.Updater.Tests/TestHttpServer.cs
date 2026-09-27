using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Ra3.BattleNet.Updater.Tests;

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

    /// <summary>是否支持 Range（206）。关掉它就能测「截断后无法续传」的路径。</summary>
    public bool SupportRange { get; set; } = true;

    /// <summary>true 时所有 <c>/patches/</c> 请求都返回 404（模拟"服务端没有该内容对的补丁"）。</summary>
    public bool PatchNotFound { get; set; }

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
            lock (_pathsLock) _paths.Add(urlPath);

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
                WriteResponse(stream, 404, "Not Found", [], []);
                return;
            }

            var bytes = File.ReadAllBytes(file);
            var etag = '"' + Convert.ToHexStringLower(MD5.HashData(bytes)) + '"';

            if (ifNoneMatch is not null && ifNoneMatch == etag)
            {
                Interlocked.Increment(ref _notModified);
                WriteResponse(stream, 304, "Not Modified", [("ETag", etag)], []);
                return;
            }

            if (SupportRange && range is not null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var spec = range[6..].Split('-')[0];
                if (long.TryParse(spec, out var from) && from < bytes.Length)
                {
                    var slice = bytes[(int)from..];
                    WriteResponse(stream, 206, "Partial Content",
                        [("ETag", etag), ("Accept-Ranges", "bytes"),
                         ("Content-Range", $"bytes {from}-{bytes.Length - 1}/{bytes.Length}")],
                        slice);
                    return;
                }
            }

            // 只截断载荷：清单本身要保持完整，否则测的就不是"载荷截断"了
            if (TruncateBytes > 0 && bytes.Length > TruncateBytes
                && !urlPath.EndsWith("manifest.xml", StringComparison.OrdinalIgnoreCase))
            {
                WriteTruncatedResponse(stream, bytes.Length, bytes[..(bytes.Length - TruncateBytes)], etag);
                return;
            }

            WriteResponse(stream, 200, "OK", [("ETag", etag), ("Accept-Ranges", "bytes")], bytes);
        }
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

    private static void WriteTruncatedResponse(Stream stream, int declaredLength, byte[] partial, string etag)
    {
        var head = System.Text.Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Length: {declaredLength}\r\nETag: {etag}\r\nConnection: close\r\n\r\n");
        stream.Write(head);
        stream.Write(partial);
        stream.Flush();   // 内容不足就关连接 → 客户端应判定响应截断
    }

    private static void WriteResponse(Stream stream, int code, string reason, (string, string)[] headers, byte[] body)
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
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
