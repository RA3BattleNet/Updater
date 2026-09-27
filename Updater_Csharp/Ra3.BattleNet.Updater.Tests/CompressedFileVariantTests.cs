using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Models;
using CoreUpdater = Ra3.BattleNet.Updater.Core.Updater;
using Ra3.BattleNet.Updater.Core;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 完整文件的**预压缩变体** `files/{md5}.gz`（AGENT.md §4.6）。
///
/// 设计要点（结论：这件事完全由我们自己实现，**不依赖边缘任何能力**）：
/// 服务端写两份：`files/{md5}`（原文件，兼容路径）+ `files/{md5}.gz`；
/// 客户端**显式请求 .gz、自己解压**。于是：
/// - 不需要主机支持 `gzip_static` / 自动压缩二进制；
/// - URL 与 manifest 仍是**未压缩内容**的 md5，别的策略一概不动；
/// - **断点续传照旧可用**：Range 打在这个普通的 N 字节对象上，下完再解压。
/// </summary>
public class CompressedFileVariantTests
{
    private static (string Client, string Server, ManifestModel Manifest, string V2) Prepare(
        TempDir tmp, bool compress = true, int fileCount = 2)
    {
        var v1 = tmp.Sub("v1");
        var v2 = tmp.Sub("v2");

        // 内容要够大 + 可压缩，才能看出"网线字节 << 内容字节"
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
    public void ServerWritesGzSibling_AndClientPrefersIt()
    {
        using var tmp = new TempDir();
        var (client, server, manifest, v2) = Prepare(tmp);

        // 服务端确实写了两份
        // 服务端对"压得动"的文件才写 .gz 旁挂（小文件 gzip 反而更大 → 不写）；
        // 写了的一定比原文件小。客户端对没有旁挂的文件回落原文件。
        var filesDir = Path.Combine(server, "files");
        var raw = Directory.GetFiles(filesDir).Where(f => !f.EndsWith(".gz", StringComparison.Ordinal)).ToList();
        var gz = Directory.GetFiles(filesDir).Where(f => f.EndsWith(".gz", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, raw.Count);
        Assert.Equal(2, gz.Count);                       // 两个 256KB 的可压文件；9 字节的 keep.txt 不写
        Assert.All(gz, g => Assert.True(new FileInfo(g).Length < new FileInfo(g[..^3]).Length,
            $"{Path.GetFileName(g)} 既然写了就必须更小"));

        using var http = new TestHttpServer(server) { CompressPayloads = false };   // 不靠边缘压缩
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(0, result.FailedCount);
        TestSupport.AssertSameAs(manifest, v2, client);

        // 走的是 .gz（不是原文件），且没有去试原文件
        var paths = http.RequestPaths;
        Assert.True(2 == paths.Count(p => p.StartsWith("/files/") && p.EndsWith(".gz", StringComparison.Ordinal)),
            "应当恰好 2 个 .gz 请求；实际：" + string.Join(" | ", paths));
        Assert.DoesNotContain(paths, p => p.StartsWith("/files/") && !p.EndsWith(".gz", StringComparison.Ordinal));

        // 内容字节是"解压后"，网线字节是"压缩后" —— 后者必须明显更小
        Assert.True(result.BytesDownloaded >= 512 * 1024, $"内容字节应按解压后算（2×256KB），实得 {result.BytesDownloaded}");
        Assert.True(result.WireDownloaded < result.BytesDownloaded / 2,
            $"网线字节应当明显更小：wire={result.WireDownloaded} content={result.BytesDownloaded}");
    }

    [Fact]
    public void ServerWithoutGz_ProbeCountDoesNotScaleWithFileCount()
    {
        using var tmp = new TempDir();
        var (client, server, manifest, v2) = Prepare(tmp, compress: false, fileCount: 6);

        using var http = new TestHttpServer(server);
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        TestSupport.AssertSameAs(manifest, v2, client);

        // 探测只发生在"会话刚开始时同时在飞"的那几个文件上（上限 = 起始并发 2），
        // 不是每个文件都去问一次 —— 6 个文件也只有 2 次探测。
        var probes = http.RequestPaths.Count(p => p.StartsWith("/files/") && p.EndsWith(".gz", StringComparison.Ordinal));
        Assert.True(probes <= UpdateConfig.StartConcurrency, $"探测次数不该超过起始并发 {UpdateConfig.StartConcurrency}，实得 {probes}");
        Assert.True(probes < 6, $"探测次数不该随文件数增长，实得 {probes}");
        Assert.Equal(6, http.RequestPaths.Count(p => p.StartsWith("/files/") && !p.EndsWith(".gz", StringComparison.Ordinal)));
    }

    [Fact]
    public void CorruptGz_FallsBackToRawFile_WithoutCorruption()
    {
        using var tmp = new TempDir();
        var (client, server, manifest, v2) = Prepare(tmp);

        // 把 .gz 写成垃圾：客户端必须发现解压不了 → 放弃压缩变体 → 用原文件
        foreach (var g in Directory.GetFiles(Path.Combine(server, "files"), "*.gz"))
            File.WriteAllBytes(g, [1, 2, 3, 4, 5, 6, 7, 8]);

        using var http = new TestHttpServer(server);
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(0, result.FailedCount);
        TestSupport.AssertSameAs(manifest, v2, client);   // 结果必须逐字节正确

        // 坏 .gz 每个在飞文件最多试一次，之后整个会话改用原文件
        var probes = http.RequestPaths.Count(p => p.StartsWith("/files/") && p.EndsWith(".gz", StringComparison.Ordinal));
        Assert.True(probes <= UpdateConfig.StartConcurrency, $"坏 .gz 的探测次数不该超过起始并发，实得 {probes}");
    }

    [Fact]
    public void ResumeOnTheGzObject_WorksLikeAnyOtherFile()
    {
        using var tmp = new TempDir();
        var (client, server, manifest, v2) = Prepare(tmp);

        // 响应一律少发 1 字节 → 必须靠 Range 续传补回（**续传打的是 .gz 这个普通对象**）
        using var http = new TestHttpServer(server) { TruncateBytes = 1 };
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(0, result.FailedCount);
        TestSupport.AssertSameAs(manifest, v2, client);

        // 续传请求确实发生了（第二次请求带 Range）
        Assert.True(http.Requests > 3, $"应当发生续传，实得 {http.Requests} 个请求");
    }

    [Fact]
    public void CompressedVariantCanBeDisabled()
    {
        using var tmp = new TempDir();
        var (client, server, manifest, v2) = Prepare(tmp);

        using var http = new TestHttpServer(server);
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
            UseCompressedFiles = false,
        }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        TestSupport.AssertSameAs(manifest, v2, client);
        Assert.DoesNotContain(http.RequestPaths, p => p.StartsWith("/files/") && p.EndsWith(".gz", StringComparison.Ordinal));
        Assert.Equal(result.BytesDownloaded, result.WireDownloaded);   // 没压缩：内容字节 == 网线字节
    }
}
