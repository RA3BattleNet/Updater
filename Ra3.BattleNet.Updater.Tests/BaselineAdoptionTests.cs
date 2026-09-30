using System.Text;
using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Server;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// B-2 / AGENT.md §4.10：**没有可信基线**（本地清单不存在，或它坏了）时不要盲目全量。
/// 用库自己的口径逐个核对受管文件的磁盘哈希，把"磁盘上已经等于远端"的那些判成「无需更新」。
///
/// 三条要钉住的性质：
///   ① **绝不伪造版本声明**：合成出来的基线只影响**本次计划**、不落盘；本地清单仍然只在
///      "逐文件哈希验证通过"或"落地成功"之后才写，写的永远是远端原文；
///   ② **保险丝看到的是真实工作量**：没有基线时计划里每个文件都是「完整下载」，于是工作量保险丝
///      （§4.4）会先一步折回宿主整包 —— 而树明明已经对。合成基线发生在**规划之前**，它才看到真的；
///   ③ **有可信基线时本路径不生效**（绝不用磁盘推断去覆盖一个存在的基线）。
/// </summary>
public class BaselineAdoptionTests
{
    private sealed record Env(string Client, string Server, string V1Dir, string V2Dir, string M1, string M2,
        TestHttpServer Http);

    /// <summary>v1 → v2：<c>bin/a.dll</c> 与 <c>data/x.dat</c> 变、<c>bin/b.dll</c> 不变、<c>bin/new.dll</c> 新增。</summary>
    private static Env Prepare(TempDir tmp)
    {
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1,
            ("bin/a.dll", TestSupport.Big("A1")),
            ("bin/b.dll", TestSupport.Big("B1")),
            ("data/x.dat", TestSupport.Big("X1")));

        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2,
            ("bin/a.dll", TestSupport.Big("A2")),
            ("bin/b.dll", TestSupport.Big("B1")),
            ("data/x.dat", TestSupport.Big("X2")),
            ("bin/new.dll", TestSupport.Big("NEW")));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        ManifestGenerator.Generate(v2, m1, [], oldRoot: v1).Manifest.SaveToXml(m2);

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 0);
        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);

        var client = tmp.Sub("client");     // 故意**不放**本地清单：这就是"没有可信基线"
        return new Env(client, server, v1, v2, m1, m2, new TestHttpServer(server));
    }

    private static UpdateConfig Cfg(Env e) => new()
    {
        RootPath = e.Client,
        CacheDir = TestSupport.TestCacheDir(e.Client),
        ManifestUrl = e.Http.BaseUrl + "manifest.xml",
    };

    private static string Rel(Env e, string rel) =>
        Path.Combine(e.Client, rel.Replace('/', Path.DirectorySeparatorChar));

    private static void AssertLocalManifestIsRemoteVerbatim(Env e) =>
        Assert.Equal(TestSupport.Md5File(e.M2), TestSupport.Md5File(Path.Combine(e.Client, "manifest.xml")));

    /// <summary>受管文件全都在磁盘上等于远端（树就是 v2）→ 一次下载都不该发生。</summary>
    [Fact]
    public void AllManagedFilesAlreadyMatch_WithoutALocalManifest_IsUpToDateWithZeroDownload()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;
        TestSupport.CopyTree(e.V2Dir, e.Client);

        var result = new ClientUpdater(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Equal(0, result.BytesDownloaded);
        Assert.Equal(0, result.Patched);
        Assert.Equal(0, result.Full);
        Assert.Equal(result.Total, result.Skipped);            // 全部按磁盘哈希命中
        AssertLocalManifestIsRemoteVerbatim(e);                // 验证通过之后才写，且是远端原文
        Assert.True(File.Exists(Path.Combine(TestSupport.TestCacheDir(e.Client), "manifest.etag")),
            "全部命中时也要落 ETag，否则下一次又要完整取一遍清单");
    }

    /// <summary>
    /// 触发条件的**另一半**：本地清单存在但坏了（解析失败 ⇒ 同样没有可信基线）。
    /// 树逐字节等于远端时，必须被治成 `UpToDate` 并把坏清单换成远端原文 ——
    /// 而不是"本地不可信，那就全量重下"。
    /// </summary>
    [Fact]
    public void CorruptLocalManifest_WithTheTreeAlreadyMatching_IsHealedToUpToDate()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;
        TestSupport.CopyTree(e.V2Dir, e.Client);
        File.WriteAllText(Path.Combine(e.Client, "manifest.xml"), "<Metadata><broken", new UTF8Encoding(false));

        var result = new ClientUpdater(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Equal(0, result.BytesDownloaded);
        Assert.Equal(result.Total, result.Skipped);
        AssertLocalManifestIsRemoteVerbatim(e);       // 坏清单被**远端原文**替换（不是我们自己拼一份）
    }

    /// <summary>
    /// 暂存模式下的全部命中**必须直接收尾**：若让它走完流程，会写出一个**空计划**并返回 `Staged`，
    /// 宿主于是提示"需要重启" —— 用户白重启一次而什么都没发生。
    /// </summary>
    [Fact]
    public void AllManagedFilesAlreadyMatch_InStagedMode_IsUpToDate_NotAStagedRestartRequest()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;
        TestSupport.CopyTree(e.V2Dir, e.Client);

        var result = new ClientUpdater(Cfg(e) with { ApplyMode = ApplyMode.Staged }).Run();

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.False(result.PendingRestart, "没有东西要落地，不该让宿主以为需要重启");
        Assert.False(StageLayout.HasPendingPlan(e.Client));     // 也没写出空计划给 applier
        AssertLocalManifestIsRemoteVerbatim(e);
    }

    /// <summary>3/4 命中：只有那 1 个没命中的要下，命中的进计划当 skip；没命中的**不试补丁**（没有可信前身）。</summary>
    [Fact]
    public void PartiallyMatching_AdoptsTheMatchingFiles_AndOnlyDownloadsTheRest()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;
        TestSupport.CopyTree(e.V2Dir, e.Client);
        File.WriteAllText(Rel(e, "data/x.dat"), "corrupted", new UTF8Encoding(false));

        var stages = new List<UpdateProgress>();
        var result = new ClientUpdater(Cfg(e)).Run(new SyncProgress<UpdateProgress>(stages.Add));

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(4, result.Total);
        Assert.Equal(3, result.Skipped);
        Assert.Equal(1, result.Full);
        Assert.Equal(0, result.Patched);
        Assert.Equal(new FileInfo(Path.Combine(e.V2Dir, "data", "x.dat")).Length, result.BytesDownloaded);
        AssertLocalManifestIsRemoteVerbatim(e);

        // 核对期间用既有的 check 阶段上报（不新增枚举）：宿主能显示"正在核对本地文件 N/M"
        Assert.Contains(stages, p => p.Stage == UpdateStage.Check && p.Total == 4);

        // 【事实】逐文件进度：Stage 只放阶段标识，动作名走 Action；收尾报一次 done（AGENT.md §4.7）
        Assert.Contains(stages, p => p.Stage == UpdateStage.Download && p.Action == "full");
        Assert.Equal(UpdateStage.Done, stages[^1].Stage);
        Assert.DoesNotContain(stages, p => p.Stage is "full" or "patch" or "move" or "skip");
    }

    /// <summary>完全不一致（空树）→ 与今天一致：全量，一个 skip 都不该凭空出现。</summary>
    [Fact]
    public void NothingMatches_BehavesLikeToday_EverythingIsAFullDownload()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        var result = new ClientUpdater(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(4, result.Total);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(4, result.Full);
        AssertLocalManifestIsRemoteVerbatim(e);
    }

    /// <summary>有可信本地清单 → 本路径不生效。可观察的判据：补丁打上了（磁盘推断出来的基线没有前身，只能整份下）。</summary>
    [Fact]
    public void WithAnIntactLocalManifest_TheAdoptionPathIsNotTaken()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;
        TestSupport.CopyTree(e.V1Dir, e.Client);
        File.Copy(e.M1, Path.Combine(e.Client, "manifest.xml"), overwrite: true);

        var result = new ClientUpdater(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.True(result.Patched > 0, "有可信基线时必须走补丁；磁盘推断出来的基线只能整份下");
        Assert.Equal(1, result.Skipped);                       // bin/b.dll：清单说它没变
        AssertLocalManifestIsRemoteVerbatim(e);
    }

    /// <summary>
    /// **保险丝 × 无基线**（§4.4 的实施约束）。同样 3/4 命中 + 阈值 2：
    /// 没有合成基线时 <c>plan.ToDownload</c> 是 4 &gt; 2 → 先一步折回宿主整包；有它才是 1。
    /// </summary>
    [Fact]
    public void Fuse_WithNoBaseline_SeesTheRealWorkload()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;
        TestSupport.CopyTree(e.V2Dir, e.Client);
        File.WriteAllText(Rel(e, "data/x.dat"), "corrupted", new UTF8Encoding(false));

        var result = new ClientUpdater(Cfg(e) with { FullPackageThresholdFiles = 2 }).Run();

        Assert.NotEqual(UpdateOutcome.NeedsHostFallback, result.Outcome);
        Assert.NotEqual(UpdateReasons.WorkloadTooLarge, result.Reason);
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(1, result.Full);
    }

    /// <summary>上一条的对照：关掉开关，同一棵树、同一个阈值 → 折回宿主，而且一个字节都不下（保险丝在任何下载之前）。</summary>
    [Fact]
    public void Fuse_WithTheAdoptionSwitchedOff_DoesFallBackToTheHost()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;
        TestSupport.CopyTree(e.V2Dir, e.Client);
        File.WriteAllText(Rel(e, "data/x.dat"), "corrupted", new UTF8Encoding(false));

        var result = new ClientUpdater(Cfg(e) with
        {
            FullPackageThresholdFiles = 2,
            AdoptLocalTreeWhenNoBaseline = false,
        }).Run();

        Assert.Equal(UpdateOutcome.NeedsHostFallback, result.Outcome);
        Assert.Equal(UpdateReasons.WorkloadTooLarge, result.Reason);
        Assert.Equal(0, result.BytesDownloaded);
    }
}