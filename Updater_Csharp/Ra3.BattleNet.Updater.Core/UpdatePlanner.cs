using Ra3.BattleNet.Updater.Share.Models;

namespace Ra3.BattleNet.Updater.Core;

public enum PlanAction
{
    /// <summary>已是最新，不动。</summary>
    Skip,

    /// <summary>纯改名 / 移动（内容未变），0 下载。</summary>
    Move,

    /// <summary>先试补丁；补丁不可用则回落 Full。</summary>
    Patch,

    /// <summary>下载完整文件。</summary>
    Full,
}

public sealed record PlanEntry(
    PlanAction Action,
    ManifestFile Target,
    string TargetPath,
    ManifestFile? Predecessor,
    string? PredecessorPath,
    string? PredecessorHash);

public sealed class UpdatePlan
{
    public required IReadOnlyList<PlanEntry> Entries { get; init; }

    public int Total => Entries.Count;

    /// <summary>需要处理的文件数（进度条分母，AGENT.md §4.7）。</summary>
    public int ToProcess => Entries.Count(e => e.Action != PlanAction.Skip);

    /// <summary>需要真正传输内容的文件数（完整包判据用它，AGENT.md §4.4）。</summary>
    public int ToDownload => Entries.Count(e => e.Action is PlanAction.Patch or PlanAction.Full);

    public int Unchanged => Entries.Count(e => e.Action == PlanAction.Skip);
}

/// <summary>
/// 计划生成：**只依赖两份 manifest**，不读盘、不联网，一遍 O(n)（AGENT.md §4.2）。
/// </summary>
public static class UpdatePlanner
{
    public static UpdatePlan Build(ManifestModel remote, ManifestModel? local, UpdateConfig cfg)
    {
        var root = Path.GetFullPath(cfg.RootPath);
        var excluded = new HashSet<string>(cfg.ExcludedDirs.Select(Normalize), StringComparer.OrdinalIgnoreCase);

        var byUuid = new Dictionary<Guid, ManifestFile>();
        var byPath = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        var byContent = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);

        if (local is not null)
        {
            foreach (var f in local.Manifest.Files)
            {
                byUuid[f.UUID] = f;
                byPath[f.RelativePath()] = f;
                byContent.TryAdd(f.MD5, f);
            }
        }

        var entries = new List<PlanEntry>(remote.Manifest.Files.Count);

        foreach (var target in remote.Manifest.Files)
        {
            var targetRel = target.RelativePath();
            var targetPath = Full(root, targetRel);

            if (target.Mode == FileModeEnum.Skip || IsExcluded(targetRel, excluded))
            {
                entries.Add(new PlanEntry(PlanAction.Skip, target, targetPath, null, null, null));
                continue;
            }

            // 1) 按 UUID 定位前身（覆盖路径变化），退化时按目标路径定位
            ManifestFile? old = null;
            if (byUuid.TryGetValue(target.UUID, out var byId)) old = byId;
            else if (byPath.TryGetValue(targetRel, out var byRel)) old = byRel;

            if (old is not null)
            {
                var oldRel = old.RelativePath();
                var oldPath = Full(root, oldRel);
                var sameContent = string.Equals(old.MD5, target.MD5, StringComparison.OrdinalIgnoreCase);

                if (sameContent)
                {
                    var action = string.Equals(oldRel, targetRel, StringComparison.OrdinalIgnoreCase)
                        ? PlanAction.Skip
                        : PlanAction.Move;
                    entries.Add(new PlanEntry(action, target, targetPath, old, oldPath, old.MD5));
                }
                else
                {
                    entries.Add(new PlanEntry(PlanAction.Patch, target, targetPath, old, oldPath, old.MD5));
                }

                continue;
            }

            // 2) 本地没有对应条目：目标内容我已经有（纯改名 / 移动）
            if (byContent.TryGetValue(target.MD5, out var same)
                && !string.Equals(same.RelativePath(), targetRel, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(new PlanEntry(PlanAction.Move, target, targetPath, same, Full(root, same.RelativePath()), same.MD5));
                continue;
            }

            // 3) 只能完整下载
            entries.Add(new PlanEntry(PlanAction.Full, target, targetPath, null, null, null));
        }

        return new UpdatePlan { Entries = entries };
    }

    public static string Full(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static bool IsExcluded(string relative, HashSet<string> excluded)
    {
        if (excluded.Count == 0) return false;
        var slash = relative.IndexOf('/');
        if (slash <= 0) return false;
        return excluded.Contains(relative[..slash]);
    }

    private static string Normalize(string s) => s.Replace('\\', '/').Trim('/');
}
