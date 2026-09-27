using System.Diagnostics;
using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Models;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 真实历史版本端到端。需要环境变量 UPDATER_E2E_TREES 指向解包目录的父目录：
///   <c>CoronaLauncher_Setup_3.12.9269.19502</c> 与 <c>CoronaLauncher_Setup_3.12.9381.2215</c>
/// 未设置时本测试不做任何事（本地/CI 默认不跑这个重活）。
/// 建议同时设置 UPDATER_TEST_TMP 到空间充足的盘。
/// </summary>
public class RealVersionsTests
{
    [Fact]
    public void RealTrees_CrossVersionUpdate_IsIncrementalAndByteExact()
    {
        var root = Environment.GetEnvironmentVariable("UPDATER_E2E_TREES");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            Console.WriteLine("跳过：未设置 UPDATER_E2E_TREES");
            return;
        }

        var oldDir = Path.Combine(root, "CoronaLauncher_Setup_3.12.9269.19502");
        var newDir = Path.Combine(root, "CoronaLauncher_Setup_3.12.9381.2215");
        Assert.True(Directory.Exists(oldDir), $"缺少旧版本目录: {oldDir}");
        Assert.True(Directory.Exists(newDir), $"缺少新版本目录: {newDir}");

        using var tmp = new TempDir();
        var sw = Stopwatch.StartNew();

        var m1 = Path.Combine(tmp.Path, "old.xml");
        ManifestGenerator.Generate(oldDir, null, []).Manifest.SaveToXml(m1);

        var gen = ManifestGenerator.Generate(newDir, m1, [], oldRoot: oldDir);
        var m2 = Path.Combine(tmp.Path, "new.xml");
        gen.Manifest.SaveToXml(m2);
        Console.WriteLine($"清单生成 {sw.Elapsed.TotalSeconds:F1}s；修改 {gen.Modified.Count}，新增 {gen.Added.Count}，移动 {gen.Moved.Count}");

        var server = tmp.Sub("server");
        sw.Restart();
        var patchSummary = PatchGenerator.Generate(m2, newDir, [new Baseline(m1, oldDir)], server);
        Console.WriteLine($"补丁生成 {sw.Elapsed.TotalSeconds:F1}s；新建 {patchSummary.PatchesCreated}，失败 {patchSummary.PatchesFailed}，字节 {patchSummary.PatchBytes:N0}");

        Assert.Equal(0, patchSummary.PatchesFailed);
        Assert.True(patchSummary.PatchesCreated > 300, "真实版本之间应当产生大量补丁");

        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);

        var client = tmp.Sub("client");
        sw.Restart();
        TestSupport.CopyTree(oldDir, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);
        Console.WriteLine($"客户端初始树拷贝 {sw.Elapsed.TotalSeconds:F1}s");

        using var http = new TestHttpServer(server);
        var cfg = new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        };

        sw.Restart();
        var result = new ClientUpdater(cfg).Run();
        Console.WriteLine($"更新完成 {sw.Elapsed.TotalSeconds:F1}s：{result}");

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(0, result.FailedCount);
        Assert.True(result.Patched > 300, $"补丁命中数过低：{result.Patched}");

        // 增量收益：必须显著小于"完全不动"的 125MB 安装包量级
        Assert.True(result.BytesDownloaded < 100L * 1024 * 1024,
            $"下载量过大：{result.BytesDownloaded:N0} 字节");

        TestSupport.AssertSameAs(gen.Manifest, newDir, client);

        var log = File.ReadAllLines(Path.Combine(client, "UpdaterCache", "update.log"));
        Assert.Contains(log, l => l.StartsWith("F\t", StringComparison.Ordinal) && l.Contains("\tpatch\t"));
    }
}
