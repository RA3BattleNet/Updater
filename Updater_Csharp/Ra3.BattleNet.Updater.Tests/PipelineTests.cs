using System.Diagnostics;
using Ra3.BattleNet.Updater.Server;
using CoreUpdater = Ra3.BattleNet.Updater.Core.Updater;
using Ra3.BattleNet.Updater.Core;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 寻址规则（AGENT.md §3.3）与并发行为（§4.6）。
/// 这两件事的共同点是：**出错了也不报错**，只会静默地永远全量下载 / 慢慢地跑。
/// 所以必须有断言把它们钉住。
/// </summary>
public class PipelineTests
{
    [Fact]
    public void PatchAndFileUrls_FollowTheSpec_WithOldHashFirst()
    {
        using var tmp = new TempDir();

        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("a.bin", TestSupport.Big("V1")));
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2, ("a.bin", TestSupport.Big("V2")), ("new.txt", "brand new"));

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

        var oldMd5 = TestSupport.Md5File(Path.Combine(v1, "a.bin"));
        var newMd5 = TestSupport.Md5File(Path.Combine(v2, "a.bin"));
        var addedMd5 = TestSupport.Md5File(Path.Combine(v2, "new.txt"));

        using var http = new TestHttpServer(server);
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);

        var paths = http.RequestPaths;

        // 三种资源都能从 BaseUrl 直接推导（§3.2）；`.bin` 后缀是为了进 CF 的默认缓存白名单
        Assert.Contains("/manifest.xml", paths);
        Assert.Contains($"/patches/{oldMd5}_{newMd5}.bin", paths);
        Assert.Contains($"/files/{addedMd5}.bin", paths);

        // **顺序**：old 在前、new 在后。反过来写不会报错，只会永远不命中补丁。
        var patchPath = paths.Single(p => p.StartsWith("/patches/", StringComparison.Ordinal));
        Assert.Equal($"/patches/{oldMd5}_{newMd5}.bin", patchPath);
        Assert.DoesNotContain($"/patches/{newMd5}_{oldMd5}.bin", paths);
        Assert.DoesNotContain(paths, p => p.Contains("patches.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConcurrentRun_OverlapsNetworkRequests_WhileMaxConcurrencyOneIsStrictlySerial()
    {
        using var tmp = new TempDir();

        var v = tmp.Sub("v");
        var files = Enumerable.Range(0, 6)
            .Select(i => ($"f{i}.bin", TestSupport.Big($"PAYLOAD-{i}", 4096)))
            .ToArray();
        TestSupport.WriteTree(v, files);

        var m = Path.Combine(tmp.Path, "v.xml");
        ManifestGenerator.Generate(v, null, []).Manifest.SaveToXml(m);

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m, v, [], server);
        File.Copy(m, Path.Combine(server, "manifest.xml"), overwrite: true);

        using var http = new TestHttpServer(server) { ArtificialDelayMs = 150 };

        var (serial, serialPeak, serialMs) = RunOnce(tmp, http, maxConcurrency: 1);
        var (parallel, parallelPeak, parallelMs) = RunOnce(tmp, http, maxConcurrency: 4);

        Assert.Equal(UpdateOutcome.Updated, serial.Outcome);
        Assert.Equal(UpdateOutcome.Updated, parallel.Outcome);
        Assert.Equal(6, serial.Full);
        Assert.Equal(6, parallel.Full);

        // 上限=1 必须是真的串行（否则"限制并发"这个配置项就是假的）
        Assert.Equal(1, serialPeak);

        // 默认配置必须真的并发：起始 2 就会立刻出现两个在飞请求
        Assert.True(parallelPeak >= 2,
            $"期望至少 2 个请求同时在飞，实测峰值 {parallelPeak}（说明退化成了线性执行）");

        // 服务端每个响应都慢 150ms，并发度必须体现在墙钟时间上
        Console.WriteLine($"并发验证：上限1 → {serialMs}ms（在飞峰值 {serialPeak}）；" +
                          $"上限4 → {parallelMs}ms（在飞峰值 {parallelPeak}）");
        Assert.True(parallelMs < serialMs * 0.8,
            $"并发未产生加速：串行 {serialMs}ms vs 并发 {parallelMs}ms（峰值 {parallelPeak}）");
    }

    private static (UpdateResult Result, int Peak, long Ms) RunOnce(TempDir tmp, TestHttpServer http, int maxConcurrency)
    {
        var client = tmp.Sub("client-" + maxConcurrency);
        http.ResetPeak();

        var sw = Stopwatch.StartNew();
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
            MaxConcurrency = maxConcurrency,
        }).Run();
        sw.Stop();

        return (result, http.PeakInFlight, sw.ElapsedMilliseconds);
    }
}
