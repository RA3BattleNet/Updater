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
    IReadOnlyList<RenameCandidate> SuspectRenames);

/// <summary>
/// 清单生成（AGENT.md §5.1）+ 生成期自检（§5.2）。
/// UUID 继承规则：先按 (FileName, Path) 完全相同，再按 MD5（内容未变）。
/// **路径与内容同时变化无法自动判定，需人工改 UUID**（§3.4）。
/// </summary>
public static class ManifestGenerator
{
    /// <summary>疑似改名阈值：补丁 / 新文件 小于该值即认为"很可能只是改名+改内容"。</summary>
    public const double SuspectRenameRatio = 0.5;

    private const int MaxProbePairs = 144;

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
        var suspects = ProbeSuspectRenames(added, removed, basePath, oldRoot, toolsDir);

        return new ManifestGenerationResult(manifest, added, removed, modified, moved, unchanged, suspects);
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
    /// 有界探测：消失集合 × 新增集合 各跑一次 hdiffz，补丁率低者即为"疑似改名+改内容"。
    /// 两个集合通常是个位数，秒级完成（AGENT.md §5.2）。
    /// </summary>
    private static List<RenameCandidate> ProbeSuspectRenames(
        List<ManifestFile> added, List<ManifestFile> removed, string newRoot, string? oldRoot, string? toolsDir)
    {
        var result = new List<RenameCandidate>();
        if (added.Count == 0 || removed.Count == 0) return result;
        if (oldRoot is null || added.Count * removed.Count > MaxProbePairs)
        {
            // 无法（或不该）探测：全部列出，ratio 为 null，由人工判断
            foreach (var nf in added)
                foreach (var of in removed)
                    result.Add(new RenameCandidate(nf, of, null));
            return result;
        }

        var tmp = Path.Combine(Path.GetTempPath(), "updater-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var nf in added)
            {
                foreach (var of in removed)
                {
                    var ratio = ComputeRatio(of, nf, oldRoot, newRoot, tmp, toolsDir);
                    if (ratio is null || ratio < SuspectRenameRatio)
                        result.Add(new RenameCandidate(nf, of, ratio));
                }
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* 尽力清理 */ }
        }

        return result.OrderBy(c => c.PatchRatio ?? double.MaxValue).ToList();
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
                sb.AppendLine("    (未提供 --old-root，无法计算补丁率，仅列出候选)");

            foreach (var c in r.SuspectRenames)
            {
                var pct = c.PatchRatio is null ? "?" : $"{c.PatchRatio:P0}";
                sb.AppendLine($"    ? 新增:     /{c.Added.RelativePath()}   hash {c.Added.MD5}");
                sb.AppendLine($"      疑似前身: /{c.Removed.RelativePath()}  hash {c.Removed.MD5}   (补丁率 {pct})");
                sb.AppendLine($"      ➜ 操作: 把新增条目的 <UUID> 改成 {c.Removed.UUID:N}");
            }

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
