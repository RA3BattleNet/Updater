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
    long PatchBytes);

/// <summary>
/// 补丁与完整文件生成（AGENT.md §5.3 / §5.4）。
/// 无状态、无数据库：输入是"新版本 manifest + 若干基线 manifest"，输出是静态文件树。
/// 命名：完整文件 files/{md5}；补丁 patches/{oldHash}_{newHash}.hdiff。
/// </summary>
public static class PatchGenerator
{
    /// <summary>小于该大小的文件不生成补丁（AGENT.md §5.3 / K6）。</summary>
    public const long DefaultMinFileSize = 16 * 1024;

    public static PatchGenerationSummary Generate(
        string newManifestPath,
        string newRoot,
        IReadOnlyList<Baseline> baselines,
        string outputDir,
        long minFileSize = DefaultMinFileSize,
        bool verify = true,
        string? toolsDir = null)
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

        foreach (var f in current.Manifest.Files)
        {
            if (!TryPath(newRoot, f, out var full) || !File.Exists(full)) continue;
            var target = Path.Combine(filesDir, f.MD5);
            if (File.Exists(target)) continue;
            File.Copy(full, target, overwrite: true);
            copied++;
        }

        foreach (var baseline in baselines)
        {
            var old = new ManifestModel(baseline.ManifestPath);
            foreach (var oldFile in old.Manifest.Files)
            {
                if (!byUuid.TryGetValue(oldFile.UUID, out var newFile)) continue;
                if (string.Equals(oldFile.MD5, newFile.MD5, StringComparison.OrdinalIgnoreCase)) continue;
                if (oldFile.MD5.Length != 32 || newFile.MD5.Length != 32) continue;

                if (!TryPath(newRoot, newFile, out var newPath) || !File.Exists(newPath)) continue;
                if (new FileInfo(newPath).Length < minFileSize) continue;
                if (!TryPath(baseline.RootPath, oldFile, out var oldPath) || !File.Exists(oldPath)) continue;

                var patchPath = Path.Combine(patchesDir, $"{oldFile.MD5}_{newFile.MD5}.hdiff");
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

                if (verify && !VerifyPatch(tools, oldPath, tmp, newFile.MD5, out _))
                {
                    failed++;
                    if (File.Exists(tmp)) File.Delete(tmp);
                    continue;
                }

                File.Move(tmp, patchPath, overwrite: true);
                patchBytes += new FileInfo(patchPath).Length;
                created++;
            }
        }

        return new PatchGenerationSummary(baselines.Count, created, skipped, failed, copied, patchBytes);
    }

    /// <summary>校验补丁确实能把旧文件还原成目标内容（AGENT.md §5.3）。</summary>
    private static bool VerifyPatch(string tools, string oldPath, string patchPath, string expectedMd5, out string error)
    {
        var outFile = patchPath + ".verify";
        try
        {
            if (!HdiffTool.Apply(tools, oldPath, patchPath, outFile, out error)) return false;

            using var s = new FileStream(outFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var md5 = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(s));
            if (string.Equals(md5, expectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                error = string.Empty;
                return true;
            }

            error = $"校验失败：期望 {expectedMd5} 实得 {md5}";
            return false;
        }
        finally
        {
            if (File.Exists(outFile)) File.Delete(outFile);
        }
    }

    private static bool TryPath(string root, ManifestFile file, out string full)
    {
        full = Path.Combine(root, file.RelativePath().Replace('/', Path.DirectorySeparatorChar));
        return true;
    }
}
