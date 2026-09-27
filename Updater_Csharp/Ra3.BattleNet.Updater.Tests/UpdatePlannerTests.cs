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
    public void UuidMatch_WinsOverPath_SoRenameWithChangedContent_StillPatches()
    {
        // 场景：路径变了 + 内容也变了，人工把新条目的 UUID 改成旧条目的（AGENT.md §3.4 / §9.2）
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
