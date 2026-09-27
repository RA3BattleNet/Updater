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

        if (local is not null)
        {
            foreach (var f in local.Manifest.Files)
            {
                byUuid[f.UUID] = f;
                byPath[f.RelativePath()] = f;
            }
        }

        var entries = new List<PlanEntry>(remote.Manifest.Files.Count);
        var unmatched = new List<ManifestFile>();
        var predecessorPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 第一遍：能按 UUID 定位前身的（覆盖路径变化），退化时按目标路径定位。
        foreach (var target in remote.Manifest.Files)
        {
            var targetRel = target.RelativePath();
            var targetPath = Full(root, targetRel);

            if (target.Mode == FileModeEnum.Skip || IsExcluded(targetRel, excluded))
            {
                entries.Add(new PlanEntry(PlanAction.Skip, target, targetPath, null, null, null));
                continue;
            }

            ManifestFile? old = null;
            if (byUuid.TryGetValue(target.UUID, out var byId)) old = byId;
            else if (byPath.TryGetValue(targetRel, out var byRel)) old = byRel;

            if (old is null)
            {
                unmatched.Add(target);
                continue;
            }

            var oldRel = old.RelativePath();
            predecessorPaths.Add(oldRel);
            var oldPath = Full(root, oldRel);

            if (string.Equals(old.MD5, target.MD5, StringComparison.OrdinalIgnoreCase))
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
        }

        // 第二遍：本地没有对应条目，但"本地已经有同样内容" → 0 下载（§4.2 的内容索引）。
        // 内容索引只允许从**这一版已经不再需要**的本地文件里取，而且取走一个就用掉一个：
        //   1. 远端这一版还要的路径不能当源头 —— 否则会把 A 挪去满足 B，而 A 自己还得留着，
        //      结果是更新报成功、安装目录少一个文件。实测 v4→v5：未变的 MapMixer.config 与
        //      新增的 MapMixer.exe.config 内容相同，旧实现把前者挪走，静默少了一个文件。
        //   2. 已经被当成补丁前身的路径也不能当源头 —— 否则那个补丁找不到前身。
        //      这也顺带消掉了"先搬前身、后打补丁"的顺序敏感问题。
        //   3. 一个本地文件只能满足一个目标（一个文件只可能被移动一次）。
        var remotePaths = new HashSet<string>(
            remote.Manifest.Files.Select(f => f.RelativePath()), StringComparer.OrdinalIgnoreCase);

        var freeContent = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        if (local is not null)
        {
            foreach (var f in local.Manifest.Files)
            {
                var rel = f.RelativePath();
                if (remotePaths.Contains(rel) || predecessorPaths.Contains(rel)) continue;
                freeContent.TryAdd(f.MD5, f);
            }
        }

        foreach (var target in unmatched)
        {
            var targetPath = Full(root, target.RelativePath());
            if (freeContent.Remove(target.MD5, out var same))
                entries.Add(new PlanEntry(PlanAction.Move, target, targetPath, same, Full(root, same.RelativePath()), same.MD5));
            else
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
