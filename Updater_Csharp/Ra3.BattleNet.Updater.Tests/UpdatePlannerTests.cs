using Ra3.BattleNet.Updater.Core;
using Ra3.BattleNet.Updater.Share.Models;

namespace Ra3.BattleNet.Updater.Tests;

public class UpdatePlannerTests
{
    private static UpdateConfig Cfg(string root) => new() { RootPath = root, ManifestUrl = "http://localhost/manifest.xml" };

    [Fact]
    public void Classifies_Skip_Move_Patch_Full()
    {
        var local = TestSupport.NewManifest();
        TestSupport.Add(local, "a.txt", "\\", TestSupport.Md5("A"));
        TestSupport.Add(local, "b.txt", "\\", TestSupport.Md5("B1"));
        TestSupport.Add(local, "old.txt", "\\", TestSupport.Md5("MOVE"));

        var remote = TestSupport.NewManifest("2.0.0");
        TestSupport.Add(remote, "a.txt", "\\", TestSupport.Md5("A"));       // 内容一致 → Skip
        TestSupport.Add(remote, "b.txt", "\\", TestSupport.Md5("B2"));      // 同路径内容变 → Patch
        TestSupport.Add(remote, "moved.txt", "\\", TestSupport.Md5("MOVE"));// 内容已在本地 → Move
        TestSupport.Add(remote, "new.txt", "\\", TestSupport.Md5("NEW"));   // 无前身 → Full

        var plan = UpdatePlanner.Build(remote, local, Cfg(@"C:\root"));

        Assert.Equal(PlanAction.Skip, Find(plan, "a.txt").Action);
        Assert.Equal(PlanAction.Patch, Find(plan, "b.txt").Action);
        Assert.Equal(PlanAction.Move, Find(plan, "moved.txt").Action);
        Assert.Equal(PlanAction.Full, Find(plan, "new.txt").Action);
        Assert.Equal(3, plan.ToProcess);
        Assert.Equal(2, plan.ToDownload);
    }

    [Fact]
    public void SamePathSameContent_IsSkip_NotMove()
    {
        var local = TestSupport.NewManifest();
        TestSupport.Add(local, "a.txt", "\\", TestSupport.Md5("A"));

        var remote = TestSupport.NewManifest("2.0.0");
        TestSupport.Add(remote, "a.txt", "\\", TestSupport.Md5("A"));

        var plan = UpdatePlanner.Build(remote, local, Cfg(@"C:\root"));
        Assert.Equal(PlanAction.Skip, plan.Entries[0].Action);
        Assert.Equal(0, plan.ToProcess);
    }

    [Fact]
    public void UuidMatch_IsTheOnlyIdentity_SoARenameWithChangedContentStillPatches()
    {
        // 场景：路径变了 + 内容也变了，服务端在发布时把新条目的 UUID 改成了旧条目的（AGENT.md §3.4 / §9.2）
        var local = TestSupport.NewManifest();
        var old = TestSupport.Add(local, "old.txt", "\\", TestSupport.Md5("V1"));

        var remote = TestSupport.NewManifest("2.0.0");
        var moved = TestSupport.Add(remote, "new.txt", "\\", TestSupport.Md5("V2"));
        moved.UUID = old.UUID;

        var plan = UpdatePlanner.Build(remote, local, Cfg(@"C:\root"));
        var entry = plan.Entries[0];

        Assert.Equal(PlanAction.Patch, entry.Action);
        Assert.Equal(TestSupport.Md5("V1"), entry.PredecessorHash);
        Assert.EndsWith("old.txt", entry.PredecessorPath);
    }

