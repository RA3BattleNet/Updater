using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Server;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// **一个内容对被多条清单条目共享**：服务端按内容对给补丁命名（一个内容对只产出一个 `.bin`），
/// 客户端按条目处理，两条条目算出的补丁路径与中转路径是**同一个文件**。
///
/// 这里钉的就是这个共享对象上的**两半**：
/// ① **并发相撞**（本文件第一个用例）：4 条条目共享一个内容对、并发 ≥2 时，
///    谁都不许因为撞车而回落完整下载（那会让"打补丁"悄悄变成"下全量"），谁都不许往外抛异常，
///    该有的日志行必须写全（每条一个 F 行、整轮一个 R 行）；
/// ② **外来的占位者**（第二个用例）：中转文件被别的句柄独占时，补丁这条路上抛出的 IO 异常
///    必须就地转成"这个文件打不上补丁"→ 回落完整下载，而不是冲出库（§4.12 绝不抛异常）。
///
/// 【为何不用真实树】：真实 CoronaLauncher v2→v3 里那 1 组共享对被埋在 481 条补丁中间，要靠时序撞上；
/// 而"4 条待办 + 起始并发 2"时它们**同时起飞**，窗口就是整个 hpatchz 执行时间 —— 可靠得多，
/// 而且不依赖 `UPDATER_E2E_TREES` 这类门控环境变量（那条路在 CI 上是静默跳过的）。
/// </summary>
public class PatchPairSharingTests
{
    /// <summary>同一份内容放 4 个路径 → v1 里它们内容相同；v2 里它们变成另一份相同内容。</summary>
    private static readonly string[] Duplicates =
        ["bin/dup1.dll", "bin/dup2.dll", "bin/dup3.dll", "bin/dup4.dll"];

    /// <summary>4 MB 级内容：给 hpatchz 留出足够长的执行窗口（小文件会让竞态窗口太窄、测不稳）。</summary>
    private const int PayloadSize = 4 * 1024 * 1024;

    [Fact]
    public void SharedContentPair_WithConcurrency_IsPatchedForEveryEntry_WithoutThrowing()
    {
        using var tmp = new TempDir();

        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");
        var oldContent = TestSupport.Big("OLD", PayloadSize);
        var newContent = TestSupport.Big("NEW", PayloadSize);
        foreach (var rel in Duplicates)
        {
            TestSupport.WriteTree(v1, (rel, oldContent));
            TestSupport.WriteTree(v2, (rel, newContent));
        }

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        ManifestGenerator.Generate(v2, m1, [], oldRoot: v1).Manifest.SaveToXml(m2);

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 0);
        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);

        // 服务端确实只产出 1 个补丁（内容对去重）—— 这正是共享的来源
        Assert.Single(Directory.GetFiles(Path.Combine(server, "patches")));

        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);

        using var http = new TestHttpServer(server);
        var cfg = new UpdateConfig
        {
            RootPath = client,
            CacheDir = TestSupport.TestCacheDir(client),
            ManifestUrl = http.BaseUrl + "manifest.xml",
            MaxConcurrency = 4,          // 4 条共享同一个内容对的条目同时起飞
        };

        var result = new ClientUpdater(cfg).Run();     // 出异常就直接是 FAIL（契约断言）

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(4, result.Patched);
        // 【必须】不许因撞车而回落完整下载：4 条都该走补丁
        Assert.Equal(0, result.Full);

        foreach (var rel in Duplicates)
            Assert.Equal(TestSupport.Md5(newContent),
                TestSupport.Md5File(Path.Combine(client, rel.Replace('/', Path.DirectorySeparatorChar))));

        // 日志契约：4 条各一行 F（都是 patch）、整轮一行 R
        var log = File.ReadAllLines(Path.Combine(TestSupport.TestCacheDir(client), "update.log"));
        var f = log.Where(l => l.StartsWith("F\t", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, f.Count);
        Assert.All(f, l => Assert.Equal("patch", l.Split('\t')[7]));
        var r = log.LastOrDefault(l => l.StartsWith("R\t", StringComparison.Ordinal));
        Assert.NotNull(r);
        Assert.Equal(nameof(UpdateOutcome.Updated), r!.Split('\t')[11]);
    }

    /// <summary>
    /// 同一根因的另一半：补丁这条路上除了"两个兄弟任务抢同一个中转文件"，还有**外来的占位者**
    /// （杀软扫描器、清理工具、上一次被强杀的进程留下的句柄…），一样会让 `Fs.Delete(outPath)` 抛
    /// 共享冲突（IOException，HResult 32）。库契约是绝不抛异常（§4.12），所以这种意外必须就地
    /// 转成"这个文件打不上补丁"→ 回落完整下载：用户最终拿到**正确的文件**，而不是一个崩掉的更新。
    ///
    /// 【为何注入而不是等它自己发生】靠时序撞出来的竞态只能证明"能撞到"，证明不了"撞到之后一定收得住"。
    /// 这里由测试进程自己用 <see cref="FileShare.None"/> 把 `patches/{old}_{new}.bin.out` 占住 ——
    /// 抛出点、异常类型、抛出时机**全部确定**，"收得住"于是成了可断言的事。
    /// 【阴性对照】抽掉 Patch 分支的兜底捕获，本用例会以 IOException 直接失败（见 S-8 的对照记录）。
    /// </summary>
    [Fact]
    public void PatchScratch_WhenHeldByAnotherHandle_FallsBackToFullDownload_WithoutThrowing()
    {
        using var tmp = new TempDir();

        const string rel = "bin/solo.dll";
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");
        var oldContent = TestSupport.Big("OLD", PayloadSize);
        var newContent = TestSupport.Big("NEW", PayloadSize);
        TestSupport.WriteTree(v1, (rel, oldContent));
        TestSupport.WriteTree(v2, (rel, newContent));

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

        using var http = new TestHttpServer(server);
        var cfg = new UpdateConfig
        {
            RootPath = client,
            CacheDir = TestSupport.TestCacheDir(client),
            ManifestUrl = http.BaseUrl + "manifest.xml",
            MaxConcurrency = 1,
        };

        // 布现场：补丁本体先放进缓存（省掉下载，让流程**确定地**走到中转文件那一步），
        // 而中转文件被另一个句柄独占着。
        var cacheDir = Path.GetFullPath(cfg.ResolveCacheDir());
        Directory.CreateDirectory(cacheDir);
        var serverPatch = Directory.GetFiles(Path.Combine(server, "patches")).Single();
        var patchPath = Path.Combine(cacheDir, Path.GetFileName(serverPatch));
        File.Copy(serverPatch, patchPath, overwrite: true);
        var outPath = patchPath + ".out";
        File.WriteAllText(outPath, "被另一个句柄占着");
        using var holder = new FileStream(outPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = new ClientUpdater(cfg).Run();     // 出异常就直接是 FAIL（契约断言）

        // 回落成功 ⇒ 这一轮**仍然是成功的**：文件换对了，用户看到的不是失败
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.Patched);
        Assert.Equal(1, result.Full);
        Assert.Equal(TestSupport.Md5(newContent), TestSupport.Md5File(Path.Combine(client, "bin", "solo.dll")));

        // 日志要如实说明"为什么没打上补丁"：full + file_in_use（不是 patch_failed，更不是崩了）
        var log = File.ReadAllLines(Path.Combine(TestSupport.TestCacheDir(client), "update.log"));
        var f = log.Single(l => l.StartsWith("F\t", StringComparison.Ordinal)).Split('\t');
        Assert.Equal("full", f[7]);
        Assert.Equal(UpdateReasons.FileInUse, f[9]);
    }
}