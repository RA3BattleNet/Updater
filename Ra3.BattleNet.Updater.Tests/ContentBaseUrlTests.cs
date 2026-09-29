using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Server;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// §3.2：清单地址与**内容**（files/ 、patches/）基准地址必须能分开配置 ——
/// 典型部署是"清单放一台小主机、内容放 CDN"。这里用**两个独立服务器**钉住它：
/// 清单服务器只被请求 manifest.xml，内容服务器只被请求 files/ 与 patches/。
/// </summary>
public class ContentBaseUrlTests
{
    private sealed record Env(string Client, string V2Dir, string M2, TestHttpServer ManifestHost, TestHttpServer ContentHost);

    private static Env Prepare(TempDir tmp)
    {
        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1,
            ("bin/a.dll", TestSupport.Big("A1")),
            ("data/x.dat", TestSupport.Big("X1")));
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2,
            ("bin/a.dll", TestSupport.Big("A2")),
            ("data/x.dat", TestSupport.Big("X2")),
            ("bin/new.dll", TestSupport.Big("NEW")));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        ManifestGenerator.Generate(v2, m1, [], oldRoot: v1).Manifest.SaveToXml(m2);

        var all = tmp.Sub("server-all");
        PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], all, minFileSize: 0);
        File.Copy(m2, Path.Combine(all, "manifest.xml"), overwrite: true);

        // 把产物拆成两个"源"：清单服务器只有 manifest.xml；内容服务器只有 files/ 与 patches/
        var manifestHost = tmp.Sub("host-manifest");
        Directory.CreateDirectory(manifestHost);
        File.Copy(Path.Combine(all, "manifest.xml"), Path.Combine(manifestHost, "manifest.xml"));
        var contentHost = tmp.Sub("host-content");
        Directory.CreateDirectory(contentHost);
        Directory.Move(Path.Combine(all, "files"), Path.Combine(contentHost, "files"));
        Directory.Move(Path.Combine(all, "patches"), Path.Combine(contentHost, "patches"));

        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);

        return new Env(client, v2, m2, new TestHttpServer(manifestHost), new TestHttpServer(contentHost));
    }

    private static void AssertAtV2(Env e)
    {
        foreach (var rel in new[] { "bin/a.dll", "data/x.dat", "bin/new.dll" })
            Assert.Equal(TestSupport.Md5File(Path.Combine(e.V2Dir, rel)),
                         TestSupport.Md5File(Path.Combine(e.Client, rel)));
        Assert.Equal(TestSupport.Md5File(e.M2), TestSupport.Md5File(Path.Combine(e.Client, "manifest.xml")));
    }

    [Fact]
    public void ManifestAndContent_CanComeFromDifferentHosts()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _m = e.ManifestHost;
        using var _c = e.ContentHost;

        var cfg = new UpdateConfig
        {
            RootPath = e.Client,
            CacheDir = TestSupport.TestCacheDir(e.Client),
            ManifestUrl = e.ManifestHost.BaseUrl + "manifest.xml",
            BaseUrl = e.ContentHost.BaseUrl,
        };

        var result = new ClientUpdater(cfg).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        AssertAtV2(e);

        // 清单服务器：只被要过 manifest.xml（内容一个字都不该问它要）
        Assert.True(e.ManifestHost.Requests > 0, "清单服务器应当被请求过");
        Assert.All(e.ManifestHost.RequestPaths, p => Assert.EndsWith("manifest.xml", p));

        // 内容服务器：只被要过 files/ 与 patches/，从没要过清单
        Assert.True(e.ContentHost.Requests > 0, "内容服务器应当被请求过");
        Assert.All(e.ContentHost.RequestPaths,
            p => Assert.True(p.Contains("/files/") || p.Contains("/patches/"), $"不该向内容服务器请求 {p}"));
    }

    [Fact]
    public void BaseUrl_WithoutTrailingSlash_StillWorks()
    {
        using var tmp = new TempDir();
        var e = Prepare(tmp);
        using var _m = e.ManifestHost;
        using var _c = e.ContentHost;

        var cfg = new UpdateConfig
        {
            RootPath = e.Client,
            CacheDir = TestSupport.TestCacheDir(e.Client),
            ManifestUrl = e.ManifestHost.BaseUrl + "manifest.xml",
            BaseUrl = e.ContentHost.BaseUrl.TrimEnd('/'),      // 末尾不写斜杠也要能用
        };

        Assert.Equal(UpdateOutcome.Updated, new ClientUpdater(cfg).Run().Outcome);
        AssertAtV2(e);
    }
}