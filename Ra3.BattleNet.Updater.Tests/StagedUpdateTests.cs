using System.Diagnostics;
using System.Text.Json;
using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Models;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 暂存更新的**阶段一**（AGENT.md §12.5）：把要落地的东西全部暂存到 <c>UpdaterStage</c>，
/// 而**一个字节都不动现有的树**。落地（阶段二）由独立 applier 在宿主退出后做，不在这里测。
///
/// 这一组要钉住的行为：
///   ① 阶段一之后，树与本地 manifest 必须逐字节等于更新前的状态；
///   ② 待提交计划必须覆盖"本次要落地的全部动作"，**包括本轮无需下载、已经就绪的那些**
///      （否则"全部已暂存"的那一轮会写出空计划，applier 就没事可做）；
///   ③ 纯改名要推迟到落地阶段（阶段一不许动树）；
///   ④ 有待提交计划时，直接更新必须被拒绝（§12.7 的两种模式互斥）；
///   ⑤ 目标版本换了 → 旧暂存内容作废，不得留下过期内容；
///   ⑥ 阶段一半途失败 → **不写计划**（计划一旦存在就意味着"可以提交"）。
/// </summary>
public class StagedUpdateTests
{
    private sealed record Env(string Client, string Server, string V1Dir, string V2Dir, string M1, string M2,
        TestHttpServer Http);

