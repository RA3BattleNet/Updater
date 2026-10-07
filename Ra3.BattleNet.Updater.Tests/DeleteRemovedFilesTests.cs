using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Models;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// **删除功能**（`DELETE_DESIGN.md`）：只删"本地清单登记过、这一版却从远端清单消失"的受管文件，
/// 而且实现是**改名进 `UpdaterStage/old/`**（改名对占用/只读最宽容，§12.8；同卷 ⇒ 原子）。
///
/// 这里钉住的是四件最容易出错的事：
///   ① 该删的删掉、且落在 `old/`（不是真删，能回退）；
///   ② **大小写改名不算消失**（Windows 上那是同一个文件，按序数比较会误删）；
///   ③ 排除目录 / 没有可信基线时**一个都不删**；
///   ④ 暂存模式下由 applier 在提交阶段做（阶段一一个字节都不许动树，§12.2）。
/// </summary>
public class DeleteRemovedFilesTests
{
    /// <summary>造世界：v1 → v2 里 a.txt 变内容、gone.txt 消失、keep.txt 不变。</summary>
    private static (TempDir Tmp, string Client, string M1, string M2, TestHttpServer Http, UpdateConfig Cfg) Build(
        bool withLocalManifest = true, bool withGone = true)
    {
        var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v1, ("data/a.txt", "A1"), ("data/keep.txt", "K1"));
        TestSupport.WriteTree(v2, ("data/a.txt", "A2"), ("data/keep.txt", "K1"));
        if (withGone) TestSupport.WriteTree(v1, ("data/gone.txt", "G1"));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        ManifestGenerator.Generate(v2, m1, [], oldRoot: v1).Manifest.SaveToXml(m2);

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 0);
        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);

        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);
        if (withLocalManifest) File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);

        var http = new TestHttpServer(server);
        var cfg = new UpdateConfig
        {
            RootPath = client,
            CacheDir = TestSupport.TestCacheDir(client),
            ManifestUrl = http.BaseUrl + "manifest.xml",
        };
        return (tmp, client, m1, m2, http, cfg);
    }

    private static string P(string client, string rel) => Path.Combine(client, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>消失的文件被删掉、并且是**改名**进 `UpdaterStage/old/`（内容原样、可回退）。</summary>
    [Fact]
    public void RemovedFile_IsMovedIntoStageOld_AndCountedAsDeleted()
    {
        var (tmp, client, _, _, http, cfg) = Build();
        using var _ = tmp;
        using var __ = http;

        var r = new ClientUpdater(cfg).Run();

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.FailedCount);
        Assert.Equal(1, r.Deleted);                                  // 只删了 gone.txt
        Assert.False(File.Exists(P(client, "data/gone.txt")), "从清单消失的文件必须被删掉");
        Assert.Equal(TestSupport.Md5("A2"), TestSupport.Md5File(P(client, "data/a.txt")));
        Assert.Equal(TestSupport.Md5("K1"), TestSupport.Md5File(P(client, "data/keep.txt")));

        // 【核心】是改名不是真删：旧内容原样躺在 old/ 里（一代回退材料，§12.7）
        var backup = StageLayout.OldPath(client, "data/gone.txt");
        Assert.True(File.Exists(backup), "删除必须是改名进 UpdaterStage/old/，不能真删");
        Assert.Equal(TestSupport.Md5("G1"), TestSupport.Md5File(backup));

        // 日志：一行 delete、R 行列尾的 deleted=1
        var log = File.ReadAllLines(Path.Combine(TestSupport.TestCacheDir(client), "update.log"));
        var del = log.Single(l => l.StartsWith("F\t", StringComparison.Ordinal) && l.Split('\t')[7] == "delete");
        Assert.Equal(UpdateReasons.RemovedFromManifest, del.Split('\t')[9]);
        var run = log.Last(l => l.StartsWith("R\t", StringComparison.Ordinal)).Split('\t');
        Assert.Equal(16, run.Length);
        Assert.Equal("1", run[15]);
        // 删除**不进** total：total 仍是远端条目数（S15 的恒等式）
        Assert.Equal(r.Total, r.Skipped + r.Moved + r.Patched + r.Full);
    }

    /// <summary>
    /// **只改大小写不算消失**。Windows 上 `data/Ra3BattleNet/x.txt` 与 `data/Ra3battlenet/x.txt`
    /// 是**同一个文件**——按序数比较会把它判成"消失"，于是刚放好的新文件被自己删掉。
    /// （LiveContent 线 v2→v3 实测有 21 对这样的改名。）
    /// </summary>
    [Fact]
    public void CaseOnlyPathChange_IsNotTreatedAsRemoval()
    {
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v1, ("data/Ra3BattleNet/x.txt", "X1"));
        TestSupport.WriteTree(v2, ("data/Ra3battlenet/x.txt", "X1"));   // 同一内容，只改了目录大小写

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
        };

        var r = new ClientUpdater(cfg).Run();

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.Deleted);
        Assert.True(File.Exists(P(client, "data/Ra3BattleNet/x.txt")), "大小写改名不是删除对象，那个文件必须还在");
    }

    /// <summary>排除目录下的路径即便从清单消失也**绝不删**（发布侧漏标时的第二道防线）。</summary>
    [Fact]
    public void PathsUnderExcludedDirs_AreNeverDeleted()
    {
        // v1 清单里**有** RA3BattleNetData/user.dat（模拟发布侧没排除），v2 里没有
        using var tmp = new TempDir();
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v1, ("data/a.txt", "A1"), ("RA3BattleNetData/user.dat", "U1"));
        TestSupport.WriteTree(v2, ("data/a.txt", "A2"));

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
            ExcludedDirs = ["RA3BattleNetData"],
        };

        var r = new ClientUpdater(cfg).Run();

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.Deleted);
        Assert.True(File.Exists(P(client, "RA3BattleNetData/user.dat")), "排除目录下的文件永远不删");
    }

    /// <summary>**没有可信基线 ⇒ 一个都不删**：不知道哪些文件是我们登记过的，就没资格删。</summary>
    [Fact]
    public void WithoutTrustworthyBaseline_NothingIsDeleted()
    {
        var (tmp, client, _, _, http, cfg) = Build(withLocalManifest: false);
        using var _ = tmp;
        using var __ = http;

        var r = new ClientUpdater(cfg).Run();

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.Deleted);
        Assert.True(File.Exists(P(client, "data/gone.txt")), "没有本地清单时不能删任何东西");
    }

    /// <summary>
    /// **暂存模式**：阶段一不许动树（§12.2），删除由 applier 在提交阶段最后做。
    /// 这里用一个**空计划**的最小场景：只验"applier 按两份清单的差集把文件改名进 old/，
    /// 然后才推进本地清单"。删除集由 applier 从清单事实自算（§12.4），不来自 plan.json。
    /// </summary>
    [Fact]
    public void StagedApplier_DeletesRemovedFiles_FromTheManifestDiff()
    {
        var (tmp, client, m1, m2, http, _) = Build();
        using var _t = tmp;
        using var _h = http;

        var remoteBytes = File.ReadAllBytes(m2);
        var cacheDir = TestSupport.TestCacheDir(client);
        Directory.CreateDirectory(cacheDir);
        // 阶段一留在缓存里的**远端清单原文**（applier 的唯一输入，§12.5）
        File.Copy(m2, Path.Combine(cacheDir, "manifest.remote.xml"), overwrite: true);
        // 空计划：本轮没有任何放置/改名，只有"清单差集"要删
        StageLayout.SavePlan(client, new StagedPlan(
            Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(remoteBytes)), []));

        var r = new StagedApplier(new ApplierConfig { RootPath = client, CacheDir = cacheDir }).Run();

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.FailedCount);
        Assert.Equal(1, r.Deleted);
        Assert.False(File.Exists(P(client, "data/gone.txt")), "applier 也要按差集删");
        Assert.True(File.Exists(StageLayout.OldPath(client, "data/gone.txt")), "而且同样是改名进 old/");
        // 本地清单在删除之后才被推进，且是远端原文（§3.4）
        Assert.Equal(remoteBytes, File.ReadAllBytes(Path.Combine(client, "manifest.xml")));
        Assert.False(StageLayout.HasPendingPlan(client), "落地完成后计划被清掉");
    }

    /// <summary>
    /// **链接穿透护栏**（`ReparseGuard`）：待删路径的父目录若是 junction，改名/删除会**穿透**到安装根之外。
    /// 这种路径在字符串上完全合法（§4.14 看不见），所以必须在这里拦：**跳过 + 如实归因**。
    /// 跳过不是失败 —— 记成失败会让本轮永远不写清单（永久卡住）。
    /// </summary>
    [Fact]
    public void PathUnderAJunction_IsSkipped_NotDeletedThroughTheLink()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("跳过：只有 Windows 有 junction"); return; }

        using var tmp = new TempDir();
        var outside = tmp.Sub("outside");
        TestSupport.WriteTree(outside, ("victim.txt", "OUTSIDE"));

        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v1, ("data/a.txt", "A1"));
        TestSupport.WriteTree(v2, ("data/a.txt", "A2"));

        // 发布树里不会有链接 —— 这是**客户端磁盘状态**：v1/link 是 junction，指向根之外
        var link = Path.Combine(v1, "link");
        if (!TryJunction(link, outside)) { Console.WriteLine("跳过：本机建不了 junction"); return; }

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);   // 会跟着链接把 link/victim.txt 收进清单
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        ManifestGenerator.Generate(v2, m1, [], oldRoot: v1).Manifest.SaveToXml(m2);
        Assert.Contains(new ManifestModel(m1).Manifest.Files, f => f.RelativePath() == "link/victim.txt");

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 0);
        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);

        // 客户端：真实文件 + 旧清单 + 同款 junction（CopyTree 会把链接解引用，所以这里手工搭）
        var client = tmp.Sub("client");
        TestSupport.WriteTree(client, ("data/a.txt", "A1"));
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);
        if (!TryJunction(Path.Combine(client, "link"), outside)) { Console.WriteLine("跳过：本机建不了 junction"); return; }

        using var http = new TestHttpServer(server);
        var cfg = new UpdateConfig
        {
            RootPath = client,
            CacheDir = TestSupport.TestCacheDir(client),
            ManifestUrl = http.BaseUrl + "manifest.xml",
        };

        var r = new ClientUpdater(cfg).Run();

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);            // 跳过不算失败
        Assert.True(File.Exists(Path.Combine(outside, "victim.txt")),
            "绝不能穿透 junction 删掉安装根之外的文件");
        var log = File.ReadAllLines(Path.Combine(TestSupport.TestCacheDir(client), "update.log"));
        Assert.Contains(log, l => l.StartsWith("F\t", StringComparison.Ordinal)
                                  && l.Contains("\tdelete\t") && l.Contains(UpdateReasons.ReparsePoint));
    }

    /// <summary>建目录 junction（`mklink /J` 不需要管理员权限）。失败返回 false（调用方跳过）。</summary>
    private static bool TryJunction(string linkPath, string targetPath)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch { return false; }
    }
}