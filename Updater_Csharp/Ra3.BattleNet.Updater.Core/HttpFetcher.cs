using System.Net;
using System.Net.Http.Headers;

namespace Ra3.BattleNet.Updater.Core;

internal sealed record DownloadOutcome(bool Ok, int StatusCode, long Bytes, string Reason);

internal sealed record ManifestOutcome(bool Ok, bool NotModified, byte[]? Content, string? ETag, string Reason);

/// <summary>
/// HTTP 访问。要求：HTTP/2 多路复用、连接复用、条件请求、Range 续传（AGENT.md §4.6）。
/// </summary>
internal sealed class HttpFetcher : IDisposable
{
    private readonly HttpClient _http;

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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            using var req = NewRequest(url);
            if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (resp.StatusCode == HttpStatusCode.NotModified)
                return new ManifestOutcome(true, true, null, etag, string.Empty);

            if (!resp.IsSuccessStatusCode)
                return new ManifestOutcome(false, false, null, null, $"manifest HTTP {(int)resp.StatusCode}");

            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            return new ManifestOutcome(true, false, bytes, resp.Headers.ETag?.Tag, string.Empty);
        }
        catch (Exception ex)
        {
            return new ManifestOutcome(false, false, null, null, ex.Message);
        }
    }

    /// <summary>
    /// 下载到 <paramref name="destPath"/>。允许续传：未完成部分留在 <c>destPath.part</c>。
    /// 返回 404 时表示"服务端没有这个资源"（补丁不存在），调用方据此回落。
    /// </summary>
    public async Task<DownloadOutcome> DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        var partPath = destPath + ".part";

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var existing = Fs.Exists(partPath) ? Fs.Length(partPath) : 0;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(10));

            try
            {
                using var req = NewRequest(url);
                if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

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
                    if (attempt == 3) return new DownloadOutcome(false, (int)resp.StatusCode, 0, $"HTTP {(int)resp.StatusCode}");
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct);
                    continue;
                }

                var append = existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                long written = append ? existing : 0;

                using (var fs = Fs.OpenWrite(partPath, append))
                using (var src = await resp.Content.ReadAsStreamAsync(cts.Token))
                {
                    var buffer = new byte[1 << 16];
                    int read;
                    while ((read = await src.ReadAsync(buffer, cts.Token)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                        written += read;
                    }
                }

                Fs.Move(partPath, destPath, overwrite: true);
                return new DownloadOutcome(true, 200, written, string.Empty);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == 3) return new DownloadOutcome(false, 0, 0, ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct);
            }
        }

        return new DownloadOutcome(false, 0, 0, "下载重试超限");
    }

    private static HttpRequestMessage NewRequest(string url) =>
        new(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };

    public void Dispose() => _http.Dispose();
}
