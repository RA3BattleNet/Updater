using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Models;
using CoreUpdater = Ra3.BattleNet.Updater.Core.Updater;
using Ra3.BattleNet.Updater.Core;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 「服务端预压缩 + 客户端透明解压」到底可不可行（AGENT.md §4.6 / §10 Q13-8）。
/// 关键结论：**传输层压缩不会碰到内容身份** —— manifest 里的 MD5 是原始字节的，
/// 客户端先解压再校验；压不压、压得好不好，最坏只是"这次传得大一点"，不可能传错。
/// 所以它不是协议改动，而是**部署开关**。
/// </summary>
public class GzipTransferTests
{
    private static (string Client, string Server, ManifestModel M2, string V1, string V2) Prepare(TempDir tmp)
    {
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1,
            ("bin/a.dll", TestSupport.Big("V1-A", 64 * 1024)),
            ("bin/b.dll", TestSupport.Big("V1-B", 48 * 1024)),
            ("data/keep.txt", "unchanged"));

        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2,
            ("bin/a.dll", TestSupport.Big("V2-A", 64 * 1024)),
            ("bin/b.dll", TestSupport.Big("V2-B", 48 * 1024)),
            ("data/keep.txt", "unchanged"));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        gen.Manifest.SaveToXml(m2);

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 0);
        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);

        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);

        return (client, server, gen.Manifest, v1, v2);
    }

    [Fact]
    public void GzipPayloads_AreTransparentlyDecompressed_AndStillByteExact()
    {
        using var tmp = new TempDir();
        var (client, server, manifest, _, v2) = Prepare(tmp);

        using var http = new TestHttpServer(server) { CompressPayloads = true };
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(0, result.FailedCount);
        Assert.True(http.CompressedResponses > 0, "服务端应当确实用 gzip 发了载荷");
        Assert.Contains("gzip", http.LastAcceptEncoding ?? string.Empty);

        // 内容是逐字节正确的（解压发生在校验之前）
        TestSupport.AssertSameAs(manifest, v2, client);

        // 客户端统计的 bytes 是**解压后**的内容字节 —— 这是"内容变了多少"，
        // 不是"网线上走了多少"。要与整包比带宽，得看 CDN 的出口统计（见 §2.2.1）。
        Assert.True(result.BytesDownloaded > 100 * 1024,
            $"客户端应当按解压后的内容字节计数，实得 {result.BytesDownloaded}");
    }

    [Fact]
    public void ResumeAgainstAHostileServer_DoesNotSilentlyCorrupt()
    {
        using var tmp = new TempDir();
        var (client, server, _, v1, v2) = Prepare(tmp);

        // 敌意变体：响应被截断 + 服务端把 Range 打在**压缩后**的字节上，还无视 identity。
        // 客户端要 <c>identity</c> 是对的，但对不守规矩的服务端无能为力 ——
        // 这时只要求一件事：**不许静默损坏**。
        using var http = new TestHttpServer(server)
        {
            CompressPayloads = true,
            NaiveRangeOverCompressed = true,
            TruncateBytes = 1,
        };

        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        }).Run();

        // 每个文件要么还是旧的、要么是完整的新内容，绝不能是半截
        foreach (var name in new[] { "bin/a.dll", "bin/b.dll" })
        {
            var actual = TestSupport.Md5File(Path.Combine(client, name.Replace('/', Path.DirectorySeparatorChar)));
            var oldMd5 = TestSupport.Md5File(Path.Combine(v1, name.Replace('/', Path.DirectorySeparatorChar)));
            var newMd5 = TestSupport.Md5File(Path.Combine(v2, name.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(actual == oldMd5 || actual == newMd5, $"{name} 被写坏了");
        }

        if (result.Outcome != UpdateOutcome.Updated)
        {
            // 没成功就必须如实报失败，并且不能把本地清单写成"已最新"
            Assert.True(result.FailedCount > 0);
            Assert.NotEqual(TestSupport.Md5File(Path.Combine(server, "manifest.xml")),
                TestSupport.Md5File(Path.Combine(client, "manifest.xml")));
        }
    }

    [Fact]
    public void ResumeSendsIdentity_SoACorrectServerCanStillDoRange()
    {
        using var tmp = new TempDir();
        var (client, server, manifest, _, v2) = Prepare(tmp);

        // 正常（守规矩）的服务端：截断会被 Range 续传补回来，因为续传那一次要求了 identity
        using var http = new TestHttpServer(server) { CompressPayloads = true, TruncateBytes = 1 };
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(0, result.FailedCount);
        TestSupport.AssertSameAs(manifest, v2, client);
    }
}
