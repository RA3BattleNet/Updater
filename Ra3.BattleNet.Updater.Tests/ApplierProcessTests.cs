using System.Diagnostics;
using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// **把 applier 当进程真跑**（签入的那个单文件 exe），因为它才是现场真正会被启动的东西。
/// 两条：
///   ① 服务端彻底关掉之后仍能落地（零网络）；
///   ② **自更新**：applier 改名**自己正在运行的那个 exe** —— 宿主自更新靠的就是这一招，
///      所以这条必须在真进程上验一次（进程内调用测不到"运行中的映像能不能被改名"）。
/// </summary>
public class ApplierProcessTests
{
    private sealed record Env(string Client, string Server, string V1Dir, string V2Dir,
        string M1, string M2, TestHttpServer Http);

    private static string ApplierRel => $"applier_bin/{HdiffTool.Rid}/"
        + (OperatingSystem.IsWindows() ? "Client.Applier.exe" : "Client.Applier");

    private static Env Prepare(TempDir tmp, Action<string, string>? decorate = null)
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

        decorate?.Invoke(v1, v2);

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

        return new Env(client, server, v1, v2, m1, m2, new TestHttpServer(server));
    }

    private static UpdateConfig Cfg(Env e) => new()
    {
        RootPath = e.Client,
        CacheDir = TestSupport.TestCacheDir(e.Client),
        ManifestUrl = e.Http.BaseUrl + "manifest.xml",
        ApplyMode = ApplyMode.Staged,
    };

    /// <summary>
    /// 启动 applier 进程（**手工给参数**）：`BuildApplyCommand` 会自动填"当前进程 = 宿主"的身份三件套，
    /// 而这里我们是测试进程、不该被它等着退出（那一套 argv 契约由 <c>ApplierConfigTests</c> 钉）。
    /// </summary>
    private static int RunApplier(string exePath, Env e, int timeoutSeconds = 90)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
                 {
                     "--root", e.Client,
                     "--cache-dir", TestSupport.TestCacheDir(e.Client),
                     "--quiescence-timeout", "30",
                     "--poll-seconds", "1",
                 })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(timeoutSeconds * 1000), "applier 超时未退出");
        if (p.ExitCode != 0) Console.WriteLine($"applier[{Path.GetFileName(exePath)}] 退出码 {p.ExitCode}：{stderr.Trim()}");
        return p.ExitCode;
    }

    private static void AssertLanded(Env e)
    {
        Assert.Equal(TestSupport.Md5File(e.M2), TestSupport.Md5File(Path.Combine(e.Client, "manifest.xml")));
        foreach (var f in new ManifestModel(e.M2).Manifest.Files)
        {
            var rel = f.RelativePath().Replace('/', Path.DirectorySeparatorChar);
            Assert.Equal(f.MD5, TestSupport.Md5File(Path.Combine(e.Client, rel)));
        }
    }

    /// <summary>零网络：阶段一跑完后把测试服务器彻底关掉，再让 applier 落地。</summary>
    [Fact]
    public void PackagedApplier_LandsWithTheServerGone()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);
        e.Http.Dispose();                                                  // 服务端此刻已经不存在了

        var applier = StagedApplier.FindDefaultApplierExe();
        Assert.False(string.IsNullOrEmpty(applier));

        Assert.Equal(0, RunApplier(applier!, e));
        AssertLanded(e);
        Assert.False(StageLayout.HasPendingPlan(e.Client));
    }

    /// <summary>
    /// **自更新**：树里那份旧 applier 要改名它**自己正在运行的那个 exe**（新版本 = 真 exe + 尾部一个字节，
    /// 仍是合法 PE）。宿主自更新（替换自己的 exe/dll）靠的就是"永远改名不覆盖"这一招 ——
    /// 这里用真实的进程把这条路走通，而不是靠推理。
    /// </summary>
    [Fact]
    public void SelfUpdate_TheApplierRenamesItsOwnRunningImage()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp, (v1, v2) =>
        {
            var real = StagedApplier.FindDefaultApplierExe();
            Assert.False(string.IsNullOrEmpty(real), "找不到签入的 applier 产物");
            var rel = ApplierRel.Replace('/', Path.DirectorySeparatorChar);

            var oldPath = Path.Combine(v1, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
            File.Copy(real!, oldPath, overwrite: true);

            var newPath = Path.Combine(v2, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            var bytes = File.ReadAllBytes(real!);
            File.WriteAllBytes(newPath, [.. bytes, 0x00]);                 // 新版本：内容变了、仍是可执行文件
        });

        Assert.Equal(UpdateOutcome.Staged, new ClientUpdater(Cfg(e)).Run().Outcome);

        // 用**树里那份**（即将被替换的旧副本）当 applier
        var applierInTree = Path.Combine(e.Client, ApplierRel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(applierInTree), "树里应当有随包携带的 applier");

        Assert.Equal(0, RunApplier(applierInTree, e));
        AssertLanded(e);                                                   // 含那个 exe：内容已换成新版本
        Assert.Equal(TestSupport.Md5File(Path.Combine(e.V2Dir, ApplierRel.Replace('/', Path.DirectorySeparatorChar))),
            TestSupport.Md5File(applierInTree));
    }
}