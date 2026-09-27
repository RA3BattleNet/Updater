using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Server;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

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
    }

    [Fact]
    public void TruncatedPayload_IsRecoveredByRangeResume()
    {
        using var tmp = new TempDir();
        var (v1, v2, m1, m2, gen) = Prepare(tmp);
        var server = PrepareServer(tmp, m1, m2, v1, v2);
        var client = PrepareClient(tmp, v1, m1);

        // 每次少发 1 字节（补丁只有几十字节，所以用 1 而不是 64）
        using var http = new TestHttpServer(server) { TruncateBytes = 1 };
        var cfg = new UpdateConfig { RootPath = client, ManifestUrl = http.BaseUrl + "manifest.xml" };

        var result = new ClientUpdater(cfg).Run();

        // 服务端支持 Range，客户端应当用「续传」把缺的那截补回来（§4.6），而不是整包重下
        Assert.True(result.Outcome == UpdateOutcome.Updated, $"应当靠续传恢复，实得 {result}; Detail={result.Detail}");
        Assert.Equal(3, result.Patched);
        Assert.Equal(0, result.Full);
        Assert.True(result.BytesDownloaded < 20_000, $"不该退化成整包重下：{result.BytesDownloaded} 字节");
        TestSupport.AssertSameAs(gen.Manifest, v2, client);
    }

    [Fact]
    public void TruncationThatDeliversNoBytes_IsRetriedAsAnExplicitRangeRequest()
    {
        using var tmp = new TempDir();
        var (v1, v2, m1, m2, gen) = Prepare(tmp);
        var server = PrepareServer(tmp, m1, m2, v1, v2);
        var client = PrepareClient(tmp, v1, m1);

        // I-1 的实验室复现：**不带 Range** 的响应一个字节都不发（模拟真实链路上小响应被整块
        // 缓冲在传输层，连接截断的异常先于任何字节到达读循环），带 Range 的续传照常发全。
        // 老客户端在这里会三次都发同一个不带 Range 的 GET，次次空手而归 → download_failed；
        // 修好之后第二次尝试会显式带上 `Range: bytes=0-`，于是换上另一条路走通。
        // 注：本地明文下 .NET 可能"干净地"返回 0 字节而不是抛异常，两条路客户端都要能兜住 ——
        // 一条靠 catch（一个字节都没落地），一条靠"声明长度 > 实收长度"的核对。
        using var http = new TestHttpServer(server)
        {
            TruncateBytes = 1, DropBodyEntirely = true, TruncateScope = TruncateScope.WithoutRange,
        };
        var cfg = new UpdateConfig { RootPath = client, ManifestUrl = http.BaseUrl + "manifest.xml" };

        var result = new ClientUpdater(cfg).Run();

        Assert.True(result.Outcome == UpdateOutcome.Updated,
            $"应当靠显式 bytes=0- 的重试恢复，实得 {result}; Detail={result.Detail}");
        TestSupport.AssertSameAs(gen.Manifest, v2, client);

        // 关键断言：`.part` 里一个字节都没有的时候，重试必须**换请求形态**（显式 bytes=0-），
        // 而不是把第一次原样重放（重放就是老 bug）。
        Assert.Contains("bytes=0-", http.RequestRanges);
    }

    [Fact]
    public void TruncatedPayload_WithoutRangeSupport_EndsAsFailed_WithoutCorruption()
    {
        using var tmp = new TempDir();
        var (v1, v2, m1, m2, _) = Prepare(tmp);
        var server = PrepareServer(tmp, m1, m2, v1, v2);
        var client = PrepareClient(tmp, v1, m1);

        // 服务端不支持 Range → 截断无法补救。这里必须用 Every：默认的 FirstOnly 只坑第一发，
        // 而客户端现在会用显式 bytes=0- 重试，服务端哪怕不认 Range 也会正常发全 —— 那就变成"能补救"了。
        using var http = new TestHttpServer(server)
        {
            TruncateBytes = 1, SupportRange = false, TruncateScope = TruncateScope.Every,
        };
        var cfg = new UpdateConfig { RootPath = client, ManifestUrl = http.BaseUrl + "manifest.xml" };

        var result = new ClientUpdater(cfg).Run();

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

        var result = new ClientUpdater(cfg).Run();

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
            var result = new ClientUpdater(cfg).Run();
            Assert.True(result.FailedCount > 0, "被占用的文件必须计入失败");
            Assert.NotEqual(UpdateOutcome.Updated, result.Outcome);
            Assert.NotEqual(TestSupport.Md5File(m2), TestSupport.Md5File(Path.Combine(client, "manifest.xml")));
        }

        // 释放后重跑应当成功
        var again = new ClientUpdater(cfg).Run();
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

        var result = new ClientUpdater(cfg).Run();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.ManifestUnavailable, result.Reason);
        Assert.False(string.IsNullOrEmpty(result.Detail));   // 必须给出可诊断的原因
    }

    [Fact]
    public void UnreachableServer_FailsFast_WithADiagnosableReason_WithoutTouchingAnything()
    {
        using var tmp = new TempDir();
        var (v1, _, m1, _, _) = Prepare(tmp);
        var client = PrepareClient(tmp, v1, m1);

        // 1 号端口上不会有人监听：连接被立刻拒绝
        var cfg = new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = "http://127.0.0.1:1/manifest.xml",
            SessionTimeout = TimeSpan.FromSeconds(20),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new ClientUpdater(cfg).Run();   // 不得抛异常（§4.12 绝不抛）
        sw.Stop();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.ManifestUnavailable, result.Reason);
        Assert.False(string.IsNullOrEmpty(result.Detail));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"取不到清单必须快速收敛，实测 {sw.Elapsed}");

        // 一个字节都不该动：本地清单还是旧的
        Assert.Equal(TestSupport.Md5File(m1), TestSupport.Md5File(Path.Combine(client, "manifest.xml")));
    }

    /// <summary>
    /// "磁盘满 / 权限不足"这一类**写不进去**的故障：用一个同名目录占住目标路径来稳定复现
    /// （拿 ACL 或真把盘写满都不可靠）。要点是：必须归到"IO/权限"这一类具体原因，
    /// 且**不得**把本地清单写成"已最新"。
    /// </summary>
    [Fact]
    public void UnwritableTarget_IsReportedAsIoFailure_WithoutCorruptingTheRest()
    {
        using var tmp = new TempDir();
        var (v1, v2, m1, m2, _) = Prepare(tmp);
        var server = PrepareServer(tmp, m1, m2, v1, v2);
        var client = PrepareClient(tmp, v1, m1);

        // 把 f0.bin 换成一个同名目录：文件替换必然失败
        File.Delete(Path.Combine(client, "f0.bin"));
        Directory.CreateDirectory(Path.Combine(client, "f0.bin"));

        using var http = new TestHttpServer(server);
        var cfg = new UpdateConfig { RootPath = client, ManifestUrl = http.BaseUrl + "manifest.xml" };

        var result = new ClientUpdater(cfg).Run();

        Assert.NotEqual(UpdateOutcome.Updated, result.Outcome);
        Assert.True(result.FailedCount > 0, "写不进去必须计入失败");

        // 其余文件照常更新，且逐字节正确
        foreach (var name in new[] { "f1.bin", "f2.bin" })
            Assert.Equal(TestSupport.Md5File(Path.Combine(v2, name)), TestSupport.Md5File(Path.Combine(client, name)));

        // 本地清单不得被写成新版（否则下次会误判"已最新"）
        Assert.NotEqual(TestSupport.Md5File(m2), TestSupport.Md5File(Path.Combine(client, "manifest.xml")));

        // 失败原因必须是"IO/权限"这一类（status=4），不是笼统的 9
        var log = File.ReadAllLines(Path.Combine(client, "UpdaterCache", "update.log"));
        var failed = log.Where(l => l.StartsWith("F\t", StringComparison.Ordinal) && l.Split('\t')[8] != "0").ToList();
        Assert.Single(failed);
        Assert.Equal("4", failed[0].Split('\t')[8]);
    }
}