    /// <summary>
    /// 【已决策 2026-09-28】文件身份**只有 UUID**：同路径、同内容，但 UUID 不同 → 视为**新文件**。
    /// 客户端**不替服务端补正失误**（同路径换 UUID 是发布侧该维护好的事）；
    /// 也不允许拿"同路径的本地文件"当补丁前身（猜错就是把两个不同文件接在一起）。
    /// 正常流程不受影响：服务端保证"同路径 ⇒ 同 UUID"（路径派生 + 同路径继承）。
    /// </summary>
    [Fact]
    public void DifferentUuid_IsANewFile_EvenAtTheSamePathAndSameContent()
    {
        var local = TestSupport.NewManifest();
        TestSupport.Add(local, "same-content.txt", "\\", TestSupport.Md5("A"));
        TestSupport.Add(local, "changed.txt", "\\", TestSupport.Md5("B1"));

        var remote = TestSupport.NewManifest("2.0.0");
        TestSupport.Add(remote, "same-content.txt", "\\", TestSupport.Md5("A")).UUID = Guid.NewGuid();
        TestSupport.Add(remote, "changed.txt", "\\", TestSupport.Md5("B2")).UUID = Guid.NewGuid();

        var plan = UpdatePlanner.Build(remote, local, Cfg(@"C:\root"));

        Assert.Equal(PlanAction.Full, plan.Entries[0].Action);   // 内容一模一样，也不许 Skip
        Assert.Equal(PlanAction.Full, plan.Entries[1].Action);   // 更不许借本地文件当补丁前身
        Assert.All(plan.Entries, e => Assert.Null(e.PredecessorPath));
    }

    [Fact]
    public void ModeSkip_IsRespected()
    {
        var remote = TestSupport.NewManifest();
        TestSupport.Add(remote, "skip.txt", "\\", TestSupport.Md5("X"), FileModeEnum.Skip);

        var plan = UpdatePlanner.Build(remote, null, Cfg(@"C:\root"));
        Assert.Equal(PlanAction.Skip, plan.Entries[0].Action);
    }

    [Fact]
    public void ExcludedTopDir_IsNotTouched()
    {
        var remote = TestSupport.NewManifest();
        TestSupport.Add(remote, "userdata.txt", "\\CoronaData\\", TestSupport.Md5("X"));

        var cfg = new UpdateConfig
        {
            RootPath = @"C:\root",
            ManifestUrl = "http://localhost/manifest.xml",
            ExcludedDirs = ["CoronaData"],
        };

        var plan = UpdatePlanner.Build(remote, null, cfg);
        Assert.Equal(PlanAction.Skip, plan.Entries[0].Action);
    }

    private static PlanEntry Find(UpdatePlan plan, string fileName) =>
        plan.Entries.Single(e => e.Target.FileName == fileName);
}

/// <summary>
/// 内容索引（"本地已有同样内容 → 0 下载"）的边界。5 版本模拟在真实版本上撞出来的坑：
/// v5 里同时存在未变的 <c>MapMixer.config</c> 与新增的 <c>MapMixer.exe.config</c>，
/// 两者内容一模一样。旧实现把前者当作后者的"移动来源"，于是
/// **更新报成功、安装目录却少了一个文件**。
/// </summary>
public class ContentIndexTests
{
    private static UpdateConfig Cfg() => new() { RootPath = @"C:\root", ManifestUrl = "http://localhost/manifest.xml" };

    private static PlanAction ActionOf(UpdatePlan plan, string relative) =>
        plan.Entries.Single(e => e.Target.RelativePath() == relative).Action;

    private static (ManifestModel Remote, ManifestModel Local, Guid Uuid) DuplicateContentPair()
    {
        var uuid = Guid.NewGuid();
        var md5 = TestSupport.Md5("SAME-CONTENT");

        var local = TestSupport.NewManifest();
        var localKeep = TestSupport.Add(local, "MapMixer.config", "\\bin\\", md5);
        localKeep.UUID = uuid;

        var remote = TestSupport.NewManifest("2.0.0");
        var remoteKeep = TestSupport.Add(remote, "MapMixer.config", "\\bin\\", md5);
        remoteKeep.UUID = uuid;                                   // 未变（同 UUID 同内容同路径）
        TestSupport.Add(remote, "MapMixer.exe.config", "\\bin\\", md5);   // 新增，UUID 是新的

        return (remote, local, uuid);
    }

    [Fact]
    public void DoesNotStealALocalFileThatThisVersionStillNeeds()
    {
        var (remote, local, _) = DuplicateContentPair();
        var plan = UpdatePlanner.Build(remote, local, Cfg());

        Assert.Equal(PlanAction.Skip, ActionOf(plan, "bin/MapMixer.config"));       // 留着别动
        Assert.Equal(PlanAction.Full, ActionOf(plan, "bin/MapMixer.exe.config"));   // 重新下载，而不是把上面那个搬走
        Assert.DoesNotContain(plan.Entries, e => e.Action == PlanAction.Move);
    }

