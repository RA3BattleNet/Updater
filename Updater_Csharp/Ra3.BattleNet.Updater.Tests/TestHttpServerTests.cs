using System.Net;
using System.Net.Http.Headers;

namespace Ra3.BattleNet.Updater.Tests;

public class TestHttpServerTests
{
    [Fact]
    public void ServesFiles_WithEtagRangeAnd404()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "manifest.xml"), "hello-world");

        using var server = new TestHttpServer(tmp.Path);
        using var http = new HttpClient();

        var get = http.GetAsync(server.BaseUrl + "manifest.xml").GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("hello-world", get.Content.ReadAsStringAsync().GetAwaiter().GetResult());

        var missing = http.GetAsync(server.BaseUrl + "nope").GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var rangeReq = new HttpRequestMessage(HttpMethod.Get, server.BaseUrl + "manifest.xml");
        rangeReq.Headers.Range = new RangeHeaderValue(6, null);
        var rangeResp = http.Send(rangeReq);
        Assert.Equal(HttpStatusCode.PartialContent, rangeResp.StatusCode);
        Assert.Equal("world", rangeResp.Content.ReadAsStringAsync().GetAwaiter().GetResult());

        var conditional = new HttpRequestMessage(HttpMethod.Get, server.BaseUrl + "manifest.xml");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", get.Headers.ETag?.Tag);
        var notModified = http.Send(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
    }

    [Fact]
    public async Task CleartextUsesHttp11()
    {
        // 记录一个实测结论：.NET 在明文连接上按 HTTP/2 直连（h2c）发帧，
        // 普通 HTTP/1.1 静态服务器会回 PROTOCOL_ERROR。
        // 因此引擎只在 https 上优先 h2（真实部署走 CDN/TLS），明文固定 HTTP/1.1。
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "manifest.xml"), "x");

        using var server = new TestHttpServer(tmp.Path);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var plain = new HttpRequestMessage(HttpMethod.Get, server.BaseUrl + "manifest.xml")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        var resp = await http.SendAsync(plain);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(HttpVersion.Version11, resp.Version);
    }
}
