using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 补丁工具（hpatchz）缺失时的行为。
///
/// 背景（2026-09-28 复现）：`HdiffTool.Rid = RuntimeInformation.RuntimeIdentifier` 取的是**当前进程**的 RID，
/// 而随包只覆盖 win-x64/linux-x64 —— 32 位宿主（RID = win-x86）里 `Find` 返回 null，
/// 于是每个文件都"**先下一份补丁 → 应用失败 → 删掉补丁 → 再下完整文件**"，
/// 比纯完整下载还费流量，而且日志里只留 patch_failed（看不出是缺工具）。
/// 现在：计划阶段就降级，一个字节的补丁都不会去下，归因写 patch_tool_missing。
/// </summary>
public class PatchToolTests
{
    /// <summary>
    /// RID 目录**只认当前 RID**：只有 win-x64 工具时，win-x86 不能"借用"它
    /// （能借用的话，32 位进程会去启动 64 位工具——在 64 位系统上碰巧能跑，在真 32 位系统上就是启动失败）。
    /// </summary>
    [Fact]
    public void Find_OnlyUsesTheRequestedRidDirectory()
    {
        using var tmp = new TempDir();
        var x64Dir = Path.Combine(tmp.Path, "win-x64");
        Directory.CreateDirectory(x64Dir);
        var fake = Path.Combine(x64Dir, "hpatchz" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.WriteAllBytes(fake, [0x4D, 0x5A]);   // 内容无所谓，Find 只看文件是否存在

        Assert.Equal(fake, HdiffTool.Find(tmp.Path, "hpatchz", "win-x64"));
        Assert.NotEqual(fake, HdiffTool.Find(tmp.Path, "hpatchz", "win-x86"));   // 不许跨 RID 借用
    }

    /// <summary>RID 目录存在对应工具时能被找到（win-x86 随包之后这条路才成立）。</summary>
    [Fact]
    public void Find_PicksTheToolInsideItsOwnRidDirectory()
    {
        using var tmp = new TempDir();
        var dir = Path.Combine(tmp.Path, "win-x86");
        Directory.CreateDirectory(dir);
        var fake = Path.Combine(dir, "hpatchz" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.WriteAllBytes(fake, [0x4D, 0x5A]);

        Assert.Equal(fake, HdiffTool.Find(tmp.Path, "hpatchz", "win-x86"));
    }

    /// <summary>随包清单里必须包含 win-x86（32 位宿主）；这条是防回退的断言。</summary>
    [Fact]
    public void ShippedRids_CoverThe32BitWindowsHost()
    {
        Assert.Contains("win-x86", HdiffTool.ShippedRids);
        Assert.Contains("win-x64", HdiffTool.ShippedRids);
        Assert.Contains("linux-x64", HdiffTool.ShippedRids);
    }

    /// <summary>
    /// **端到端**：工具不可用时，一个补丁请求都不该发出去。
    ///
    /// 为了在测试进程里造出"工具不可用"，这里把输出目录里的 `hdiffpatch_bin` 整个挪开、并清空 PATH
    /// （`Find` 的最后一道兜底是 PATH，而开发机上 PATH 里可能有别人装的 hpatchz），
    /// 跑完在 finally 里原样还原 —— 下次 `dotnet build` 也会按 Content 规则重新复制一份。
    /// </summary>
    [Fact]
    public void MissingPatchTool_DegradesAtPlanStage_WithoutFetchingAnyPatch()
    {
        using var tmp = new TempDir();

        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("a.bin", TestSupport.Big("V1")), ("b.bin", TestSupport.Big("B1")));
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2, ("a.bin", TestSupport.Big("V2")), ("b.bin", TestSupport.Big("B2")));

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

        // 挪开随包工具 + 清空 PATH ⇒ `Find` 必然返回 null
        var binDir = Path.Combine(AppContext.BaseDirectory, "hdiffpatch_bin");
        var moved = binDir + ".disabled-for-test";
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        var movedOk = false;
        try
        {
            if (Directory.Exists(binDir)) { Directory.Move(binDir, moved); movedOk = true; }
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            Assert.Null(HdiffTool.FindPatchTool(AppContext.BaseDirectory));   // 前提：确实找不到了

            using var http = new TestHttpServer(server);
            var result = new ClientUpdater(new UpdateConfig
            {
                RootPath = client,
                ManifestUrl = http.BaseUrl + "manifest.xml",
            }).Run();

            Assert.Equal(UpdateOutcome.Updated, result.Outcome);          // 仍然更新成功（回落完整下载）
            Assert.Equal(0, result.Patched);
            Assert.Equal(2, result.Full);
            Assert.Equal(0, result.FailedCount);
            Assert.DoesNotContain(http.RequestPaths, p => p.StartsWith("/patches/", StringComparison.Ordinal));
            Assert.Contains("patch_tool_missing", File.ReadAllText(Path.Combine(client, "UpdaterCache", "update.log")));
            Assert.Contains("hpatchz", result.Detail);                    // 宿主能看出一句"为什么全在整包下载"

            TestSupport.AssertSameAs(new ManifestModel(m2), v2, client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            if (movedOk && Directory.Exists(moved) && !Directory.Exists(binDir)) Directory.Move(moved, binDir);
        }
    }
}
