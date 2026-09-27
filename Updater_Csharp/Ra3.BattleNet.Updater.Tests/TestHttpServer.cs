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

    public string BaseUrl { get; }

    public int Requests => Volatile.Read(ref _requests);

    public int NotFound => Volatile.Read(ref _notFound);

    public int NotModified => Volatile.Read(ref _notModified);

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

                if (name.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase)) ifNoneMatch = value;
                else if (name.Equals("Range", StringComparison.OrdinalIgnoreCase)) range = value;
            }

            var parts = requestLine.Split(' ');
            var urlPath = parts.Length > 1 ? parts[1] : "/";
            Interlocked.Increment(ref _requests);

            var file = ResolveFile(urlPath);
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

            if (range is not null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
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
