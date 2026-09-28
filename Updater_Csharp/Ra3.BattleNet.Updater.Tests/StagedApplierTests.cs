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

    private static UpdateConfig Cfg(Env e) => new()
    {
        RootPath = e.Client,
        CacheDir = TestSupport.TestCacheDir(e.Client),
        ManifestUrl = e.Http.BaseUrl + "manifest.xml",
        ApplyMode = ApplyMode.Staged,
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

        var applied = new StagedApplier(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.Updated, applied.Outcome);
        Assert.False(applied.PendingRestart);
        Assert.Equal(0, applied.BytesDownloaded);                            // 落地不下任何东西
        AssertTreeIsVersion(e.Client, e.V2Dir, e.M2);                        // 树 == 目标版本、清单 == m2 原样字节

        Assert.False(StageLayout.HasPendingPlan(e.Client));                  // 计划清掉
        Assert.False(Directory.Exists(StageLayout.NewRoot(e.Client)));       // 暂存内容清掉
        Assert.True(File.Exists(StageLayout.OldPath(e.Client, "bin/a.dll"))); // 备份留着（回退窗口）
        Assert.Equal(TestSupport.Md5File(e.M2), TestSupport.Md5File(Path.Combine(e.Client, "manifest.xml")));
    }

    [Fact]
    public void Apply_WithNothingPending_IsANoOp()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        var result = new StagedApplier(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Contains("没有待提交", result.Detail);
        AssertTreeIsVersion(e.Client, e.V1Dir, e.M1);
    }

    // ============================================================ 拒绝落地

    [Fact]
    public void Apply_WhenTheRefetchedManifestMovedOn_RefusesToLand()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        // 删掉缓存里的远端原文 → applier 只能联网重取；而这时服务端已经发布了 v3
        File.Delete(Path.Combine(TestSupport.TestCacheDir(e.Client), "manifest.remote.xml"));
        var v3 = tmp.Sub("v3");
        TestSupport.WriteTree(v3,
            ("bin/a.dll", TestSupport.Big("A3")), ("bin/b.dll", TestSupport.Big("B3")),
            ("data/x.dat", TestSupport.Big("X3")));
        var m3 = Path.Combine(tmp.Path, "v3.xml");
        ManifestGenerator.Generate(v3, e.M2, [], oldRoot: e.V2Dir).Manifest.SaveToXml(m3);
        File.Copy(m3, Path.Combine(e.Server, "manifest.xml"), overwrite: true);

        var result = new StagedApplier(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.StagedPlanStale, result.Reason);
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

        Assert.Equal(UpdateOutcome.Updated, new StagedApplier(Cfg(e)).Run().Outcome);
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

        var result = new StagedApplier(Cfg(e)).Run();

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

        var result = new StagedApplier(Cfg(e)).Run();

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

        var result = new StagedApplier(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateReasons.StagedContentMissing, result.Reason);
        Assert.Equal(TestSupport.Md5(TestSupport.Big("X1")), TestSupport.Md5File(xTarget));   // 已恢复成旧内容
        Assert.Equal(TestSupport.Md5(TestSupport.Big("A1")),
            TestSupport.Md5File(Path.Combine(root, "bin", "a.dll")));                        // 其它文件没被动
        Assert.True(StageLayout.HasPendingPlan(root));
    }

    // ============================================================ 静默判据

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
        var cfg = new UpdateConfig
        {
            RootPath = client,
            CacheDir = TestSupport.TestCacheDir(client),
            ManifestUrl = http.BaseUrl + "manifest.xml",
            ApplyMode = ApplyMode.Staged,
            ApplierPollInterval = TimeSpan.FromSeconds(1),
        };

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(cfg).Run().Outcome);

        // 让这棵树里真的有个进程在跑（暂存内容就是 ping 的字节，正好拿来当可执行文件）
        var exe = Path.Combine(client, "bin", "app.exe");
        File.Copy(StageLayout.NewPath(client, "bin/app.exe"), exe, overwrite: true);

        using var blocker = Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "-n", "25", "127.0.0.1" },
        })!;
        try
        {
            var blocked = new StagedApplier(cfg with { ApplierQuiescenceTimeout = TimeSpan.FromSeconds(3) }).Run();
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
        var landed = new StagedApplier(cfg with { ApplierQuiescenceTimeout = TimeSpan.FromSeconds(20) }).Run();
        Assert.Equal(UpdateOutcome.Updated, landed.Outcome);
        Assert.False(StageLayout.HasPendingPlan(client));
        Assert.Equal(TestSupport.Md5(TestSupport.Big("D2")),
            TestSupport.Md5File(Path.Combine(client, "bin", "data.bin")));
    }

    // ============================================================ CLI 契约

    /// <summary>宿主只需要 `Client.CLI --apply`：落地成功退出码 0、stdout 一行 JSON 的 Outcome=Updated。</summary>
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
                     "--manifest-url", e.Http.BaseUrl + "manifest.xml",
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
        AssertTreeIsVersion(e.Client, e.V2Dir, e.M2);
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
}