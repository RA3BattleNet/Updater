using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Models;
using CoreUpdater = Ra3.BattleNet.Updater.Core.Updater;
using Ra3.BattleNet.Updater.Core;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// <c>files/{md5}.gz</c> 预压缩旁挂（AGENT.md §4.6）。
///
/// 决策记录（需求方 2026-09-27）：
/// - **客户端不消费它**：默认不访问、也没有开关可以打开。那条分支已按决策删除；
///   规范里保留了协议描述，将来真要用时照规范重新实现。
/// - **服务端只保留生成能力**：`--compress-files` 可选、**默认关闭**；开启只有发布期 CPU
///   与约 +46% 存储的代价，收益只在"整块替换运行时"那类发布上体现。
/// - 压不动的文件不写（小文件 gzip 反而更大），所以**有旁挂和没旁挂都是正常状态**。
/// </summary>
public class CompressedSiblingTests
{
    private static (string Client, string Server, ManifestModel Manifest, string V2) Prepare(
        TempDir tmp, bool compress, int fileCount = 2)
    {
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v1, ("data/keep.txt", "unchanged"));
        TestSupport.WriteTree(v2,
            Enumerable.Range(0, fileCount)
                .Select(i => ($"bin/f{i}.dll", TestSupport.Big($"PAYLOAD-{i}", 256 * 1024)))
                .Append(("data/keep.txt", "unchanged"))
                .ToArray());

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var gen = ManifestGenerator.Generate(v2, m1, [], oldRoot: v1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        gen.Manifest.SaveToXml(m2);

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m2, v2, [], server, compressFiles: compress);
        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);

        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);

        return (client, server, gen.Manifest, v2);
    }

    [Fact]
    public void SiblingsAreOffByDefault()
    {
        using var tmp = new TempDir();
        var (_, server, _, _) = Prepare(tmp, compress: false);

        var files = Directory.GetFiles(Path.Combine(server, "files"));
        Assert.Equal(3, files.Length);
        Assert.DoesNotContain(files, f => f.EndsWith(".gz", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenEnabled_WritesSiblingsOnlyWhenTheyActuallyHelp()
    {
        using var tmp = new TempDir();
        var (_, server, _, _) = Prepare(tmp, compress: true);

        var filesDir = Path.Combine(server, "files");
        var raw = Directory.GetFiles(filesDir).Where(f => !f.EndsWith(".gz", StringComparison.Ordinal)).ToList();
        var gz = Directory.GetFiles(filesDir).Where(f => f.EndsWith(".gz", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, raw.Count);                     // 原文件永远都在（兼容路径）
        Assert.Equal(2, gz.Count);                      // 两个 256KB 可压文件；9 字节的 keep.txt 不写
        Assert.All(gz, g => Assert.True(new FileInfo(g).Length < new FileInfo(g[..^3]).Length,
            $"{Path.GetFileName(g)} 既然写了就必须比原文件小"));
    }

    /// <summary>最重要的一条：**客户端绝不访问 `.gz`** —— 即使服务端把旁挂都准备好了。</summary>
    [Fact]
    public void ClientIgnoresSiblings_EvenWhenTheServerHasThem()
    {
        using var tmp = new TempDir();
        var (client, server, manifest, v2) = Prepare(tmp, compress: true);

        using var http = new TestHttpServer(server);
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(0, result.FailedCount);
        TestSupport.AssertSameAs(manifest, v2, client);

        Assert.DoesNotContain(http.RequestPaths, p => p.EndsWith(".gz", StringComparison.Ordinal));
        Assert.Equal(2, http.RequestPaths.Count(p => p.StartsWith("/files/") && !p.EndsWith(".gz", StringComparison.Ordinal)));

        // 没有端到端压缩：网线字节 ≈ 内容字节 + 清单（清单也计入上网字节）
        Assert.True(result.PayloadBytes > result.BytesDownloaded, "网线字节应当还包含清单本身");
        Assert.True(result.PayloadBytes < result.BytesDownloaded * 1.5,
            $"没压缩时两者应当接近：wire={result.PayloadBytes} content={result.BytesDownloaded}");
    }
}
