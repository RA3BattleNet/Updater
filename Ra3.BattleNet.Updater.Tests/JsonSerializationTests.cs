using System.Text.Json;
using Ra3.BattleNet.Updater.Client;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// JSON 改成 **source generation**（Native AOT / 裁剪必需）之后，**线上格式必须逐字不变**：
/// `UpdaterStage/plan.json` 是阶段一与 applier 之间的**跨进程契约**（新库写的计划要能被老 applier 读、
/// 反之亦然），结果 JSON 是宿主/独立壳的机器可读契约。这两个格式一旦漂移，故障都是"静默不落地"级别
/// —— 所以这里逐项钉住格式，而不是只测"能往返"。
/// </summary>
public class JsonSerializationTests
{
    [Fact]
    public void Plan_OnDiskFormat_IsIndentedWithStringEnums()
    {
        using var tmp = new TempDir();
        var plan = new StagedPlan("0123456789abcdef0123456789abcdef",
        [
            new StagedAction("bin/a.dll", "aabb", StagedActionKind.Place, null),
            new StagedAction("bin/b.dll", "ccdd", StagedActionKind.Move, "bin/old.dll"),
        ]);

        StageLayout.SavePlan(tmp.Path, plan);
        var text = File.ReadAllText(StageLayout.PlanPath(tmp.Path));

        Assert.Contains("\n", text);                                  // 缩进（两个进程都要能人眼读）
        Assert.Contains("\"Kind\": \"Place\"", text);                 // 枚举写字面量
        Assert.Contains("\"Kind\": \"Move\"", text);
        Assert.Contains("\"MoveFromRelative\": null", text);          // null 也要显式写出（默认条件）

        var back = StageLayout.LoadPlan(tmp.Path)!;
        Assert.Equal(plan.ManifestHash, back.ManifestHash);
        Assert.Equal(2, back.Actions.Count);
        Assert.Equal(StagedActionKind.Move, back.Actions[1].Kind);
        Assert.Equal("bin/old.dll", back.Actions[1].MoveFromRelative);
    }

    [Fact]
    public void ResultJson_IsOneLine_AndKeepsEveryContractKey()
    {
        var r = new UpdateResult(UpdateOutcome.Updated, "", 10, 8, 1, 0, 1, 0, 123,
            TimeSpan.FromMilliseconds(45), "", "HTTP/1.1", 100, 200, 300, 400, 2);

        var json = r.ToJson();
        Assert.DoesNotContain("\n", json);                             // 紧凑：严格一行

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var key in new[]
                 {
                     "Outcome", "Reason", "Detail", "HttpVersion", "Applied", "PendingRestart",
                     "Total", "Skipped", "Moved", "Patched", "Full", "FailedCount",
                     "BytesDownloaded", "PayloadBytes", "WireBytes", "WireSentBytes", "WireReceivedBytes",
                     "Ms", "Deleted",
                 })
            Assert.True(root.TryGetProperty(key, out _), $"结果 JSON 缺少契约字段 {key}");

        Assert.Equal("Updated", root.GetProperty("Outcome").GetString());
        Assert.Equal(45, root.GetProperty("Ms").GetInt64());
        Assert.Equal(2, root.GetProperty("Deleted").GetInt32());
        Assert.True(root.GetProperty("Applied").GetBoolean());
        Assert.False(root.GetProperty("PendingRestart").GetBoolean());
    }
}