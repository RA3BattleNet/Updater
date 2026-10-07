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

    /// <summary>
    /// **删除集**：本地清单登记过、这一版却从远端清单消失的条目（见 `DELETE_DESIGN.md`）。
    /// 【必须】它与 <see cref="Entries"/> 分开计数：`Total` 的语义是"远端条目数"（进度分母、
    /// 失败容忍度基数），把删除混进去会破坏 `Total == Skip + Move + Patch + Full` 这条不变量。
    /// </summary>
    public IReadOnlyList<ManifestFile> Deletions { get; init; } = [];

    public int Deleted => Deletions.Count;

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

        var moveSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in unmatched)
        {
            var targetPath = Full(root, target.RelativePath());
            if (freeContent.Remove(target.MD5, out var same))
            {
                moveSources.Add(same.RelativePath());   // 已被改名到别处：不能同时又当删除对象
                entries.Add(new PlanEntry(PlanAction.Move, target, targetPath, same, Full(root, same.RelativePath()), same.MD5));
            }
            else
                entries.Add(new PlanEntry(PlanAction.Full, target, targetPath, null, null, null));
        }

        return new UpdatePlan
        {
            Entries = entries,
            Deletions = DeletedFrom(remote, local, cfg.ExcludedDirs, moveSources),
        };
    }

    /// <summary>
    /// **删除集**（见 `DELETE_DESIGN.md`）：本地清单里登记过、这一版却从远端清单**消失**的受管条目。
    ///
    /// 为什么不是"按远端清单对整棵树做反向布尔删除"：安装根里必然有客户端自己生成的非追踪文件
    /// （日志、设置、缓存…），反向布尔要求把它们逐个列白名单，漏一个就是删用户文件。
    /// 差集法只动"我们自己登记过的东西"。
    ///
    /// 【必须】路径比较**大小写不敏感**：Windows 上 `data/Ra3BattleNet/…` 与 `data/Ra3battlenet/…`
    /// 是同一个文件（LiveContent 线 v2→v3 实测有 21 对这样的改名）。按序数比较会把它判成"消失"从而误删。
    /// 【必须】三条排除：`Mode=Skip` 的本地条目（清单登记但不受管）、排除目录下的路径、
    /// 以及已经被 `Move` 消费掉的前身（它已被改名到别处）。
    /// 【必须】只从**真实的本地清单**算：合成基线（§4.10）里只有远端命中的条目 ⇒ 差集恒为空；
    /// `local` 为 null（没有可信基线）时直接不删 —— 这是"不删"的最强保证，不依赖巧合。
    ///
    /// 阶段一与 applier 共用这一份实现（两处各写一遍必然漂移）。
    /// </summary>
    public static IReadOnlyList<ManifestFile> DeletedFrom(
        ManifestModel remote, ManifestModel? local, IReadOnlyList<string>? excludedDirs,
        IReadOnlyCollection<string>? moveSources = null)
    {
        if (local is null) return [];

        var excluded = ExcludedNames(excludedDirs);
        var remotePaths = new HashSet<string>(
            remote.Manifest.Files.Select(f => f.RelativePath()), StringComparer.OrdinalIgnoreCase);

        var result = new List<ManifestFile>();
        foreach (var f in local.Manifest.Files)
        {
            if (f.Mode == FileModeEnum.Skip) continue;
            var rel = f.RelativePath();
            if (IsExcluded(rel, excluded)) continue;
            if (remotePaths.Contains(rel)) continue;
            if (moveSources is not null && moveSources.Contains(rel)) continue;
            result.Add(f);
        }

        return result;
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
    private static HashSet<string> Excluded(UpdateConfig cfg) => ExcludedNames(cfg.ExcludedDirs);

    /// <summary>
    /// 排除目录名集合。**始终**把暂存区算进去 —— 它是库自己的工作目录：即便某个 manifest 误列了它、
    /// 或它某天出现在本地清单里，也绝不受管、**绝不删除**（§12.3）。
    /// 阶段一与 applier 共用这一份：applier 的排除清单来自 `ApplierConfig.ExcludedDirs`（命令行传入）。
    /// </summary>
    internal static HashSet<string> ExcludedNames(IReadOnlyList<string>? dirs)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (dirs is not null)
            foreach (var d in dirs) excluded.Add(Normalize(d));
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
