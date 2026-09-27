using Ra3.BattleNet.Updater.Core;
using Ra3.BattleNet.Updater.Server;
using CoreUpdater = Ra3.BattleNet.Updater.Core.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>AGENT.md §2.3 故障注入：每一种都必须干净收敛，不得崩、不得写坏已有文件。</summary>
public class FaultInjectionTests
{
    private static (string V1, string V2, string M1, string M2, ManifestGenerationResult Gen) Prepare(TempDir tmp, int count = 3)
    {
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v1, Enumerable.Range(0, count).Select(i => ($"f{i}.bin", TestSupport.Big($"V1-{i}"))).ToArray());
        TestSupport.WriteTree(v2, Enumerable.Range(0, count).Select(i => ($"f{i}.bin", TestSupport.Big($"V2-{i}"))).ToArray());

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        gen.Manifest.SaveToXml(m2);

        return (v1, v2, m1, m2, gen);
    }

    private static string PrepareServer(TempDir tmp, string m1, string m2, string v1, string v2)
    {
        var server = tmp.Sub("server");
        PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 0);
        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);
        return server;
    }

    private static string PrepareClient(TempDir tmp, string v1Dir, string m1)
    {
        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1Dir, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);
        return client;
    }    [Fact]
    public void TruncatedPayload_IsRecoveredByRangeResume()
    {
        using var tmp = new TempDir();
        var (v1, v2, m1, m2, gen) = Prepare(tmp);
        var server = PrepareServer(tmp, m1, m2, v1, v2);
        var client = PrepareClient(tmp, v1, m1);

        // 每次少发 1 字节（补丁只有几十字节，所以用 1 而不是 64）
        using var http = new TestHttpServer(server) { TruncateBytes = 1 };
        var cfg = new UpdateConfig { RootPath = client, ManifestUrl = http.BaseUrl + "manifest.xml" };

        var result = new CoreUpdater(cfg).Run();

        // 服务端支持 Range，客户端应当用「续传」把缺的那截补回来（§4.6），而不是整包重下
        Assert.True(result.Outcome == UpdateOutcome.Updated, $"应当靠续传恢复，实得 {result}; Detail={result.Detail}");
        Assert.Equal(3, result.Patched);
        Assert.Equal(0, result.Full);
        Assert.True(result.BytesDownloaded < 20_000, $"不该退化成整包重下：{result.BytesDownloaded} 字节");
        TestSupport.AssertSameAs(gen.Manifest, v2, client);
    }

    [Fact]
    public void TruncatedPayload_WithoutRangeSupport_EndsAsFailed_WithoutCorruption()
    {
        using var tmp = new TempDir();
        var (v1, v2, m1, m2, _) = Prepare(tmp);
        var server = PrepareServer(tmp, m1, m2, v1, v2);
        var client = PrepareClient(tmp, v1, m1);

        // 服务端不支持 Range → 截断无法补救
        using var http = new TestHttpServer(server) { TruncateBytes = 1, SupportRange = false };
        var cfg = new UpdateConfig { RootPath = client, ManifestUrl = http.BaseUrl + "manifest.xml" };

        var result = new CoreUpdater(cfg).Run();

        Assert.True(result.Outcome is UpdateOutcome.Failed or UpdateOutcome.NeedsHostFallback,
            $"无法补救的截断必须收敛为失败，实得 {result}");
        Assert.True(result.FailedCount > 0);

        // 本地必须完好：没写成"已最新"，目标文件要么旧要么是完整新内容
        Assert.NotEqual(TestSupport.Md5File(m2), TestSupport.Md5File(Path.Combine(client, "manifest.xml")));
        foreach (var name in new[] { "f0.bin", "f1.bin", "f2.bin" })
        {
            var actual = TestSupport.Md5File(Path.Combine(client, name));
            var oldMd5 = TestSupport.Md5File(Path.Combine(v1, name));
            var newMd5 = TestSupport.Md5File(Path.Combine(v2, name));
            Assert.True(actual == oldMd5 || actual == newMd5, $"{name} 被写坏了");
        }
    }
    [Fact]
    public void CorruptedPatch_FallsBackToFullDownload()
    {
        using var tmp = new TempDir();
        var (v1, v2, m1, m2, gen) = Prepare(tmp);
        var server = PrepareServer(tmp, m1, m2, v1, v2);
        var client = PrepareClient(tmp, v1, m1);

        // 把补丁写成垃圾：下载会成功，但打不上 → 必须回落完整下载
        foreach (var p in Directory.GetFiles(Path.Combine(server, "patches")))
            File.WriteAllText(p, "this is not a valid hdiff patch");

        using var http = new TestHttpServer(server);
        var cfg = new UpdateConfig { RootPath = client, ManifestUrl = http.BaseUrl + "manifest.xml" };

        var result = new CoreUpdater(cfg).Run();

        Assert.True(result.Outcome == UpdateOutcome.Updated, $"应当回落成功，实得 {result}; Detail={result.Detail}");
        Assert.Equal(0, result.Patched);
        Assert.Equal(3, result.Full);
        TestSupport.AssertSameAs(gen.Manifest, v2, client);
    }

    [Fact]
    public void LockedTargetFile_IsReportedAsFailure_AndLocalManifestIsNotWritten()
    {
        using var tmp = new TempDir();
        var (v1, v2, m1, m2, _) = Prepare(tmp);
        var server = PrepareServer(tmp, m1, m2, v1, v2);
        var client = PrepareClient(tmp, v1, m1);

        using var http = new TestHttpServer(server);
        var cfg = new UpdateConfig { RootPath = client, ManifestUrl = http.BaseUrl + "manifest.xml" };

        // 占住其中一个目标文件
        using (File.Open(Path.Combine(client, "f0.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = new CoreUpdater(cfg).Run();
            Assert.True(result.FailedCount > 0, "被占用的文件必须计入失败");
            Assert.NotEqual(UpdateOutcome.Updated, result.Outcome);
            Assert.NotEqual(TestSupport.Md5File(m2), TestSupport.Md5File(Path.Combine(client, "manifest.xml")));
        }

        // 释放后重跑应当成功
        var again = new CoreUpdater(cfg).Run();
        Assert.True(again.Outcome == UpdateOutcome.Updated, $"释放后应当成功，实得 {again}");
    }

    [Fact]
    public void MalformedRemoteManifest_DoesNotCrash_AndRequestsHostFallback()
    {
        using var tmp = new TempDir();
        var client = tmp.Sub("client");
        var server = tmp.Sub("server");
        File.WriteAllText(Path.Combine(server, "manifest.xml"), "<Metadata><broken");

        using var http = new TestHttpServer(server);
        var cfg = new UpdateConfig { RootPath = client, ManifestUrl = http.BaseUrl + "manifest.xml" };

        var result = new CoreUpdater(cfg).Run();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.ManifestUnavailable, result.Reason);
        Assert.False(string.IsNullOrEmpty(result.Detail));   // 必须给出可诊断的原因
    }
}