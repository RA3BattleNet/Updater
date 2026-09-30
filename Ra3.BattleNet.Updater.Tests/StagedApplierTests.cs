using System.Diagnostics;
using System.Text.Json;
using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Models;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// **落地**（AGENT.md §12.5 阶段二/三）：<see cref="StagedApplier"/> 把阶段一暂存好的内容换上去。
///
/// 这一组要钉住的行为：
///   ① 落地后：树 == 目标版本、本地清单 == 远端清单的**原样字节**、暂存内容与计划清掉、备份留着；
///   ② **落地才推进本地清单与 ETag**（阶段一绝不写）—— 反过来看：中途失败时清单必须还是旧的；
///   ③ 只要有任何一个动作到不了目标，**整体不动**（否则会落出新旧混合的树、而清单又被推进）；
///   ④ 中途被杀留下的状态能被判定并收敛（applier 就是自己的恢复过程，§12.6）；
///   ⑤ 树里有进程在跑 → 等到时限就不落地（静默判据，§12.5）。
/// </summary>
public class StagedApplierTests
{
    private sealed record Env(string Client, string Server, string V1Dir, string V2Dir, string M1, string M2,
        TestHttpServer Http);

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

        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);

        var http = new TestHttpServer(server);
        return new Env(client, server, v1, v2, m1, m2, http);
    }

    /// <summary>阶段一（更新会话）的配置。</summary>
    private static UpdateConfig Cfg(Env e) => new()
    {
        RootPath = e.Client,
        CacheDir = TestSupport.TestCacheDir(e.Client),
        ManifestUrl = e.Http.BaseUrl + "manifest.xml",
        ApplyMode = ApplyMode.Staged,
    };

    /// <summary>
    /// 阶段二/三（落地）的配置：**另一个类型** <see cref="ApplierConfig"/>。
    /// 它没有清单地址、也没有并发/工具目录 —— 那些只属于阶段一（§12.5：落地零网络）。
    /// </summary>
    private static ApplierConfig Applier(Env e) => new()
    {
        RootPath = e.Client,
        CacheDir = TestSupport.TestCacheDir(e.Client),
        ApplierPollInterval = TimeSpan.FromSeconds(1),
        ApplierQuiescenceTimeout = TimeSpan.FromSeconds(20),
    };

    // ============================================================ 正常落地

    [Fact]
    public void Apply_LandsTheStagedTree_AndOnlyThenAdvancesTheManifest()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);                        // 阶段一没动树

        var applied = new StagedApplier(Applier(e)).Run();

        Assert.Equal(UpdateOutcome.Updated, applied.Outcome);
        Assert.False(applied.PendingRestart);
        Assert.Equal(0, applied.BytesDownloaded);                            // 落地不下任何东西
        AssertTreeIsVersion(e.Client, e.V2Dir, e.M2);                        // 树 == 目标版本、清单 == m2 原样字节

        Assert.False(StageLayout.HasPendingPlan(e.Client));                  // 计划清掉
        Assert.False(Directory.Exists(StageLayout.NewRoot(e.Client)));       // 暂存内容清掉
        Assert.True(File.Exists(StageLayout.OldPath(e.Client, "bin/a.dll"))); // 备份留着（回退窗口）
        Assert.Equal(TestSupport.Md5File(e.M2), TestSupport.Md5File(Path.Combine(e.Client, "manifest.xml")));

        // 【必须】落地阶段**不写 ETag**（B-1 定案）：它只服务于"下一次清单请求能不能 304"，
        // 而落地阶段不联网、拿不到新 ETag；写一份属于**上一版**的比不写更坏。
        // 后果仅是落地后第一次会话多取一遍清单正文，那一次会自己补上（见 ManifestFreshnessTests）。
        Assert.False(File.Exists(Path.Combine(TestSupport.TestCacheDir(e.Client), "manifest.etag")));
    }

    [Fact]
    public void Apply_WithNothingPending_IsANoOp()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        var result = new StagedApplier(Applier(e)).Run();

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Contains("没有待提交", result.Detail);
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);
    }

    /// <summary>
    /// B-1 的目标本身：**落地阶段零网络**。做法是阶段一跑完之后把 HTTP 服务器**彻底关掉**，
    /// 再落地 —— 只要 applier 还指望着联网取清单，这条用例必红。
    /// </summary>
    [Fact]
    public void Apply_WithTheServerGone_LandsOffline()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        e.Http.Dispose();                                        // 服务端此刻已经不存在了

        var applied = new StagedApplier(Applier(e)).Run();

        Assert.Equal(UpdateOutcome.Updated, applied.Outcome);
        Assert.Equal(0, applied.BytesDownloaded);
        Assert.Equal(0, applied.WireBytes);
        Assert.Equal(0, applied.PayloadBytes);
        Assert.Equal(string.Empty, applied.HttpVersion);
        AssertTreeIsVersion(e.Client, e.V2Dir, e.M2);            // 树 == v2、本地清单 == m2 的原样字节
        Assert.False(StageLayout.HasPendingPlan(e.Client));
    }

    // ============================================================ 拒绝落地

    /// <summary>
    /// B-1：applier **不联网** —— 缓存里的清单原文不在，就拒绝落地（`manifest_unavailable`），
    /// 而不是去线上取一份"最新版"落下来。本地清单不被改写、暂存内容与计划原样留着。
    /// </summary>
    [Fact]
    public void Apply_WithoutTheCachedManifest_RefusesToLand_AndDoesNotTouchAnything()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        File.Delete(Path.Combine(TestSupport.TestCacheDir(e.Client), "manifest.remote.xml"));

        var result = new StagedApplier(Applier(e)).Run();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.ManifestUnavailable, result.Reason);
        Assert.Contains("拒绝落地", result.Detail);
        Assert.Equal(0, result.BytesDownloaded);
        Assert.Equal(string.Empty, result.HttpVersion);          // 零网络：连 HTTP 版本都没有
        Assert.Equal(0, result.WireBytes);
        Assert.Equal(0, result.PayloadBytes);
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);           // 树一个字节没动
        Assert.True(StageLayout.HasPendingPlan(e.Client));      // 计划与暂存内容原样留着
        Assert.False(File.Exists(Path.Combine(TestSupport.TestCacheDir(e.Client), "manifest.etag")),
            "落地阶段不写 ETag（§12.5）");
    }

    /// <summary>
    /// `staged_plan_stale` 保留为**一致性自检**：正常流程下计划与缓存都出自阶段一，
    /// 所以它只会在"缓存被替换 / 损坏"（或运维手工塞了一份别的清单）时触发。
    /// </summary>
    [Fact]
    public void Apply_WhenTheCachedManifestNoLongerMatchesThePlan_RefusesToLand()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        // 缓存里的远端原文被换成**另一版**（模拟缓存被替换/损坏）：字节哈希与计划对不上
        var v3 = tmp.Sub("v3");
        TestSupport.WriteTree(v3,
            ("bin/a.dll", TestSupport.Big("A3")), ("bin/b.dll", TestSupport.Big("B3")),
            ("data/x.dat", TestSupport.Big("X3")));
        var m3 = Path.Combine(tmp.Path, "v3.xml");
        ManifestGenerator.Generate(v3, e.M2, [], oldRoot: e.V2Dir).Manifest.SaveToXml(m3);
        File.Copy(m3, Path.Combine(TestSupport.TestCacheDir(e.Client), "manifest.remote.xml"), overwrite: true);

        var result = new StagedApplier(Applier(e)).Run();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.StagedPlanStale, result.Reason);
        Assert.Contains("缓存", result.Detail);
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);            // 一个文件都没动
        Assert.True(StageLayout.HasPendingPlan(e.Client));       // 计划与暂存内容原样留着
    }

    [Fact]
    public void Apply_WithTheCachedManifest_LandsTheStagedVersion_EvenIfTheRemoteMovedOn()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        // 服务端发布了 v3，但缓存里仍有阶段一取到的 m2 → applier 按计划落地 m2。
        // 这是**有意的**：m2 本身是自洽的一版，落它不会出错；下一次更新会话自然会做 m2→m3。
        var v3 = tmp.Sub("v3");
        TestSupport.WriteTree(v3,
            ("bin/a.dll", TestSupport.Big("A3")), ("bin/b.dll", TestSupport.Big("B3")),
            ("data/x.dat", TestSupport.Big("X3")));
        var m3 = Path.Combine(tmp.Path, "v3.xml");
        ManifestGenerator.Generate(v3, e.M2, [], oldRoot: e.V2Dir).Manifest.SaveToXml(m3);
        File.Copy(m3, Path.Combine(e.Server, "manifest.xml"), overwrite: true);

        Assert.Equal(UpdateOutcome.Updated, new StagedApplier(Applier(e)).Run().Outcome);
        AssertTreeIsVersion(e.Client, e.V2Dir, e.M2);
    }

    [Fact]
    public void Apply_WhenAStagedFileIsMissing_RefusesToLandAsAWhole()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);
        File.Delete(StageLayout.NewPath(e.Client, "data/x.dat"));      // 暂存内容丢了一个

        var result = new StagedApplier(Applier(e)).Run();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.StagedContentMissing, result.Reason);
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);                  // 整体不动（连已就绪的也不动）
        Assert.True(File.Exists(StageLayout.NewPath(e.Client, "bin/a.dll")));
    }

    // ============================================================ 恢复（§12.6）

    [Fact]
    public void Apply_ConvergesFromAnInterruptedCommit()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        // 手工造出"崩在中途"的三种状态：
        //   ① bin/a.dll：已经落地完成（目标=新内容、备份在）
        //   ② data/x.dat：崩在两次改名之间（目标缺失、暂存在、备份在）
        //   ③ bin/new.dll：还没轮到（目标本来不存在、暂存在）
        var root = e.Client;
        var aOld = StageLayout.OldPath(root, "bin/a.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(aOld)!);
        File.Move(Path.Combine(root, "bin", "a.dll"), aOld);
        File.Move(StageLayout.NewPath(root, "bin/a.dll"), Path.Combine(root, "bin", "a.dll"));

        var xTarget = Path.Combine(root, "data", "x.dat");
        var xOld = StageLayout.OldPath(root, "data/x.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(xOld)!);
        File.Move(xTarget, xOld);
        Assert.False(File.Exists(xTarget));

        var result = new StagedApplier(Applier(e)).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        AssertTreeIsVersion(root, e.V2Dir, e.M2);
    }

    [Fact]
    public void Apply_RestoresAMissingTargetFromBackup_ThenRefusesToLand()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        // §12.6 的 Rollback 行：目标缺失 + 暂存内容也没了 + 备份在。
        // 先把目标恢复成"整的"，但它已经到不了目标版本 → 整体不落地。
        var root = e.Client;
        var xTarget = Path.Combine(root, "data", "x.dat");
        var xOld = StageLayout.OldPath(root, "data/x.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(xOld)!);
        File.Move(xTarget, xOld);
        File.Delete(StageLayout.NewPath(root, "data/x.dat"));

        var result = new StagedApplier(Applier(e)).Run();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.StagedContentMissing, result.Reason);
        Assert.Equal(TestSupport.Md5(TestSupport.Big("X1")), TestSupport.Md5File(xTarget));   // 已恢复成旧内容
        Assert.Equal(TestSupport.Md5(TestSupport.Big("A1")),
            TestSupport.Md5File(Path.Combine(root, "bin", "a.dll")));                        // 其它文件没被动
        Assert.True(StageLayout.HasPendingPlan(root));
    }

    // ============================================================ 静默判据

    [Fact]
    public void Apply_OnSuccess_LaunchesTheHostAgain()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        var marker = Path.Combine(tmp.Path, "restarted.txt");
        var cfg = Applier(e) with
        {
            RestartAfterApply = true,
            RestartExecutable = "cmd.exe",
            RestartArguments = $"/c echo ok > \"{marker}\"",
            RestartDelay = TimeSpan.Zero,
        };

        var applied = new StagedApplier(cfg).Run();

        Assert.Equal(UpdateOutcome.Updated, applied.Outcome);
        AssertTreeIsVersion(e.Client, e.V2Dir, e.M2);          // 先落地，再拉起宿主
        Assert.Contains("已拉起宿主", applied.Detail);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(marker) && sw.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(50);
        Assert.True(File.Exists(marker), "落地成功之后应当把宿主拉起来");
    }

    [Fact]
    public void Apply_WhenNothingWasLanded_DoesNotLaunchTheHost()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        var marker = Path.Combine(tmp.Path, "restarted-never.txt");
        var cfg = Applier(e) with
        {
            RestartAfterApply = true,
            RestartExecutable = "cmd.exe",
            RestartArguments = $"/c echo ok > \"{marker}\"",
            RestartDelay = TimeSpan.Zero,
        };

        var result = new StagedApplier(cfg).Run();             // 没有待提交计划 → 什么都没落地

        Assert.NotEqual(UpdateOutcome.Updated, result.Outcome);
        Thread.Sleep(600);
        Assert.False(File.Exists(marker), "没落地成功就不该拉起宿主");
    }

    /// <summary>把某个清单文件里的 <Path> 全换成逃逸值（模拟被篡改 / 位翻转 / 从旧备份还原）。</summary>
    private static void TamperManifestPath(string manifestPath, string value)
    {
        var xml = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, System.Text.RegularExpressions.Regex.Replace(
            xml, "<Path>[^<]*</Path>", "<Path>" + value + "</Path>",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5)));
    }

    /// <summary>把**服务端**清单里的 <Path> 全换成逃逸值。</summary>
    private static void TamperPath(Env e, string value) =>
        TamperManifestPath(Path.Combine(e.Server, "manifest.xml"), value);

    [Fact]
    public void Update_WhenTheRemoteManifestHasAnEscapingPath_RefusesBeforeTouchingAnything()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        TamperPath(e, "..");

        var result = new ClientUpdater(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.PathEscape, result.Reason);
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);            // 一个字节都没动
        Assert.False(StageLayout.HasPendingPlan(e.Client));
    }

    /// <summary>
    /// **本地**清单的路径也要过信任边界（§4.14）。它不只是"防篡改"，也防**位翻转**与**从旧备份还原**：
    /// `Updater.cs` 原来只用 try/catch 覆盖"解析失败"，**不覆盖"语义不安全"**。
    ///
    /// 这条路径是真实原语：本地清单里的相对路径会被规划器当成 `Move` 的**改名来源**
    /// （`UpdatePlanner.cs` 的 `Full(root, same.RelativePath())`），而 `PlanAction.Move` 执行的是
    /// 真的 `Fs.Place(来源 → 目标)` —— 越界路径 = 把安装根**之外**的文件搬进树（这里就是那个诱饵）。
    /// </summary>
    [Fact]
    public void Update_WhenTheLocalManifestHasAnEscapingPath_RefusesBeforeTouchingAnything()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        // 根外诱饵：内容**正好等于** bin/b.dll 的目标内容，于是它会成为那个"改名来源"。
        // 用就地模式（而不是本类默认的暂存模式）—— 暂存模式只是把改名**推迟**给 applier，
        // 就地模式则当场执行 `Fs.Place(来源 → 目标)`：修好之前这一轮会把诱饵搬进树，
        // 并收敛成 `Updated`（实测过，下面两条断言都会红）。
        var decoy = Path.Combine(tmp.Path, "b.dll");
        File.WriteAllText(decoy, TestSupport.Big("B1"), new System.Text.UTF8Encoding(false));
        var outsideBefore = TreeFingerprint(tmp.Path, skip: e.Client);   // 根外（除 client 之外）的全部文件

        TamperManifestPath(Path.Combine(e.Client, "manifest.xml"), "..");

        var result = new ClientUpdater(Cfg(e) with { ApplyMode = ApplyMode.InPlace }).Run();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.PathEscape, result.Reason);
        Assert.Contains("本地清单", result.Detail);
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1, manifestIsTampered: true);   // 树一个字节没动
        Assert.False(StageLayout.HasPendingPlan(e.Client));
        Assert.True(File.Exists(decoy), "安装根之外的文件被搬走了");
        Assert.Equal(outsideBefore, TreeFingerprint(tmp.Path, skip: e.Client));   // 根外一个字节没动
    }

    [Fact]
    public void Apply_WhenTheCachedManifestHasAnEscapingPath_RefusesToLand()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        // 缓存里的远端原文被换成带逃逸路径的版本（被篡改/损坏的落地输入）。
        // 注意：这份清单的字节哈希与计划也对不上 —— 但路径信任边界要在**谈计划新不新之前**就拦下。
        TamperManifestPath(Path.Combine(TestSupport.TestCacheDir(e.Client), "manifest.remote.xml"), "..");

        var result = new StagedApplier(Applier(e)).Run();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.PathEscape, result.Reason);
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);
        Assert.True(StageLayout.HasPendingPlan(e.Client));
    }

    [Fact]
    public void Apply_WhenThePidWasRecycledToAnotherProcess_DoesNotWaitForIt()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        // 拿本测试进程当"抢到同一个 PID 的无关进程"：PID 活着，但身份对不上 → 不许等它
        var cfg = Applier(e) with
        {
            WaitForProcessId = Environment.ProcessId,
            WaitForProcessName = "definitely-not-this-process",
            ApplierQuiescenceTimeout = TimeSpan.FromSeconds(5),
        };

        var applied = new StagedApplier(cfg).Run();

        Assert.Equal(UpdateOutcome.Updated, applied.Outcome);   // 没有白等到超时
        AssertTreeIsVersion(e.Client, e.V2Dir, e.M2);
    }

    [Fact]
    public void Apply_WhenSameNameButADifferentStartTime_DoesNotWaitForIt()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        // 名字也相同（例如用户又双击了一次同名启动器）—— 启动时刻对不上就一定是别的进程
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var cfg = Applier(e) with
        {
            WaitForProcessId = self.Id,
            WaitForProcessName = self.ProcessName,
            WaitForProcessStartTicks = self.StartTime.Ticks - TimeSpan.TicksPerSecond,
            ApplierQuiescenceTimeout = TimeSpan.FromSeconds(5),
        };

        var applied = new StagedApplier(cfg).Run();

        Assert.Equal(UpdateOutcome.Updated, applied.Outcome);
        AssertTreeIsVersion(e.Client, e.V2Dir, e.M2);
    }

    [Fact]
    public void Apply_WhenTheHostIdentityStillMatches_KeepsWaitingThenRefuses()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        // 三元组完全吻合 = 就是宿主本人且它没退 → 必须一直等，最后以 tree_busy 收场、一个文件都不动
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var cfg = Applier(e) with
        {
            WaitForProcessId = self.Id,
            WaitForProcessName = self.ProcessName,
            WaitForProcessStartTicks = self.StartTime.Ticks,
            ApplierQuiescenceTimeout = TimeSpan.FromSeconds(3),
            ApplierPollInterval = TimeSpan.FromMilliseconds(200),
        };

        var result = new StagedApplier(cfg).Run();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.TreeBusy, result.Reason);
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);
        Assert.True(StageLayout.HasPendingPlan(e.Client));       // 计划原样留着，下次再来
    }

    [Fact]
    public void Apply_WaitsForAProcessRunningFromTheTree_ThenLandsAfterItExits()
    {
        const string ping = @"C:\Windows\System32\ping.exe";
        if (!OperatingSystem.IsWindows() || !File.Exists(ping)) return;

        using var tmp = new TempDir();
        // 专用环境：v2 里有一个 exe —— 静默判据是"按 manifest 里的 exe 名扫进程"，得有东西可扫
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("bin/data.bin", TestSupport.Big("D1")));
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2, ("bin/data.bin", TestSupport.Big("D2")));
        File.Copy(ping, Path.Combine(v2, "bin", "app.exe"), overwrite: true);

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
        var stageOne = new UpdateConfig
        {
            RootPath = client,
            CacheDir = TestSupport.TestCacheDir(client),
            ManifestUrl = http.BaseUrl + "manifest.xml",
            ApplyMode = ApplyMode.Staged,
        };
        var applier = new ApplierConfig
        {
            RootPath = client,
            CacheDir = TestSupport.TestCacheDir(client),
            ApplierPollInterval = TimeSpan.FromSeconds(1),
        };

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(stageOne).Run().Outcome);

        // 让这棵树里真的有个进程在跑（暂存内容就是 ping 的字节，正好拿来当可执行文件）
        var exe = Path.Combine(client, "bin", "app.exe");
        File.Copy(StageLayout.NewPath(client, "bin/app.exe"), exe, overwrite: true);

        using var blocker = Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "-n", "25", "127.0.0.1" },
        })!;
        try
        {
            var blocked = new StagedApplier(applier with { ApplierQuiescenceTimeout = TimeSpan.FromSeconds(3) }).Run();
            Assert.Equal(UpdateOutcome.Failed, blocked.Outcome);
            Assert.Equal(UpdateReasons.TreeBusy, blocked.Reason);
            Assert.True(StageLayout.HasPendingPlan(client));
        }
        finally
        {
            try { blocker.Kill(entireProcessTree: true); } catch { /* 已经退了 */ }
            blocker.WaitForExit(10000);
        }

        // 障碍消失后，同一次落地就能完成
        var landed = new StagedApplier(applier with { ApplierQuiescenceTimeout = TimeSpan.FromSeconds(20) }).Run();
        Assert.Equal(UpdateOutcome.Updated, landed.Outcome);
        Assert.False(StageLayout.HasPendingPlan(client));
        Assert.Equal(TestSupport.Md5(TestSupport.Big("D2")),
            TestSupport.Md5File(Path.Combine(client, "bin", "data.bin")));
    }

    // ============================================================ CLI 契约

    /// <summary>
    /// 宿主只需要 `Client.CLI --apply`：落地成功退出码 0、stdout 一行 JSON 的 Outcome=Updated。
    /// 命令行里**没有 `--manifest-url`** —— 落地阶段不再有"去哪儿取清单"这件事（B-1）。
    /// </summary>
    [Fact]
    public void Shell_WithApplyMode_LandsTheStagedTree_AndExitsZero()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in new[]
                 {
                     ShellDll(), "--apply", "--root", e.Client,
                     "--cache-dir", TestSupport.TestCacheDir(e.Client), "--json",
                 })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        Assert.True(p.ExitCode == 0, $"落地成功应当以退出码 0 结束（实得 {p.ExitCode}）；stderr={stderr}");
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("Updated", doc.RootElement.GetProperty("Outcome").GetString());

        // 零网络：三个字节口径与 HTTP 版本都必须是空的/0，且日志的列数/列含义不变（§4.11 契约）
        Assert.Equal(0, doc.RootElement.GetProperty("PayloadBytes").GetInt64());
        Assert.Equal(0, doc.RootElement.GetProperty("WireBytes").GetInt64());
        Assert.Equal(0, doc.RootElement.GetProperty("WireSentBytes").GetInt64());
        Assert.Equal(0, doc.RootElement.GetProperty("WireReceivedBytes").GetInt64());
        Assert.Equal(string.Empty, doc.RootElement.GetProperty("HttpVersion").GetString());
        AssertRunLineHasContractColumns(e.Client);
        AssertTreeIsVersion(e.Client, e.V2Dir, e.M2);
    }

    /// <summary>
    /// B-1 的"最彻底"那一半：阶段一的参数在 `--apply` 下**报错**，而不是被静默忽略。
    /// 静默忽略会让"落地还要去取远端"这个误解一直活着。
    /// </summary>
    [Fact]
    public void Shell_WithApplyMode_RejectsStageOneArguments()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in new[]
                 {
                     ShellDll(), "--apply", "--root", e.Client,
                     "--cache-dir", TestSupport.TestCacheDir(e.Client),
                     "--manifest-url", e.Http.BaseUrl + "manifest.xml",
                 })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        Assert.Equal(2, p.ExitCode);                                  // 参数错误
        Assert.Contains("--manifest-url", stderr);
        Assert.Contains("阶段一", stderr);
        Assert.DoesNotContain("Outcome", stdout);                     // 没跑落地，没吐结果 JSON
        Assert.Contains("用法", stdout);                              // 而是打了一遍用法
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);                 // 树一个字节没动
    }

    /// <summary>`BuildApplyCommand` 生成的 argv：没有 URL / 工具目录，且把 applier 真正需要的都带上了。</summary>
    [Fact]
    public void BuildApplyCommand_HasNoWayToExpressARemoteManifest()
    {
        var cfg = new ApplierConfig
        {
            RootPath = Path.GetTempPath(),
            CacheDir = Path.Combine(Path.GetTempPath(), "cache-x"),
            WaitForProcessId = 4242,
            WaitForProcessName = "host",
            WaitForProcessStartTicks = 123456789,
            ApplierQuiescenceTimeout = TimeSpan.FromSeconds(90),
            ApplierPollInterval = TimeSpan.FromSeconds(3),
        };

        var psi = StagedApplier.BuildApplyCommand("applier.exe", cfg);
        var argv = psi.ArgumentList.ToList();

        Assert.DoesNotContain("--manifest-url", argv);
        Assert.DoesNotContain("--tools-dir", argv);
        Assert.DoesNotContain("--concurrency", argv);
        Assert.DoesNotContain("--fallback", argv);
        Assert.DoesNotContain("--base-url", argv);
        Assert.Equal("--apply", argv[0]);
        Assert.Contains("--root", argv);
        Assert.Contains("--cache-dir", argv);
        Assert.Contains("--wait-for-pid", argv);
        // 身份三件套由 BuildApplyCommand 在**宿主进程里**自动填 —— 装的必须是"当前这个进程"，不是上面那个 4242
        Assert.Equal(Environment.ProcessId.ToString(), argv[argv.IndexOf("--wait-for-pid") + 1]);
        Assert.Equal("90", argv[argv.IndexOf("--quiescence-timeout") + 1]);
        Assert.Equal("3", argv[argv.IndexOf("--poll-seconds") + 1]);
        // 没有显式给 --manifest-file 时不带它：两边用**同一个默认值**（<cache-dir>/manifest.remote.xml）
        Assert.DoesNotContain("--manifest-file", argv);
    }

    /// <summary>阶段一与落地阶段对"缓存在哪"必须算出**逐字相同**的结果（锁与清单都在那里）。</summary>
    [Fact]
    public void ApplierAndStageOne_AgreeOnTheResolvedPaths()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("install");
        var stageOne = new UpdateConfig { RootPath = root, ManifestUrl = "http://example.invalid/manifest.xml" };
        var applier = new ApplierConfig { RootPath = root };

        Assert.Equal(stageOne.ResolveCacheDir(), applier.ResolveCacheDir());
        Assert.Equal(stageOne.ResolveLogPath(), applier.ResolveLogPath());
        Assert.Equal(stageOne.ResolveLocalManifestPath(), applier.ResolveLocalManifestPath());
        Assert.Equal(Path.Combine(applier.ResolveCacheDir(), "manifest.remote.xml"), applier.ResolveManifestFile());
    }

    /// <summary>R 行的列数与列含义是契约（§4.11）：落地阶段**照旧写满**，只是 requests/payload/wire 恒为 0。</summary>
    private static void AssertRunLineHasContractColumns(string client)
    {
        var log = File.ReadAllLines(Path.Combine(client, "UpdaterCache", "update.log"));
        var run = log.Last(l => l.StartsWith("R\t", StringComparison.Ordinal));
        var cols = run.Split('\t');
        // R | run_id | manifest_hash | total | skip | move | patch | full | fail | bytes | ms | result | requests | payload | wire
        Assert.Equal(15, cols.Length);
        Assert.Equal("R", cols[0]);
        Assert.Equal("Updated", cols[11]);          // result
        Assert.Equal("0", cols[12]);                // requests
        Assert.Equal("0", cols[13]);                // payload
        Assert.Equal("0", cols[14]);                // wire
    }

    /// <summary>
    /// applier 把**实际使用**的 root / cacheDir / manifestFile 打在日志最前面（`C` 行型）。
    /// 两个配置类型各自独立，唯一会出事的是"两边指向的不是同一处"，所以这一行是排查的第一入口。
    /// </summary>
    [Fact]
    public void Apply_LogsThePathsItActuallyUsed()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);
        Assert.Equal(UpdateOutcome.Updated, new StagedApplier(Applier(e)).Run().Outcome);

        var lines = File.ReadAllLines(Path.Combine(TestSupport.TestCacheDir(e.Client), "update.log"));
        var ctx = lines.Where(l => l.StartsWith("C\t", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, ctx.Count);
        Assert.Equal("root", ctx[0].Split('\t')[2]);
        Assert.Equal(Path.GetFullPath(e.Client), ctx[0].Split('\t')[3]);
        Assert.Equal("cacheDir", ctx[1].Split('\t')[2]);
        Assert.Equal(Path.GetFullPath(TestSupport.TestCacheDir(e.Client)), ctx[1].Split('\t')[3]);
        Assert.Equal("manifestFile", ctx[2].Split('\t')[2]);
        Assert.Equal(Path.Combine(Path.GetFullPath(TestSupport.TestCacheDir(e.Client)), "manifest.remote.xml"),
            ctx[2].Split('\t')[3]);
        // 三个 `C` 行必须紧挨在**本次（applier）**的 S 行之前（同一个日志里前面还有阶段一那一轮的 S/F/R）
        var sIndex = Array.FindLastIndex(lines, l => l.StartsWith("S\t", StringComparison.Ordinal));
        Assert.True(sIndex >= 3, "C 行必须排在本次运行的 S 行之前");
        Assert.Contains("-apply", lines[sIndex].Split('\t')[1]);       // run_id 里带 -apply，确认这是 applier 那一轮
        Assert.All(new[] { sIndex - 3, sIndex - 2, sIndex - 1 },
            i => Assert.StartsWith("C\t", lines[i]));
    }

    private static string ShellDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, "Ra3.BattleNet.Updater.Client.CLI")))
            dir = dir.Parent;

        Assert.True(dir is not null, "找不到仓库根（含 Ra3.BattleNet.Updater.Client.CLI 的目录）");
        return Path.Combine(dir!.FullName, "Ra3.BattleNet.Updater.Client.CLI",
            "bin", "Release", "net10.0", "Ra3.BattleNet.Updater.Client.CLI.dll");
    }
    // ============================================================ 工具

    private static List<string> RelativeFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Where(r => !r.StartsWith(StageLayout.DirName + "/", StringComparison.OrdinalIgnoreCase)
                        && !r.StartsWith("UpdaterCache/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

    /// <summary>整棵子树的 <c>(相对路径, MD5)</c> 清单 —— 用来断言"一个字节都没动"。</summary>
    private static string TreeFingerprint(string root, string? skip = null)
    {
        var prefix = skip is null ? null : skip + Path.DirectorySeparatorChar;
        var lines = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => prefix is null || !p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/') + "=" + TestSupport.Md5File(p))
            .OrderBy(x => x, StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    /// <param name="manifestIsTampered">
    /// 本地清单是被故意改过的（信任边界的用例）：只比"受管文件是否原样"，**不**要求清单字节等于基线。
    /// </param>
    private static void AssertTreeIsVersion(string client, string versionDir, string localManifest,
        bool manifestIsTampered = false)
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

        if (!manifestIsTampered)
            Assert.Equal(TestSupport.Md5File(localManifest),
                TestSupport.Md5File(Path.Combine(client, "manifest.xml")));
    }
}