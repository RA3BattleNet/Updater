using System.Security.Cryptography;
using System.Text;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;

namespace Ra3.BattleNet.Updater.Server;

public sealed record RenameCandidate(ManifestFile Added, ManifestFile Removed, double? PatchRatio);

public sealed record ManifestGenerationResult(
    ManifestModel Manifest,
    IReadOnlyList<ManifestFile> Added,
    IReadOnlyList<ManifestFile> Removed,
    IReadOnlyList<ManifestFile> Modified,
    IReadOnlyList<ManifestFile> Moved,
    IReadOnlyList<ManifestFile> Unchanged,
    IReadOnlyList<RenameCandidate> SuspectRenames,
    int ProbeCandidates = 0,
    int ProbedPairs = 0);

/// <summary>
/// 清单生成（AGENT.md §5.1）+ 生成期自检（§5.2）。
/// UUID 继承规则：先按 (FileName, Path) 完全相同，再按 MD5（内容未变）。
/// **路径与内容同时变化无法自动判定，需人工改 UUID**（§3.4）。
/// </summary>
public static class ManifestGenerator
{
    /// <summary>疑似改名阈值：补丁 / 新文件 小于该值即认为"很可能只是改名+改内容"。</summary>
    public const double SuspectRenameRatio = 0.5;

    /// <summary>真正跑 hdiffz 的候选对数上限（§5.2：「有界」是硬要求）。</summary>
    public const int MaxProbePairs = 200;

    /// <summary>传闻尺寸预筛窗口：疑似改名的两个文件大小不会差 4 倍以上。</summary>
    public const double SizeWindowFactor = 4.0;

    /// <summary>报告里最多列出多少条疑似改名。</summary>
    public const int MaxReportedCandidates = 20;

    public static ManifestGenerationResult Generate(
        string targetDir,
        string? oldManifestPath,
        IReadOnlyList<string> excludeDirs,
        string? oldRoot = null,
        string? toolsDir = null)
    {
        var basePath = Path.GetFullPath(targetDir);
        var excluded = new HashSet<string>(
            excludeDirs.Select(d => d.Trim().TrimStart('/', '\\')),
            StringComparer.OrdinalIgnoreCase);

        var oldManifest = string.IsNullOrEmpty(oldManifestPath) ? null : new ManifestModel(oldManifestPath);
        var oldFiles = oldManifest?.Manifest.Files.ToList() ?? [];
        var consumed = new HashSet<Guid>();

        var manifest = new ManifestModel(new Version(1, 0, 0),
            $"自动生成-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");

        var added = new List<ManifestFile>();
        var modified = new List<ManifestFile>();
        var moved = new List<ManifestFile>();
        var unchanged = new List<ManifestFile>();

        foreach (var full in Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(full);
            var relativeDir = RelativeDir(basePath, Path.GetDirectoryName(full)!);

            var file = new ManifestFile(DeterministicUuid(relativeDir, fileName), fileName, Md5File(full), relativeDir, "1.0.0");

            var byPath = oldFiles.FirstOrDefault(o => o.FileName == fileName && o.Path == relativeDir);
            if (byPath is not null)
            {
                Inherit(file, byPath);
                consumed.Add(byPath.UUID);
                if (string.Equals(file.MD5, byPath.MD5, StringComparison.OrdinalIgnoreCase)) unchanged.Add(file);
                else modified.Add(file);
            }
            else
            {
                var byMd5 = oldFiles.FirstOrDefault(o => !consumed.Contains(o.UUID)
                    && string.Equals(o.MD5, file.MD5, StringComparison.OrdinalIgnoreCase));
                if (byMd5 is not null)
                {
                    Inherit(file, byMd5);
                    consumed.Add(byMd5.UUID);
                    moved.Add(file);
                }
                else
                {
                    added.Add(file);
                }
            }

            // 排除目录在继承之后强制标记，避免被旧 Mode 覆盖
            var top = relativeDir.TrimStart('\\', '/').Split('\\', '/')[0];
            if (top.Length > 0 && excluded.Contains(top))
                file.Mode = FileModeEnum.Skip;

            manifest.Manifest.Files.Add(file);
        }

        var removed = oldFiles.Where(o => !consumed.Contains(o.UUID)).ToList();
        var (suspects, candidates, probed) = ProbeSuspectRenames(added, removed, basePath, oldRoot, toolsDir);

        return new ManifestGenerationResult(
            manifest, added, removed, modified, moved, unchanged, suspects, candidates, probed);
    }

