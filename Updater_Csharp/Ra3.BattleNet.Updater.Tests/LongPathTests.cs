using Ra3.BattleNet.Updater.Server;
using CoreUpdater = Ra3.BattleNet.Updater.Core.Updater;
using Ra3.BattleNet.Updater.Core;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 超长路径（AGENT.md §7.5）：安装根目录一旦落在
/// "C:\Program Files (x86)\&lt;发行商&gt;\&lt;产品&gt;\..."，260 字符很快就不够用。
/// 这里用一棵**故意超过 MAX_PATH** 的树走完整链路（生成 → 打补丁 → 下载 → 应用）。
/// </summary>
public class LongPathTests
{
    private const string DeepDir =
        "layer0xxxxxxxxxxxxx/layer1xxxxxxxxxxxxx/layer2xxxxxxxxxxxxx/layer3xxxxxxxxxxxxx/" +
        "layer4xxxxxxxxxxxxx/layer5xxxxxxxxxxxxx/layer6xxxxxxxxxxxxx/layer7xxxxxxxxxxxxx/" +
        "layer8xxxxxxxxxxxxx/layer9xxxxxxxxxxxxx/layer10xxxxxxxxxxxx/layer11xxxxxxxxxxxx";

    [Fact]
    public void DeepTree_BeyondMaxPath_UpdatesIncrementally_AndAppliesPatches()
    {
        using var tmp = new TempDir();

        var deepFile = DeepDir + "/payload.bin";
        Assert.True(Path.Combine(tmp.Sub("probe"), DeepDir).Length > 260,
            "测试前提不成立：临时根目录太短，这棵树没超过 MAX_PATH");

        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, (deepFile, TestSupport.Big("DEEP-V1", 32 * 1024)), ("top.txt", "top"));

        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2, (deepFile, TestSupport.Big("DEEP-V2", 32 * 1024)), ("top.txt", "top"));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        var gen2 = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        gen2.Manifest.SaveToXml(m2);
        Assert.Single(gen2.Modified);

        var server = tmp.Sub("server");
        var summary = PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 0);
        Assert.Equal(1, summary.PatchesCreated);

        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);

        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);

        using var http = new TestHttpServer(server);
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        }).Run();

        Assert.True(result.Outcome == UpdateOutcome.Updated, $"期望 Updated，实得 {result}；Detail={result.Detail}");
        Assert.Equal(1, result.Patched);          // 深路径也必须能走补丁
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.Full);

        TestSupport.AssertSameAs(gen2.Manifest, v2, client);
    }
}