    [Fact]
    public void StillMovesWhenTheOldPathIsNoLongerNeeded()
    {
        var (_, local, uuid) = DuplicateContentPair();

        var remote = TestSupport.NewManifest("2.1.0");
        var moved = TestSupport.Add(remote, "MapMixer.exe.config", "\\bin\\", TestSupport.Md5("SAME-CONTENT"));
        moved.UUID = uuid;                                        // 生成器会靠 MD5 继承 UUID（纯改名）

        var plan = UpdatePlanner.Build(remote, local, Cfg());
        Assert.Equal(PlanAction.Move, plan.Entries.Single().Action);   // 0 下载
    }

    [Fact]
    public void OneLocalFileCanSatisfyOnlyOneNewTarget()
    {
        var local = TestSupport.NewManifest();
        TestSupport.Add(local, "spare.bin", "\\", TestSupport.Md5("ONLY-ONE-COPY"));

        var remote = TestSupport.NewManifest("2.0.0");
        TestSupport.Add(remote, "copy1.bin", "\\", TestSupport.Md5("ONLY-ONE-COPY"));
        TestSupport.Add(remote, "copy2.bin", "\\", TestSupport.Md5("ONLY-ONE-COPY"));

        var plan = UpdatePlanner.Build(remote, local, Cfg());

        // 本地只有一份，只能满足一个目标；另一个必须老老实实下载
        Assert.Equal(1, plan.Entries.Count(e => e.Action == PlanAction.Move));
        Assert.Equal(1, plan.Entries.Count(e => e.Action == PlanAction.Full));
    }

    [Fact]
    public void DoesNotUseAPatchPredecessorAsAMoveSource()
    {
        // local: old.bin(uuid1, 内容 OLD)  —— 它同时是远端 new.bin 的补丁前身
        // remote: new.bin(uuid1, 内容 NEW) + copy.bin(新 uuid, 内容 OLD)
        // 不能把 old.bin 搬去当 copy.bin，否则 new.bin 的补丁就找不到前身了
        var uuid = Guid.NewGuid();
        var local = TestSupport.NewManifest();
        var pred = TestSupport.Add(local, "old.bin", "\\", TestSupport.Md5("OLD"));
        pred.UUID = uuid;

        var remote = TestSupport.NewManifest("2.0.0");
        var patched = TestSupport.Add(remote, "new.bin", "\\", TestSupport.Md5("NEW"));
        patched.UUID = uuid;
        TestSupport.Add(remote, "copy.bin", "\\", TestSupport.Md5("OLD"));

        var plan = UpdatePlanner.Build(remote, local, Cfg());

        Assert.Equal(PlanAction.Patch, ActionOf(plan, "new.bin"));
        Assert.Equal(PlanAction.Full, ActionOf(plan, "copy.bin"));
    }
}

/// <summary>失败容忍度：max(绝对下限, 比例 × 待处理文件数)。</summary>
public class FailToleranceTests
{
    private static UpdateConfig Cfg() => new() { RootPath = ".", ManifestUrl = "http://localhost/manifest.xml" };

    [Fact]
    public void ScalesWithPlannedFileCount()
    {
        var cfg = Cfg();
        Assert.Equal(5, cfg.EffectiveFailTolerance(0));
        Assert.Equal(5, cfg.EffectiveFailTolerance(3));
        Assert.Equal(5, cfg.EffectiveFailTolerance(40));    // 10% = 4 < 下限 5
        Assert.Equal(10, cfg.EffectiveFailTolerance(100));  // 10% = 10
        Assert.Equal(30, cfg.EffectiveFailTolerance(300));  // 10% = 30
    }

    [Fact]
    public void CanBeTuned()
    {
        var cfg = Cfg() with { MinFailuresForHostFallback = 2, FailRatioForHostFallback = 0.5 };
        Assert.Equal(2, cfg.EffectiveFailTolerance(2));
        Assert.Equal(3, cfg.EffectiveFailTolerance(6));     // 50% = 3 > 下限 2
    }
}