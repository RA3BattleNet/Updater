using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Models;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;
using Ra3.BattleNet.Updater.Client;

namespace Ra3.BattleNet.Updater.Tests;

public class EndToEndTests
{
    [Fact]
    public void SyntheticTree_UpdatesIncrementally_AndMatchesNewVersionByteForByte()
    {
        using var tmp = new TempDir();

        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1,
            ("bin/a.dll", TestSupport.Big("VERSION-1")),
            ("bin/keep.txt", "keep me"),
            ("data/old.bin", "shared-content"));

        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2,
            ("bin/a.dll", TestSupport.Big("VERSION-2")),
            ("bin/keep.txt", "keep me"),
            ("data/new.bin", "shared-content"),
            ("bin/c.dll", "brand new file"));

        // 1) 生成两份清单（第二份以第一份为基线，继承 UUID）
        var m1Path = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1Path);

        var gen2 = ManifestGenerator.Generate(v2, m1Path, [], oldRoot: v1);
        var m2Path = Path.Combine(tmp.Path, "v2.xml");
        gen2.Manifest.SaveToXml(m2Path);

        Assert.Equal(1, gen2.Added.Count);                 // bin/c.dll
        Assert.Equal(3, gen2.Modified.Count + gen2.Unchanged.Count + gen2.Moved.Count);

        // 2) 生成服务端静态树：files/{md5} + patches/{old}_{new}.bin
        var server = tmp.Sub("server");
        var summary = PatchGenerator.Generate(m2Path, v2, [new Baseline(m1Path, v1)], server, minFileSize: 0);

        Assert.Equal(1, summary.PatchesCreated);           // 只有 bin/a.dll 需要补丁
        Assert.Equal(0, summary.PatchesFailed);

        var patchFiles = Directory.GetFiles(Path.Combine(server, "patches"));
        Assert.Single(patchFiles);
        Assert.EndsWith(".bin", patchFiles[0]);
        Assert.Matches("^[0-9a-f]{32}_[0-9a-f]{32}\\.bin$", Path.GetFileName(patchFiles[0]));

        File.Copy(m2Path, Path.Combine(server, "manifest.xml"), overwrite: true);

        // 3) 客户端根 = v1 的一份拷贝 + 本地清单
        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);
        File.Copy(m1Path, Path.Combine(client, "manifest.xml"), overwrite: true);

        using var http = new TestHttpServer(server);
        var cfg = new UpdateConfig
        {
            RootPath = client,
            CacheDir = TestSupport.TestCacheDir(client),
            ManifestUrl = http.BaseUrl + "manifest.xml",
        };

        var result = new ClientUpdater(cfg).Run();

        Assert.True(result.Outcome == UpdateOutcome.Updated, $"期望 Updated，实得 {result}；Detail={result.Detail}");
        Assert.True(result.Patched >= 1, "必须至少有一个文件走补丁");
        Assert.True(result.Moved >= 1, "纯改名应当走 Move（0 下载）");
        Assert.True(result.Full >= 1, "新增文件应当走完整下载");
        Assert.True(result.Skipped >= 1, "未变文件应当跳过");
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(string.Empty, result.Detail);
        Assert.Equal("HTTP/1.1", result.HttpVersion);   // 明文连接

        // 4) 结果与 v2 逐字节一致
        TestSupport.AssertSameAs(gen2.Manifest, v2, client);
        Assert.False(File.Exists(Path.Combine(client, "data", "old.bin")), "纯改名后旧路径不应残留");

        // 5) 日志必须能证明「补丁确实命中」
        var log = File.ReadAllLines(Path.Combine(client, "UpdaterCache", "update.log"));
        Assert.Contains(log, l => l.StartsWith("F\t", StringComparison.Ordinal) && l.Contains("\tpatch\t"));
        Assert.Contains(log, l => l.StartsWith("F\t", StringComparison.Ordinal) && l.Contains("\tmove\t"));
        var runLine = log.Single(l => l.StartsWith("R\t", StringComparison.Ordinal));
        Assert.Matches(@"^R\t[^\t]+\t[0-9a-f]{32}\t", runLine);  // R 行必须带 manifest 哈希
        Assert.Contains("\tUpdated", runLine);

        // 6) 再来一次：必须早退（304 或内容哈希一致），不做任何下载
        var before = http.Requests;
        var again = new ClientUpdater(cfg).Run();
        Assert.Equal(UpdateOutcome.UpToDate, again.Outcome);
        Assert.Equal(0, again.BytesDownloaded);
        Assert.True(http.NotModified >= 1, "第二次应当命中 If-None-Match 304");
        Assert.Contains("gzip", http.LastAcceptEncoding ?? string.Empty);   // 客户端开启透明压缩（§4.6）
        Assert.True(http.Requests - before <= 2);
    }

    [Fact]
    public void NoLocalManifest_VerifiesDiskInsteadOfRedownloadingEverything()
    {
        using var tmp = new TempDir();

        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("a.bin", TestSupport.Big("ONE")), ("b.txt", "hello"), ("c/d.txt", "nested"));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m1, v1, [], server);
        File.Copy(m1, Path.Combine(server, "manifest.xml"), overwrite: true);

        // 客户端目录内容与远端完全一致，但**没有本地清单**
        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);

        using var http = new TestHttpServer(server);
        var cfg = new UpdateConfig { RootPath = client, CacheDir = TestSupport.TestCacheDir(client), ManifestUrl = http.BaseUrl + "manifest.xml" };
        var result = new ClientUpdater(cfg).Run();

        // 必须退化为「按磁盘哈希校验」：花 CPU，不花带宽
        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(3, result.Skipped);
        Assert.Equal(0, result.Full);
        Assert.Equal(0, result.BytesDownloaded);
        Assert.Equal(0, http.NotFound);   // 没有前身，连补丁都不该去探测
    }

    [Fact]
    public void WorkloadPredicate_TriggersNeedsFullPackage()
    {
        using var tmp = new TempDir();

        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("a.txt", "A1"), ("b.txt", "B1"), ("c.txt", "C1"));

        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2, ("a.txt", "A2"), ("b.txt", "B2"), ("c.txt", "C2"));

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
            FullPackageThresholdFiles = 1,   // 待下载 >= 1 即判定需要完整包
        };

        var result = new ClientUpdater(cfg).Run();
        Assert.True(result.Outcome == UpdateOutcome.NeedsHostFallback, $"期望 NeedsHostFallback，实得 {result}；Detail={result.Detail}");
        Assert.Equal(UpdateReasons.WorkloadTooLarge, result.Reason);

        // 不得改动任何文件
        Assert.Equal(TestSupport.Md5("A1"), TestSupport.Md5File(Path.Combine(client, "a.txt")));
    }

    [Fact]
    public void MissingPatchFile_FallsBackToFullDownload()
    {
        using var tmp = new TempDir();

        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("a.bin", TestSupport.Big("ONE")));
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2, ("a.bin", TestSupport.Big("TWO")));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        var gen2 = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        gen2.Manifest.SaveToXml(m2);

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m2, v2, [new Baseline(m1, v1)], server, minFileSize: 0);
        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);

        // 故意删掉补丁，模拟"服务端没有该内容对的补丁"
        foreach (var p in Directory.GetFiles(Path.Combine(server, "patches"))) File.Delete(p);

        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);

        using var http = new TestHttpServer(server);
        var cfg = new UpdateConfig { RootPath = client, CacheDir = TestSupport.TestCacheDir(client), ManifestUrl = http.BaseUrl + "manifest.xml" };
        var result = new ClientUpdater(cfg).Run();

        Assert.True(result.Outcome == UpdateOutcome.Updated, $"期望 Updated，实得 {result}；Detail={result.Detail}");
        Assert.Equal(0, result.Patched);
        Assert.Equal(1, result.Full);
        Assert.True(http.NotFound >= 1, "应当真的去探测过补丁并收到 404");
        TestSupport.AssertSameAs(gen2.Manifest, v2, client);
    }
}