    private static void Inherit(ManifestFile target, ManifestFile source)
    {
        target.UUID = source.UUID;
        target.Version = new Version(source.Version.Major, source.Version.Minor, source.Version.Build + 1);
        target.Type = source.Type;
        target.Mode = source.Mode;
        target.KindOf = source.KindOf;
    }

    /// <summary>
    /// 有界探测：先在"消失 × 新增"的笛卡尔积上做**只看文件大小的廉价预筛**，
    /// 再对最像的至多 <see cref="MaxProbePairs"/> 对真跑 hdiffz，补丁率低者即为
    /// "疑似改名+改内容"（AGENT.md §5.2）。
    ///
    /// 这里必须**两侧都有界**，5 版本模拟里 v4→v5 就是活教材：614 新增 × 463 消失 = 28 万对。
    /// - 全跑 hdiffz = 28 万次进程调用；
    /// - 而"不跑就全列出来"更糟 —— 那是 93 MB、28 万条**每一条都在建议人工改错 UUID** 的报告，
    ///   比不说还坏。
    /// </summary>
    private static (List<RenameCandidate> Suspects, int Candidates, int Probed) ProbeSuspectRenames(
        List<ManifestFile> added, List<ManifestFile> removed, string newRoot, string? oldRoot, string? toolsDir)
    {
        if (added.Count == 0 || removed.Count == 0 || string.IsNullOrEmpty(oldRoot))
            return ([], 0, 0);

        var candidates = new List<(ManifestFile Added, ManifestFile Removed, double Score)>();
        foreach (var nf in added)
        {
            var nsize = FileLength(newRoot, nf);
            if (nsize <= 0) continue;

            foreach (var of in removed)
            {
                var osize = FileLength(oldRoot, of);
                if (osize <= 0) continue;

                double lo = Math.Min(nsize, osize), hi = Math.Max(nsize, osize);
                if (hi > lo * SizeWindowFactor) continue;
                candidates.Add((nf, of, lo / hi));
            }
        }

        if (candidates.Count == 0) return ([], 0, 0);

        // 最像的排前面（大小接近度），只探测前 MaxProbePairs 对
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        var take = Math.Min(candidates.Count, MaxProbePairs);

        var suspects = new List<RenameCandidate>();
        var tmp = Path.Combine(Path.GetTempPath(), "updater-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            for (var i = 0; i < take; i++)
            {
                var (nf, of, _) = candidates[i];
                var ratio = ComputeRatio(of, nf, oldRoot, newRoot, tmp, toolsDir);
                if (ratio is not null && ratio < SuspectRenameRatio)
                    suspects.Add(new RenameCandidate(nf, of, ratio));
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* 尽力清理 */ }
        }

        // 一个消失文件**只能**是一个新增文件的前身（UUID 在一个清单里唯一）。
        // 按补丁率从低到高贪心配对，避免同一份旧文件被建议给好几个新文件 ——
        // 那种建议会让人真的改错 UUID。
        var claimedAdded = new HashSet<Guid>();
        var claimedRemoved = new HashSet<Guid>();
        var pairing = new List<RenameCandidate>();
        foreach (var c in suspects.OrderBy(c => c.PatchRatio ?? double.MaxValue))
        {
            if (!claimedAdded.Add(c.Added.UUID)) continue;
            if (!claimedRemoved.Add(c.Removed.UUID)) continue;
            pairing.Add(c);
        }

        return (pairing, candidates.Count, take);
    }

    private static long FileLength(string root, ManifestFile file)
    {
        var p = Path.Combine(root, file.RelativePath().Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(p) ? new FileInfo(p).Length : 0;
    }

    private static double? ComputeRatio(
        ManifestFile oldFile, ManifestFile newFile, string oldRoot, string newRoot, string tmp, string? toolsDir)
    {
        var oldPath = Path.Combine(oldRoot, oldFile.RelativePath().Replace('/', Path.DirectorySeparatorChar));
        var newPath = Path.Combine(newRoot, newFile.RelativePath().Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(oldPath) || !File.Exists(newPath)) return null;

        var newSize = new FileInfo(newPath).Length;
        if (newSize == 0) return null;

        var patch = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".hdiff");
        try
        {
            if (!HdiffTool.Generate(toolsDir ?? AppContext.BaseDirectory, oldPath, newPath, patch, out _))
                return null;
            return (double)new FileInfo(patch).Length / newSize;
        }
        finally
        {
            if (File.Exists(patch)) File.Delete(patch);
        }
    }

