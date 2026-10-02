using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Server;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// **交回宿主时作废本地清单**（`UpdateOutcome.NeedsHostFallback` → 删掉 `<root>/manifest.xml`）。
///
/// 它解的是一个**自锁**：宿主拿整包把文件换新了、本地清单却还是旧的 → 下一轮算出的差异是
/// "相对那个旧清单"的、恒不缩小 → 保险丝每轮都撞 → 清单永远不前进 → 客户端被**永久钉在旧版本**
/// 上，每轮都走整包。作废之后下一轮走"没有可信基线"那条路（逐文件按磁盘哈希核对）：
/// 树已经对了就直接 `UpToDate` 并写回清单，自锁解除；宿主没动手则看到的是**真实**工作量。
///
/// 同时钉住**不该做**的那一半：普通失败（下载失败、个别文件被占用）必须**保留**清单 ——
/// 它是"续做 + 打补丁"的唯一依据，删掉会把这轮白下的字节变成下一轮的整份下载。
/// </summary>
public class HostFallbackBaselineTests
{
    private static readonly string[] Files = ["data/a.txt", "data/b.txt", "data/c.txt"];
    private static readonly string[] V1 = ["A1", "B1", "C1"];
    private static readonly string[] V2 = ["A2", "B2", "C2"];

    /// <summary>母板里没有补丁（服务端不生成 patches/）——本轮要么撞阈值、要么整份下载，不涉及打补丁。</summary>
    private sealed class World : IDisposable
    {
        public TempDir Tmp = new();
        public string Client = string.Empty;
        public string M1 = string.Empty;
        public string M2 = string.Empty;
        public TestHttpServer Http = null!;
        public UpdateConfig Cfg = null!;

        public string LocalManifest => Path.Combine(Client, "manifest.xml");

        public void Dispose()
        {
            Http.Dispose();
            Tmp.Dispose();
        }
    }