    private static Env Prepare(TempDir tmp, bool v2ChangesTheSecondFile = true)
    {
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1,
            ("bin/a.dll", TestSupport.Big("A1")),
            ("bin/b.dll", TestSupport.Big("B1")),
            ("data/x.dat", TestSupport.Big("X1")));

        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2,
            ("bin/a.dll", TestSupport.Big("A2")),
            ("bin/b.dll", TestSupport.Big("B1")),                                    // 未变
            ("data/x.dat", v2ChangesTheSecondFile ? TestSupport.Big("X2") : TestSupport.Big("X1")));

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

        var http = new TestHttpServer(server);
        return new Env(client, server, v1, v2, m1, m2, http);
    }

    private static UpdateConfig Cfg(Env e, ApplyMode mode = ApplyMode.Staged) => new()
    {
        RootPath = e.Client,
        CacheDir = TestSupport.TestCacheDir(e.Client),
        ManifestUrl = e.Http.BaseUrl + "manifest.xml",
        ApplyMode = mode,
    };

    /// <summary>落地阶段（阶段二/三）的配置：**另一个类型**，没有清单地址（§12.5 零网络）。</summary>
    private static ApplierConfig Applier(Env e) => new()
    {
        RootPath = e.Client,
        CacheDir = TestSupport.TestCacheDir(e.Client),
    };

    [Fact]
    public void StagedRun_LeavesTheTreeUntouched_AndStagesEveryChangeToLand()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        var result = new ClientUpdater(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.Staged, result.Outcome);
        Assert.True(result.Applied, "库这边该做的都做完了，宿主不该回退到自己的整包流程");
        Assert.True(result.PendingRestart);
        Assert.True(result.BytesDownloaded > 0);

        // ① 树没被动过：内容与本地 manifest 都与更新前逐字节一致
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);

        // ② 该落地的东西都在暂存区，且内容是目标版本的
        AssertStagedMatchesManifest(e.Client, e.M2, "bin/a.dll", "data/x.dat");
        Assert.False(File.Exists(StageLayout.NewPath(e.Client, "bin/b.dll")), "未变的文件不该进暂存区");

        // ③ 计划覆盖全部待落地动作
        var plan = StageLayout.LoadPlan(e.Client);
        Assert.NotNull(plan);
        Assert.Equal(TestSupport.Md5File(e.M2), plan!.ManifestHash);
        Assert.Equal(new[] { "bin/a.dll", "data/x.dat" },
            plan.Actions.Select(a => a.RelativePath).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(plan.Actions, a => Assert.Equal(StagedActionKind.Place, a.Kind));

        Assert.Contains("Staged", File.ReadAllText(Path.Combine(TestSupport.TestCacheDir(e.Client), "update.log")));
    }

    [Fact]
    public void StagedRun_IsResumable_SecondRunDownloadsNothing_AndThePlanStillCoversEverything()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        var second = new ClientUpdater(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.Staged, second.Outcome);
        Assert.Equal(0, second.Patched);
        Assert.Equal(0, second.Full);
        Assert.Equal(0, second.BytesDownloaded);      // 已经暂存好的内容被认出来，不重下
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);

        // 这一条是这组测试最关键的断言：本轮"没干活"，但计划必须**仍然是完整的**
        var plan = StageLayout.LoadPlan(e.Client);
        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Actions.Count);
    }

    /// <summary>§12.7：回退窗口止于"下一次更新开始" —— 上一轮落地留下的备份，下一轮暂存开始时就该清掉。</summary>
    [Fact]
    public void StagedRun_ClearsThePreviousRoundsBackups_AtStartup()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);
        Assert.Equal(UpdateOutcome.Updated, new StagedApplier(Applier(e)).Run().Outcome);
        Assert.True(Directory.Exists(StageLayout.OldRoot(e.Client)), "落地后应当留着备份（回退窗口）");
        Assert.True(File.Exists(StageLayout.OldPath(e.Client, "bin/a.dll")));

        // 同一目标版本再跑一轮暂存（这次是 UpToDate）—— 清发生在会话开始时，照样会清
        var again = new ClientUpdater(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.UpToDate, again.Outcome);
        Assert.False(Directory.Exists(StageLayout.OldRoot(e.Client)), "新一轮开始时应当把上一轮的备份清掉");
    }

    [Fact]
    public void StagedRun_DefersPureRenames_UntilTheApplyStage()
    {
        using var tmp = new TempDir();
        // 只做一次纯改名：UUID 与内容都不变，只有路径变（§9.2 的 move 场景）
        var v = tmp.Sub("v");
        TestSupport.WriteTree(v, ("bin/old_name.dll", TestSupport.Big("SAME")));
        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v, null, []).Manifest.SaveToXml(m1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        var renamed = new ManifestModel(m1);
        renamed.Manifest.Files[0].FileName = "new_name.dll";
        renamed.SaveToXml(m2);

        var server = tmp.Sub("server");
        Directory.CreateDirectory(server);
        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);

        var client = tmp.Sub("client");
        TestSupport.CopyTree(v, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);

        using var http = new TestHttpServer(server);
        var result = new ClientUpdater(new UpdateConfig
        {
            RootPath = client,
            CacheDir = TestSupport.TestCacheDir(client),
            ManifestUrl = http.BaseUrl + "manifest.xml",
            ApplyMode = ApplyMode.Staged,
        }).Run();

        Assert.Equal(UpdateOutcome.Staged, result.Outcome);
        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.BytesDownloaded);

        // 阶段一不许动树：旧路径还在、新路径还不存在
        Assert.True(File.Exists(Path.Combine(client, "bin", "old_name.dll")));
        Assert.False(File.Exists(Path.Combine(client, "bin", "new_name.dll")));

        var plan = StageLayout.LoadPlan(client);
        Assert.NotNull(plan);
        var action = Assert.Single(plan!.Actions);
        Assert.Equal(StagedActionKind.Move, action.Kind);
        Assert.Equal("bin/new_name.dll", action.RelativePath);
        Assert.Equal("bin/old_name.dll", action.MoveFromRelative);
    }

    [Fact]
    public void InPlaceRun_IsRejected_WhileAStagedPlanIsPending()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        // 同一棵树上再走直接更新：必须拒绝 —— 否则随后运行的 applier 会拿旧计划覆盖刚换好的文件
        var inPlace = new ClientUpdater(Cfg(e, ApplyMode.InPlace)).Run();

        Assert.Equal(UpdateOutcome.Failed, inPlace.Outcome);
        Assert.Equal(UpdateReasons.PendingStagedApply, inPlace.Reason);
        Assert.True(inPlace.Detail.Length > 0);
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);       // 一个文件都没动
        Assert.NotNull(StageLayout.LoadPlan(e.Client));     // 待提交计划还在
    }

    [Fact]
    public void StagedRun_AgainstANewTargetVersion_DiscardsStaleStagedContent()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        // 发布 v3：把 bin/b.dll 也改掉（v1→v2 里它是未变的，于是暂存区里本没有它）
        var v3 = tmp.Sub("v3");
        TestSupport.WriteTree(v3,
            ("bin/a.dll", TestSupport.Big("A3")),
            ("bin/b.dll", TestSupport.Big("B3")),
            ("data/x.dat", TestSupport.Big("X3")));
        var m3 = Path.Combine(tmp.Path, "v3.xml");
        ManifestGenerator.Generate(v3, e.M2, [], oldRoot: e.V2Dir).Manifest.SaveToXml(m3);
        PatchGenerator.Generate(m3, v3, [new Baseline(e.M2, e.V2Dir)], e.Server, minFileSize: 0);
        File.Copy(m3, Path.Combine(e.Server, "manifest.xml"), overwrite: true);

        var result = new ClientUpdater(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.Staged, result.Outcome);
        var plan = StageLayout.LoadPlan(e.Client);
        Assert.NotNull(plan);
        Assert.Equal(TestSupport.Md5File(m3), plan!.ManifestHash);       // 计划换成新目标了
        // 暂存区里**不允许**有任何过时内容：每个暂存文件的哈希都必须等于 v3 清单里的哈希
        AssertStagedMatchesManifest(e.Client, m3, plan.Actions.Where(a => a.Kind == StagedActionKind.Place)
            .Select(a => a.RelativePath).ToArray());
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);                    // 树始终没被碰过
    }

    [Fact]
    public void StagedRun_WithPartialFailure_WritesNoPlan()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        // 造一个"必然失败"的文件：新增一个 v2 独有的文件，然后把它在服务端的 blob 删掉
        var v2 = e.V2Dir;
        TestSupport.WriteTree(v2, ("bin/brand_new.dll", TestSupport.Big("NEW")));
        var m2b = Path.Combine(tmp.Path, "v2b.xml");
        ManifestGenerator.Generate(v2, e.M1, [], oldRoot: e.V1Dir).Manifest.SaveToXml(m2b);
        PatchGenerator.Generate(m2b, v2, [new Baseline(e.M1, e.V1Dir)], e.Server, minFileSize: 0);
        File.Copy(m2b, Path.Combine(e.Server, "manifest.xml"), overwrite: true);

        var newMd5 = TestSupport.Md5(TestSupport.Big("NEW"));
        File.Delete(Path.Combine(e.Server, "files", newMd5 + ".bin"));   // 新增文件没有前身 → 只能整份下 → 404

        var result = new ClientUpdater(Cfg(e)).Run();

        Assert.NotEqual(UpdateOutcome.Staged, result.Outcome);
        Assert.True(result.FailedCount > 0);
        Assert.False(StageLayout.HasPendingPlan(e.Client), "有失败就绝不能留下待提交计划（计划存在 = 可以提交）");
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);
        // 成功那些文件的暂存内容应当留着（下一轮的续做材料）
        Assert.True(File.Exists(StageLayout.NewPath(e.Client, "bin/a.dll")));
    }

    /// <summary>CLI 壳的契约：暂存模式必须以退出码 3 结束，且 stdout 的 JSON 里 Outcome=Staged（§12.7 / §4.12）。</summary>
    [Fact]
    public void Shell_WithStagedMode_ExitsWith3_AndWritesThePlan()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        var shell = Path.Combine(RepoRoot(), "Ra3.BattleNet.Updater.Client.CLI",
            "bin", "Release", "net10.0", "Ra3.BattleNet.Updater.Client.CLI.dll");
        Assert.True(File.Exists(shell), $"找不到壳：{shell}");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in new[]
                 {
                     shell, "--root", e.Client, "--manifest-url", e.Http.BaseUrl + "manifest.xml",
                     "--cache-dir", TestSupport.TestCacheDir(e.Client), "--apply-mode", "staged", "--json",
                 })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        Assert.True(p.ExitCode == 3, $"暂存模式应当以退出码 3 结束（实得 {p.ExitCode}）；stderr={stderr}");
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("Staged", doc.RootElement.GetProperty("Outcome").GetString());
        // 独立进程壳（宿主不是 C#）只能读这行 JSON —— 重启信号必须在里面
        Assert.True(doc.RootElement.GetProperty("PendingRestart").GetBoolean(), "JSON 必须带 PendingRestart");
        Assert.True(doc.RootElement.GetProperty("Applied").GetBoolean(), "Staged 也算宿主可以跳过自己的更新逻辑");
        Assert.True(StageLayout.HasPendingPlan(e.Client));
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, "Ra3.BattleNet.Updater.Client.CLI")))
            dir = dir.Parent;

        Assert.True(dir is not null, "找不到仓库根（含 Ra3.BattleNet.Updater.Client.CLI 的目录）");
        return dir!.FullName;
    }
    // ---------------------------------------------------------------- 断言工具

    private static List<string> RelativeFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            // 缓存与暂存区都不算「树的一部分」（与 VersionMatrixSimulation 的比对口径一致）
            .Where(r => !r.StartsWith(StageLayout.DirName + "/", StringComparison.OrdinalIgnoreCase)
                     && !r.StartsWith("UpdaterCache/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

    /// <summary>客户端树（忽略暂存区）必须与某一版主目录逐字节一致，且本地 manifest 仍是给定的那份。</summary>
    private static void AssertTreeIsVersion(string client, string versionDir, string localManifest)
    {
        var actual = RelativeFiles(client)
            .Where(r => !r.Equals("manifest.xml", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var expected = RelativeFiles(versionDir);
        Assert.Equal(expected, actual);

        var sep = Path.DirectorySeparatorChar;
        foreach (var rel in expected)
            Assert.Equal(TestSupport.Md5File(Path.Combine(versionDir, rel.Replace('/', sep))),
                TestSupport.Md5File(Path.Combine(client, rel.Replace('/', sep))));

        Assert.Equal(TestSupport.Md5File(localManifest), TestSupport.Md5File(Path.Combine(client, "manifest.xml")));
    }

    /// <summary>暂存区里这些文件的哈希必须分别等于目标 manifest 里的哈希。</summary>
    private static void AssertStagedMatchesManifest(string client, string manifestPath, params string[] rels)
    {
        var manifest = new ManifestModel(manifestPath);
        var byRel = manifest.Manifest.Files.ToDictionary(f => f.RelativePath(), f => f.MD5,
            StringComparer.OrdinalIgnoreCase);

        foreach (var rel in rels)
        {
            var staged = StageLayout.NewPath(client, rel);
            Assert.True(File.Exists(staged), $"暂存区缺少 {rel}");
            Assert.Equal(byRel[rel], TestSupport.Md5File(staged));
        }
    }
}