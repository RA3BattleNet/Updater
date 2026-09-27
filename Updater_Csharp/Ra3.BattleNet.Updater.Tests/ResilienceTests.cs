using Ra3.BattleNet.Updater.Core;
using Ra3.BattleNet.Updater.Server;
using CoreUpdater = Ra3.BattleNet.Updater.Core.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>可靠性：中断后续跑（F6）、多源回退（§4.6）。</summary>
public class ResilienceTests
{
    private static (string V1, string V2, ManifestGenerationResult Gen) BuildPair(TempDir tmp, int count)
    {
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v1, Enumerable.Range(0, count)
            .Select(i => ($"f{i}.bin", TestSupport.Big($"V1-{i}"))).ToArray());
        TestSupport.WriteTree(v2, Enumerable.Range(0, count)
            .Select(i => ($"f{i}.bin", TestSupport.Big($"V2-{i}"))).ToArray());

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        gen.Manifest.SaveToXml(m2);

        return (m1, m2, gen);
    }

    [Fact]
    public void InterruptedRun_CanBeResumed_AndIsIdempotent()
    {
        using var tmp = new TempDir();
        var (m1, m2, gen) = BuildPair(tmp, 6);
        var v1 = Path.Combine(tmp.Path, "v1");
        var v2 = Path.Combine(tmp.Path, "v2");

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 0);
        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);

        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);

        using var http = new TestHttpServer(server);
        var cfg = new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
            MaxConcurrency = 2,
        };

        // 第一次：做到第 2 个文件就取消（同步回调，取消时机确定）
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<UpdateProgress>(p => { if (p.Current >= 2) cts.Cancel(); });
        var first = new CoreUpdater(cfg).Run(progress, cts.Token);

        Assert.NotEqual(UpdateOutcome.Updated, first.Outcome);
        Assert.False(File.Exists(Path.Combine(client, "manifest.xml.tmp")), "取消后不该留下半截清单");
        // 没写完本地清单（仍与远端不同）
        Assert.NotEqual(TestSupport.Md5File(m2), TestSupport.Md5File(Path.Combine(client, "manifest.xml")));

        // 第二次：正常跑完（已更新的文件会被跳过，只补差的）
        var second = new CoreUpdater(cfg).Run();
        Assert.True(second.Outcome == UpdateOutcome.Updated, $"期望 Updated 实得 {second}; Detail={second.Detail}");
        TestSupport.AssertSameAs(gen.Manifest, v2, client);

        // 第三次：幂等，什么都不做
        var third = new CoreUpdater(cfg).Run();
        Assert.Equal(UpdateOutcome.UpToDate, third.Outcome);
        Assert.Equal(0, third.BytesDownloaded);
    }

    [Fact]
    public void FallbackBaseUrl_IsUsed_WhenPrimaryLacksPayload()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v1, ("a.txt", TestSupport.Big("ONE")), ("b.txt", TestSupport.Big("ONE")));
        TestSupport.WriteTree(v2, ("a.txt", TestSupport.Big("TWO")), ("b.txt", TestSupport.Big("TWO")));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        gen.Manifest.SaveToXml(m2);

        // 主源只有清单，没有任何载荷
        var serverA = tmp.Sub("serverA");
        File.Copy(m2, Path.Combine(serverA, "manifest.xml"), overwrite: true);

        // 备用源有 files/ 与 patches/
        var serverB = tmp.Sub("serverB");
        PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], serverB, minFileSize: 0);

        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);

        using var httpA = new TestHttpServer(serverA);
        using var httpB = new TestHttpServer(serverB);

        var cfg = new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = httpA.BaseUrl + "manifest.xml",
            FallbackBaseUrls = [httpB.BaseUrl],
        };

        var result = new CoreUpdater(cfg).Run();

        Assert.True(result.Outcome == UpdateOutcome.Updated, $"期望 Updated 实得 {result}");
        Assert.True(httpA.NotFound > 0, "主源应当确实缺载荷");
        Assert.True(httpB.Requests > 0, "备用源应当被用到");
        TestSupport.AssertSameAs(gen.Manifest, v2, client);
    }
}