    /// <summary>造世界：v1/v2 三个文件全变；客户端树可选 v1 或 v2，本地清单可选有无（v1 的）。</summary>
    private static World Build(bool treeIsV2 = true, bool withStaleManifest = true)
    {
        var w = new World();
        var v1 = w.Tmp.Sub("v1");
        var v2 = w.Tmp.Sub("v2");
        for (var i = 0; i < Files.Length; i++)
        {
            TestSupport.WriteTree(v1, (Files[i], V1[i]));
            TestSupport.WriteTree(v2, (Files[i], V2[i]));
        }

        w.M1 = Path.Combine(w.Tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(w.M1);
        w.M2 = Path.Combine(w.Tmp.Path, "v2.xml");
        ManifestGenerator.Generate(v2, w.M1, [], oldRoot: v1).Manifest.SaveToXml(w.M2);

        var server = w.Tmp.Sub("server");
        File.Copy(w.M2, Path.Combine(server, "manifest.xml"), overwrite: true);   // 远端 = v2

        w.Client = w.Tmp.Sub("client");
        TestSupport.CopyTree(treeIsV2 ? v2 : v1, w.Client);
        if (withStaleManifest) File.Copy(w.M1, Path.Combine(w.Client, "manifest.xml"), overwrite: true);

        w.Http = new TestHttpServer(server);
        w.Cfg = new UpdateConfig
        {
            RootPath = w.Client,
            CacheDir = TestSupport.TestCacheDir(w.Client),
            ManifestUrl = w.Http.BaseUrl + "manifest.xml",
        };
        return w;
    }

    private static string[] LogLines(World w) =>
        File.ReadAllLines(Path.Combine(TestSupport.TestCacheDir(w.Client), "update.log"));

    private static void AssertTreeIsV2(World w)
    {
        for (var i = 0; i < Files.Length; i++)
            Assert.Equal(TestSupport.Md5(V2[i]),
                TestSupport.Md5File(Path.Combine(w.Client, Files[i].Replace('/', Path.DirectorySeparatorChar))));
    }

    /// <summary>
    /// 本改动要解的那个自锁（用的是"宿主刚用整包换完树、清单还是旧的"这个形态）：
    /// 第一轮撞阈值 → **清单被作废**；第二轮没有基线 → 按磁盘核对 → 全命中 → 直接 `UpToDate`
    /// 并把正确的清单写回去。第三轮起稳定。
    ///
    /// 改动之前这里会**永远**是 `NeedsHostFallback`：清单不动 → 差异恒为 3 → 每轮都撞。
    /// </summary>
    [Fact]
    public void HostFallback_DropsTheStaleBaseline_SoTheNextRunHealsItself()
    {
        using var w = Build();
        var cfg = w.Cfg with { FullPackageThresholdFiles = 2 };   // 差异 3 > 2 → 撞

        var first = new ClientUpdater(cfg).Run();
        Assert.Equal(UpdateOutcome.NeedsHostFallback, first.Outcome);
        Assert.Equal(UpdateReasons.WorkloadTooLarge, first.Reason);
        Assert.Equal(0, first.BytesDownloaded);

        // ① 清单被作废（这就是本改动的核心事实）
        Assert.False(File.Exists(w.LocalManifest), "交回宿主后本地清单必须被作废，否则下一轮还是拿旧基线算");
        // ② 而且日志里留了证据（C 行：上下文行型，§4.11 允许）
        Assert.Contains(LogLines(w), l => l.StartsWith("C\t", StringComparison.Ordinal) && l.Contains("\tbaseline\tdropped:policy"));
        // ③ 树一个字节都没动
        AssertTreeIsV2(w);

        // ④ 下一轮：没有基线 → 逐文件按磁盘核对 → 全命中 → 直接已最新，并把正确的清单写回
        var second = new ClientUpdater(cfg).Run();
        Assert.Equal(UpdateOutcome.UpToDate, second.Outcome);
        Assert.Equal(0, second.FailedCount);
        Assert.Equal(0, second.BytesDownloaded);
        Assert.Equal(0, second.Patched + second.Full);
        Assert.True(File.Exists(w.LocalManifest), "全命中时必须把远端清单原文写成本地清单");
        Assert.Equal(File.ReadAllBytes(w.M2), File.ReadAllBytes(w.LocalManifest));
        AssertTreeIsV2(w);

        // ⑤ 自锁解除之后再跑一轮：稳定在"已最新"，不会退回交回宿主
        var third = new ClientUpdater(cfg).Run();
        Assert.Equal(UpdateOutcome.UpToDate, third.Outcome);
        Assert.Equal(0, third.BytesDownloaded);
    }

    /// <summary>
    /// 另一个 `NeedsHostFallback` 来路（失败收敛：连完整下载都失败的文件够多 → 本地状态不可信）
    /// 也要作废清单；原因码如实记成 `local_corrupt`。
    /// </summary>
    [Fact]
    public void HostFallback_OnTooManyDownloadFailures_DropsTheBaselineToo()
    {
        using var w = Build(treeIsV2: false);                      // 树是 v1，本地清单也是 v1（一致）
        // 内容地址指向一个没人监听的端口：清单能取到，内容全失败（可重试类失败）
        var cfg = w.Cfg with
        {
            BaseUrl = "http://127.0.0.1:58999/",
            MinFailuresForHostFallback = 1,                        // 3 个失败 ≥ 1 → 判定本地不可信
        };

        var r = new ClientUpdater(cfg).Run();

        Assert.Equal(UpdateOutcome.NeedsHostFallback, r.Outcome);
        Assert.Equal(UpdateReasons.LocalCorrupt, r.Reason);
        Assert.Equal(Files.Length, r.FailedCount);
        Assert.False(File.Exists(w.LocalManifest), "交回宿主就必须作废清单，理由是哪条都一样");
        Assert.Contains(LogLines(w), l => l.StartsWith("C\t", StringComparison.Ordinal) && l.Contains("\tbaseline\tdropped:local_corrupt"));
    }

    /// <summary>
    /// **不该做的那一半**：普通失败（`Failed`，还没到"交回宿主"）必须保留清单 ——
    /// 它是续做与打补丁的唯一依据。删掉它，下一轮就只能是"整份下载"而不是"接着打补丁"。
    /// </summary>
    [Fact]
    public void PlainFailure_KeepsTheBaseline_ForResumeAndPatches()
    {
        using var w = Build(treeIsV2: false);                      // 树与清单都是 v1，远端是 v2 → 该更新
        var before = TestSupport.Md5File(w.LocalManifest);
        var cfg = w.Cfg with { BaseUrl = "http://127.0.0.1:58999/" };   // 内容全失败

        var r = new ClientUpdater(cfg).Run();

        Assert.Equal(UpdateOutcome.Failed, r.Outcome);
        Assert.Equal(UpdateReasons.DownloadFailed, r.Reason);
        Assert.Equal(Files.Length, r.FailedCount);                 // 3 < 容忍度 5 → 不是"交回宿主"
        Assert.True(File.Exists(w.LocalManifest), "普通失败绝不能把清单删掉：那是续做+补丁的依据");
        Assert.Equal(before, TestSupport.Md5File(w.LocalManifest));
        Assert.DoesNotContain(LogLines(w), l => l.StartsWith("C\t", StringComparison.Ordinal) && l.Contains("\tbaseline\t"));
    }

    /// <summary>
    /// 边界：压根没有清单可删时（且关掉了无基线合成）→ 静默跳过、不抛异常，且**照旧**交回宿主。
    /// 关掉 `AdoptLocalTreeWhenNoBaseline` 就等于放弃这条自救路（§4.4 的实施约束）。
    /// </summary>
    [Fact]
    public void HostFallback_WithNoBaselineToDrop_IsANoOp_AndNeverThrows()
    {
        using var w = Build(treeIsV2: true, withStaleManifest: false);
        var cfg = w.Cfg with
        {
            FullPackageThresholdFiles = 2,
            AdoptLocalTreeWhenNoBaseline = false,      // 关掉合成基线：3 个文件全算待下载 → 撞
        };

        var r = new ClientUpdater(cfg).Run();          // 不该抛

        Assert.Equal(UpdateOutcome.NeedsHostFallback, r.Outcome);
        Assert.Equal(UpdateReasons.WorkloadTooLarge, r.Reason);
        Assert.Equal(0, r.BytesDownloaded);
        Assert.False(File.Exists(w.LocalManifest));
        // 没有基线可删 → 不该记"作废"（只记事实，不编事实）
        Assert.DoesNotContain(LogLines(w), l => l.StartsWith("C\t", StringComparison.Ordinal) && l.Contains("\tbaseline\t"));
    }
}