using Ra3.BattleNet.Updater.Server;
using CoreUpdater = Ra3.BattleNet.Updater.Core.Updater;
using Ra3.BattleNet.Updater.Core;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 上网字节（`wire`）的口径（AGENT.md §2.1 F8 / §4.11，OPEN_ISSUES M-1）。
///
/// 这一列的意义是"**真正上网的字节**"，所以它必须能被**独立来源**验证 ——
/// 这里用测试服务器自己在网线另一头数的字节数（TestHttpServer.WireBytesSent）来对账：
/// 明文链路下，客户端收到的字节数应当与服务器发出的字节数**逐字节相等**。
/// 这条断言一旦不成立，就说明计数器（或计数位置）错了 —— 这正是 M-1 当初缺的那块。
/// </summary>
public class WireAccountingTests
{
    [Fact]
    public void WireBytes_MatchTheServersOwnCount_ForAFullDownload()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("a.bin", TestSupport.Big("PAYLOAD")));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m1, v1, [], server, minFileSize: 0);
        File.Copy(m1, Path.Combine(server, "manifest.xml"), overwrite: true);

        var client = tmp.Sub("client");          // 空目录 → 1 个文件走完整下载
        using var http = new TestHttpServer(server);
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);

        // 总账 = 收 + 发
        Assert.Equal(result.WireBytes, result.WireSentBytes + result.WireReceivedBytes);

        // **独立对账**：服务器写出去的字节 == 客户端读进来的字节（明文链路，应当逐字节相等）
        Assert.Equal(http.WireBytesSent, result.WireReceivedBytes);

        // 口径提醒：wire 含 HTTP 头，所以它**大于** payload（正文读取字节）；
        // 反过来如果有人把 wire 接回 payload，这条就会失败。
        Assert.True(result.WireBytes > result.PayloadBytes,
            $"wire({result.WireBytes}) 应当大于 payload({result.PayloadBytes}) —— 它含 HTTP 头与往返");
        Assert.True(result.WireSentBytes > 0, "请求方向也必须有字节");
    }

    [Fact]
    public void WireBytes_AlsoCoverFailuresAndRetries()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("a.bin", TestSupport.Big("PAYLOAD")));
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2, ("a.bin", TestSupport.Big("PAYLOAD2")), ("b.bin", "brand new"));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        ManifestGenerator.Generate(v2, m1, [], oldRoot: v1).Manifest.SaveToXml(m2);

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 0);
        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);

        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);

        // 所有补丁都 404 → 走完整下载：失败与重试的字节也必须在账上
        using var http = new TestHttpServer(server) { PatchNotFound = true };
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.True(http.NotFound > 0, "这个场景必须真的探空过补丁");
        Assert.True(result.WireBytes > result.BytesDownloaded,
            "wire 必须覆盖 404 正文与请求头，而不只是内容字节");

        // 404 会少读一次正文（客户端看到状态码就返回），所以这里不要求与服务器计数逐字节相等，
        // 只要求：读到的 ≤ 服务器写出的（不可能多读）。
        Assert.True(result.WireReceivedBytes <= http.WireBytesSent,
            $"客户端读了 {result.WireReceivedBytes}，服务器只写了 {http.WireBytesSent} —— 不可能多读");
    }
}
