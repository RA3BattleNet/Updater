using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;

namespace Ra3.BattleNet.Updater.Server;

public sealed record Baseline(string ManifestPath, string RootPath);

public sealed record PatchGenerationSummary(
    int Baselines,
    int PatchesCreated,
    int PatchesSkipped,
    int PatchesFailed,
    int FilesCopied,
    long PatchBytes,
    int PatchesPruned);

/// <summary>
/// 补丁与完整文件生成（AGENT.md §5.3 / §5.4）。
/// 无状态、无数据库：输入是"新版本 manifest + 若干基线 manifest"，输出是静态文件树。
/// 命名：完整文件 files/{md5}；补丁 patches/{oldHash}_{newHash}.hdiff。
/// </summary>
public static class PatchGenerator
{
    /// <summary>
    /// 生成补丁前的尺寸预过滤（省 CPU）。**默认 0 = 不过滤**：
    /// 权威规则是「补丁不小于完整文件就弃用」的后判断，见 Generate。
    /// </summary>
    public const long DefaultMinFileSize = 0;

    /// <summary>
    /// 给 <c>files/{md5}</c> 生成预压缩旁挂 <c>files/{md5}.gz</c>（**可选项，默认关闭**）。
    /// 客户端目前**不消费**它（见 AGENT.md §4.6）；开启只会有发布期 CPU 与约 +46% 存储的代价。
    /// 压不动的（小文件/已压过的二进制）不写：小文件 gzip 反而更大，写了纯亏。
    /// 已存在且比原文件小就跳过（幂等：重跑发布流水线不会白压一遍 1 GB）。
    /// **不追求字节确定性**：URL 的键是未压缩内容的 md5，客户端解压后照样校验那个 md5，
    /// 所以换个压缩级别、换个工具版本都不会影响正确性（这点和"把压缩字节当内容身份"完全不同）。
    /// </summary>
    private static void WriteGzipSibling(string rawPath)
    {
        var gzPath = rawPath + ".gz";
        try
        {
            var rawLength = new FileInfo(rawPath).Length;
            if (File.Exists(gzPath) && new FileInfo(gzPath).Length < rawLength) return;

            var tmp = gzPath + ".tmp";
            using (var input = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var gz = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
                input.CopyTo(gz, 1 << 20);

            // **压不动就不写**：小文件（几十字节的配置）gzip 之后反而更大，
            // 已经压过的二进制也压不动。客户端对这类文件本来就会 404 回落原文件，
            // 所以"有的文件有 .gz、有的没有"是完全正常的状态。
            if (new FileInfo(tmp).Length >= rawLength)
            {
                File.Delete(tmp);
                File.Delete(gzPath);   // 顺手清掉可能存在的旧旁挂
                return;
            }

            File.Move(tmp, gzPath, overwrite: true);
        }
        catch
        {
            // 压不出来不影响正确性：客户端会 404 → 回落原文件
        }
    }

    public static PatchGenerationSummary Generate(
        string newManifestPath,
        string newRoot,
        IReadOnlyList<Baseline> baselines,
        string outputDir,
        long minFileSize = DefaultMinFileSize,
        bool verify = true,
        bool prune = false,
        string? toolsDir = null,
        bool compressFiles = false)
    {
        var tools = toolsDir ?? AppContext.BaseDirectory;
        var filesDir = Path.Combine(outputDir, "files");
        var patchesDir = Path.Combine(outputDir, "patches");
        Directory.CreateDirectory(filesDir);
        Directory.CreateDirectory(patchesDir);

        var current = new ManifestModel(newManifestPath);
        var byUuid = current.Manifest.Files.ToDictionary(f => f.UUID);

        var copied = 0;
        var created = 0;
        var skipped = 0;
        var failed = 0;
        long patchBytes = 0;
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in current.Manifest.Files)
        {
            var full = FullPath(newRoot, f);
            if (!File.Exists(full)) continue;
            var target = Path.Combine(filesDir, f.MD5);
            if (!File.Exists(target))
            {
                File.Copy(full, target, overwrite: true);
                copied++;
            }

            // 预压缩变体 files/{md5}.gz（AGENT.md §4.6）：
            // **客户端显式请求它、自己解压** —— 不依赖边缘任何能力（gzip_static / Content-Encoding 都不用）。
            // 原文件必须同时保留：老客户端、或关掉该功能的客户端走的就是原文件那条路。
            if (compressFiles)
                WriteGzipSibling(target);
        }

        foreach (var baseline in baselines)
        {
            var old = new ManifestModel(baseline.ManifestPath);
            foreach (var oldFile in old.Manifest.Files)
            {
                if (!byUuid.TryGetValue(oldFile.UUID, out var newFile)) continue;
                if (string.Equals(oldFile.MD5, newFile.MD5, StringComparison.OrdinalIgnoreCase)) continue;
                if (oldFile.MD5.Length != 32 || newFile.MD5.Length != 32) continue;

                var newPath = FullPath(newRoot, newFile);
                if (!File.Exists(newPath)) continue;
                if (new FileInfo(newPath).Length < minFileSize) continue;

                var oldPath = FullPath(baseline.RootPath, oldFile);
                if (!File.Exists(oldPath)) continue;

                var name = $"{oldFile.MD5}_{newFile.MD5}.hdiff";
                expected.Add(name);

                var patchPath = Path.Combine(patchesDir, name);
                if (File.Exists(patchPath))
                {
                    skipped++;
                    continue;
                }

                var tmp = patchPath + ".tmp";
                if (!HdiffTool.Generate(tools, oldPath, newPath, tmp, out _))
                {
                    failed++;
                    continue;
                }

                if (verify && !VerifyPatch(tools, oldPath, tmp, newFile.MD5))
                {
                    failed++;
                    if (File.Exists(tmp)) File.Delete(tmp);
                    continue;
                }

                // **后判断**：补丁不比完整文件小就没有意义，直接弃用 → 客户端 404 → 走完整下载
                if (new FileInfo(tmp).Length >= new FileInfo(newPath).Length)
                    skipped++;
                else
                {
                    File.Move(tmp, patchPath, overwrite: true);
                    patchBytes += new FileInfo(patchPath).Length;
                    created++;
                }

                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        // 保留策略（§5.4）：只保留"当前基线集合能推导出来的"补丁，其余删除。
        // 删除是安全的：老客户端会 404 → 回落完整下载。
        var pruned = 0;
        if (prune)
        {
            foreach (var file in Directory.GetFiles(patchesDir, "*.hdiff"))
            {
                if (expected.Contains(Path.GetFileName(file))) continue;
                File.Delete(file);
                pruned++;
            }
        }

        return new PatchGenerationSummary(baselines.Count, created, skipped, failed, copied, patchBytes, pruned);
    }

    /// <summary>校验补丁确实能把旧文件还原成目标内容（AGENT.md §5.3）。</summary>
    private static bool VerifyPatch(string tools, string oldPath, string patchPath, string expectedMd5)
    {
        var outFile = patchPath + ".verify";
        try
        {
            if (!HdiffTool.Apply(tools, oldPath, patchPath, outFile, out _)) return false;

            using var s = new FileStream(outFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var md5 = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(s));
            return string.Equals(md5, expectedMd5, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(outFile)) File.Delete(outFile);
        }
    }

    private static string FullPath(string root, ManifestFile file) =>
        Path.Combine(root, file.RelativePath().Replace('/', Path.DirectorySeparatorChar));
}