    public static string FormatReport(ManifestGenerationResult r, string? oldManifestPath, string? oldRoot)
    {
        var oldName = string.IsNullOrEmpty(oldManifestPath) ? "(无)" : Path.GetFileName(oldManifestPath);
        var sb = new StringBuilder();
        sb.AppendLine($"=== 与上一版对比 ({oldName}) ===");
        sb.AppendLine($"  未变    {r.Unchanged.Count,6} files");
        sb.AppendLine($"  已修改  {r.Modified.Count,6} files        (UUID 延续，仅内容变动)");
        sb.AppendLine($"  已移动  {r.Moved.Count,6} files        (UUID 延续，内容未变 → 客户端 0 下载)");
        sb.AppendLine($"  新增    {r.Added.Count,6} files");
        sb.AppendLine($"  消失    {r.Removed.Count,6} files");

        if (r.Added.Count > 0 && r.Removed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  ⚠ 需要人工确认的重命名（新增 × 消失）:");

            if (oldRoot is null)
            {
                // 没有旧根目录就算不出补丁率。此时**不要**列出候选对：没有依据的笛卡尔积
                // 只是噪声（见 ProbeSuspectRenames 的注释）。
                sb.AppendLine($"    新增 {r.Added.Count} × 消失 {r.Removed.Count}；未提供 --old-root，" +
                              "无法计算补丁率。要确认改名请带 --old-root 重跑。");
                return sb.ToString();
            }

            if (r.ProbedPairs > 0 && r.ProbeCandidates > r.ProbedPairs)
                sb.AppendLine($"    候选 {r.ProbeCandidates} 对（按文件大小接近度取前 {r.ProbedPairs} 对探测；" +
                              "新增/消失规模较大时这不代表它们是改名）");

            if (r.SuspectRenames.Count == 0)
            {
                sb.AppendLine($"    未发现疑似改名（已探测 {r.ProbedPairs} 对，补丁率均 ≥ {SuspectRenameRatio:P0}）。");
                sb.AppendLine();
                sb.AppendLine("  提示: 新增/消失数量大时通常是整块内容替换（例如新打包了一整套运行时），不是改名。");
                return sb.ToString();
            }

            foreach (var c in r.SuspectRenames.Take(MaxReportedCandidates))
            {
                var pct = c.PatchRatio is null ? "?" : $"{c.PatchRatio:P0}";
                sb.AppendLine($"    ? 新增:     /{c.Added.RelativePath()}   hash {c.Added.MD5}");
                sb.AppendLine($"      疑似前身: /{c.Removed.RelativePath()}  hash {c.Removed.MD5}   (补丁率 {pct})");
                sb.AppendLine($"      ➜ 操作: 把新增条目的 <UUID> 改成 {c.Removed.UUID:N}");
            }
            if (r.SuspectRenames.Count > MaxReportedCandidates)
                sb.AppendLine($"    …另有 {r.SuspectRenames.Count - MaxReportedCandidates} 条疑似未列出（补丁率最低的已列在前面）");

            sb.AppendLine();
            sb.AppendLine("  提示: 漏改的重命名会让该文件从「增量」退化为「全量」，且不会报错。");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 新文件的 UUID 由**相对路径**确定性派生（与内容无关）：
    /// 同一份目录树重复生成清单必须得到同样的 UUID（AGENT.md §5.1 可复现）。
    /// 内容变化不影响身份；改名靠继承（先按 (FileName,Path)，再按 MD5）保留旧 UUID。
    /// </summary>
    private static Guid DeterministicUuid(string relativeDir, string fileName)
    {
        var rel = (relativeDir.TrimStart('\\', '/').Replace('\\', '/') + "/" + fileName).ToLowerInvariant();
        return new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(rel)));
    }

    private static string Md5File(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(MD5.HashData(s));
    }

    private static string RelativeDir(string basePath, string dir)
    {
        var b = basePath.TrimEnd(Path.DirectorySeparatorChar, '/');
        var d = dir.TrimEnd(Path.DirectorySeparatorChar, '/');
        if (string.Equals(b, d, StringComparison.OrdinalIgnoreCase)) return "\\";
        return "\\" + Path.GetRelativePath(b, d).Replace('/', '\\') + "\\";
    }
}
