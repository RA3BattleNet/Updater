using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Server;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 「缓存比树新」这类状态必须被自愈，而不是静默判"已最新"：
///   ① 命中 304 时只看 ETag 不看本地清单 → 降级/覆盖安装、还原备份后**永久静默不更新**；
///   ② `--verify-unchanged` 到不了逐文件校验循环 → 宿主的"修复资源"在主场景下空转。
/// 这两条相互放大（缓存默认在安装根之外，重装不会清），所以放在一起钉。
/// </summary>
public class ManifestFreshnessTests
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

        return new Env(client, server, v1, v2, m1, m2, new TestHttpServer(server));
    }

    private static UpdateConfig Cfg(Env e) => new()
    {
        RootPath = e.Client,
        CacheDir = TestSupport.TestCacheDir(e.Client),
        ManifestUrl = e.Http.BaseUrl + "manifest.xml",
    };

    /// <summary>受管文件逐字节等于 v2，且本地清单就是 m2 的原样字节。</summary>
    private static void AssertClientIsAtV2(Env e)
    {
        foreach (var rel in new[] { "bin/a.dll", "bin/b.dll", "data/x.dat", "bin/new.dll" })
            Assert.Equal(TestSupport.Md5File(Path.Combine(e.V2Dir, rel)),
                         TestSupport.Md5File(Path.Combine(e.Client, rel)));
        Assert.Equal(TestSupport.Md5File(e.M2), TestSupport.Md5File(Path.Combine(e.Client, "manifest.xml")));
    }

    /// <summary>先正常更新到最新：本地清单 = m2、ETag 就位、文件都是 v2。返回缓存里的两个文件名。</summary>
    private static (string Etag, string Remote) BringUpToDate(Env e)
    {
        Assert.Equal(UpdateOutcome.Updated, new ClientUpdater(Cfg(e)).Run().Outcome);
        AssertClientIsAtV2(e);
        var cache = TestSupport.TestCacheDir(e.Client);
        return (Path.Combine(cache, "manifest.etag"), Path.Combine(cache, "manifest.remote.xml"));
    }

    [Fact]
    public void RolledBackLocalManifest_WithWarmCache_IsRepaired_NotSilentlyUpToDate()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        BringUpToDate(e);

        // 模拟降级重装 / 还原备份：把**旧清单**拷回安装根；缓存（含 ETag）原样不动。
        // 下次请求带 If-None-Match → 服务端 304（远端清单确实没变）→ 旧代码就此判"已最新"，
        // 而本地其实落后一整版，且**再也不会**更新。
        File.Copy(e.M1, Path.Combine(e.Client, "manifest.xml"), overwrite: true);

        var again = new ClientUpdater(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.Updated, again.Outcome);
        AssertClientIsAtV2(e);
    }

    [Fact]
    public void WarmEtag_WithoutCachedRemoteManifest_DoesNotTrust304()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        var (_, remote) = BringUpToDate(e);

        // 同样的"树落后"，但缓存里**没有远端清单**可以拿来核对 → 必须无条件重取，而不是信 304。
        File.Delete(remote);
        File.Copy(e.M1, Path.Combine(e.Client, "manifest.xml"), overwrite: true);

        var again = new ClientUpdater(Cfg(e)).Run();

        Assert.Equal(UpdateOutcome.Updated, again.Outcome);
        AssertClientIsAtV2(e);
    }

    [Fact]
    public void VerifyUnchanged_RepairsACorruptedFile_EvenWhenTheManifestIsUnchanged()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        BringUpToDate(e);

        // 清单与远端一致，但**磁盘上的文件坏了**（用户/别的程序动过）。
        // 这正是宿主"修复资源"按钮要处理的场景。
        File.WriteAllText(Path.Combine(e.Client, "bin/a.dll"), "corrupted");

        var result = new ClientUpdater(Cfg(e) with { VerifyUnchangedFiles = true }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.True(result.Full >= 1, "坏文件应当被重新完整下载（而不是什么都不校验就报已最新）");
        AssertClientIsAtV2(e);
    }

    [Fact]
    public void VerifyUnchanged_WithEverythingHealthy_StillReportsUpToDate_ButDidHashEverything()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _ = e.Http;

        BringUpToDate(e);

        // 全好时：逐文件校验过（慢是预期的），没有任何东西需要下 → 报 Updated（清单原样重写）
        var result = new ClientUpdater(Cfg(e) with { VerifyUnchangedFiles = true }).Run();

        Assert.Equal(0, result.Patched);
        Assert.Equal(0, result.Full);
        AssertClientIsAtV2(e);
    }
}