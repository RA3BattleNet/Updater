using Ra3.BattleNet.Updater.Share.Models;

namespace Ra3.BattleNet.Updater.Client;

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
    /// <summary>
    /// 生成计划。
    /// <paramref name="patchAvailable"/> = 本机的补丁应用工具（hpatchz）**能不能用**。
    /// 为 <c>false</c> 时所有本会走补丁的条目**在计划阶段就降级为完整下载** —— 这样不会出现
    /// "补丁下下来才发现打不上、删掉再下完整文件"（那比纯完整下载还费流量）。缺工具的归因由
    /// 调用方写成 <c>patch_tool_missing</c>（见 <c>Updater.ProcessEntryAsync</c>）。
    /// </summary>
    public static UpdatePlan Build(ManifestModel remote, ManifestModel? local, UpdateConfig cfg,
        bool patchAvailable = true)
    {
        var root = Path.GetFullPath(cfg.RootPath);
        var excluded = Excluded(cfg);

        var byUuid = new Dictionary<Guid, ManifestFile>();

        if (local is not null)
        {
            foreach (var f in local.Manifest.Files)
                byUuid[f.UUID] = f;
        }

        var entries = new List<PlanEntry>(remote.Manifest.Files.Count);
        var unmatched = new List<ManifestFile>();
        var predecessorPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 第一遍：**只按 UUID 认身份**（AGENT.md §3.4）。
        foreach (var target in remote.Manifest.Files)
        {
            var targetRel = target.RelativePath();
            var targetPath = Full(root, targetRel);

            if (target.Mode == FileModeEnum.Skip || IsExcluded(targetRel, excluded))
            {
                entries.Add(new PlanEntry(PlanAction.Skip, target, targetPath, null, null, null));
                continue;
            }

            // 本地没有同 UUID 的条目 → 就是"新文件"，不去按路径猜前身。
            // 【已决策 2026-09-28】以前这里会退化成"按目标路径找前身"：那等于**客户端替服务端的失误补正**
            // （同路径换了 UUID 是发布侧该维护好的事），而且猜错的代价是"两个不同文件被接在一起"。
            // 服务端已保证"同路径 ⇒ 同 UUID"（生成器用 路径派生 UUID + 同路径继承），所以正常流程下
            // 删掉这条路没有任何行为变化（见 UpdatePlannerTests.DifferentUuid_IsANewFile_...）。
            var old = byUuid.TryGetValue(target.UUID, out var byId) ? byId : null;
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
                // 没有补丁工具 → 计划阶段就降级成 full（保留前身信息，便于调用方写出归因 reason）
                entries.Add(new PlanEntry(patchAvailable ? PlanAction.Patch : PlanAction.Full,
                    target, targetPath, old, oldPath, old.MD5));
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

    /// <summary>
    /// 远端清单里**真正受管**的条目（`Mode != Skip` 且不在排除目录里，§4.2）—— 也就是规划器会
    /// 逐个判定的那一批。
    ///
    /// 「没有可信基线时按磁盘哈希合成基线」（B-2 / §4.10）必须用**同一个**口径去挑候选：它若自己去
    /// 哈希一批规划器根本不看的文件，最轻是白读一遍树，最重是把"本该照常更新"的文件判成"磁盘上已有"。
    /// 所以这里只留一份定义，两边共用。
    /// </summary>
    internal static List<ManifestFile> ManagedTargets(ManifestModel remote, UpdateConfig cfg)
    {
        var excluded = Excluded(cfg);
        return remote.Manifest.Files
            .Where(f => f.Mode != FileModeEnum.Skip && !IsExcluded(f.RelativePath(), excluded))
            .ToList();
    }

    /// <summary>不受管目录（顶层目录名，大小写不敏感）+ 库自己的工作目录（§12.3）。</summary>
    private static HashSet<string> Excluded(UpdateConfig cfg)
    {
        var excluded = new HashSet<string>(cfg.ExcludedDirs.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        // 暂存区是库自己的工作目录：即便某个 manifest 误列了它，也绝不受管。
        excluded.Add(Normalize(StageLayout.DirName));
        return excluded;
    }

    private static bool IsExcluded(string relative, HashSet<string> excluded)
    {
        if (excluded.Count == 0) return false;
        var slash = relative.IndexOf('/');
        if (slash <= 0) return false;
        return excluded.Contains(relative[..slash]);
    }

    private static string Normalize(string s) => s.Replace('\\', '/').Trim('/');
}
