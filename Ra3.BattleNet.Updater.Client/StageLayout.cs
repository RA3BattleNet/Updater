using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ra3.BattleNet.Updater.Client;

/// <summary>落地模式（AGENT.md §12.1）。</summary>
public enum ApplyMode
{
    /// <summary>直接更新：会话内就地替换，全部成功即生效（默认，行为与历史一致）。</summary>
    InPlace,

    /// <summary>暂存更新：只把内容暂存到 <c>UpdaterStage</c>，由独立 applier 在宿主退出后落地。</summary>
    Staged,
}

/// <summary>待提交计划里的动作类型（AGENT.md §12.4）。</summary>
public enum StagedActionKind
{
    /// <summary>把暂存内容放到目标路径。**新增还是替换由 applier 按磁盘状态自行判定**（崩溃恢复本来也要判）。</summary>
    Place,

    /// <summary>树内改名：<see cref="StagedAction.MoveFromRelative"/> → <see cref="StagedAction.RelativePath"/>，0 下载。</summary>
    Move,
}

/// <summary>计划里的一个动作。</summary>
public sealed record StagedAction(
    string RelativePath,
    string Md5,
    StagedActionKind Kind,
    string? MoveFromRelative = null);

/// <summary>
/// 待提交计划。**它只是提示，不是决策依据**（AGENT.md §12.4）：
/// applier 的判断必须来自「远端 manifest + 本地 manifest + 磁盘真实哈希」，
/// 本文件被删/被改/损坏，最坏只应导致"重下一遍"，**不得**导致错误落地。
/// </summary>
public sealed record StagedPlan(string ManifestHash, IReadOnlyList<StagedAction> Actions);

/// <summary>
/// 暂存区布局（AGENT.md §12.3）：
/// <code>
/// UpdaterStage/new/&lt;相对路径&gt;   阶段一落的暂存内容（与目标同卷 ⇒ 提交是纯改名）
/// UpdaterStage/old/&lt;相对路径&gt;   提交时旧文件搬到这里（回退材料）
/// UpdaterStage/plan.json        待提交计划（提示文件，见上）
/// </code>
/// 【必须】它必须在安装根目录下：程序目录与系统临时目录很可能不同卷，
/// 跨卷"改名"会退化成复制（慢，且失去原子性）。
/// </summary>
public static class StageLayout
{
    public const string DirName = "UpdaterStage";
    public const string NewDirName = "new";
    public const string OldDirName = "old";
    public const string PlanFileName = "plan.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Root(string installRoot) => Path.Combine(installRoot, DirName);
    public static string NewRoot(string installRoot) => Path.Combine(Root(installRoot), NewDirName);
    public static string OldRoot(string installRoot) => Path.Combine(Root(installRoot), OldDirName);
    public static string PlanPath(string installRoot) => Path.Combine(Root(installRoot), PlanFileName);

    /// <summary>相对路径 → 暂存内容路径（updater 自己的目录，走长路径安全拼接）。</summary>
    public static string NewPath(string installRoot, string relative) =>
        Fs.P(Path.Combine(NewRoot(installRoot), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>相对路径 → 备份路径（提交阶段把旧文件搬到这里）。</summary>
    public static string OldPath(string installRoot, string relative) =>
        Fs.P(Path.Combine(OldRoot(installRoot), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>是否存在待提交计划 —— 存在即表示"有已暂存但未落地的更新"（§12.7 的互斥判据）。</summary>
    public static bool HasPendingPlan(string installRoot) => Fs.Exists(PlanPath(installRoot));

    /// <summary>
    /// 写计划。**只在阶段一全部成功之后调用**：计划一旦存在，就代表"可以提交"，
    /// 半成品写进去会让 applier 落出一个新旧混合的树。
    /// </summary>
    public static void SavePlan(string installRoot, StagedPlan plan) =>
        Fs.WriteAllText(PlanPath(installRoot), JsonSerializer.Serialize(plan, JsonOptions));

    /// <summary>读计划。**容错**：坏文件/缺字段一律当作"没有计划"（它只是提示）。</summary>
    public static StagedPlan? LoadPlan(string installRoot)
    {
        try
        {
            var path = PlanPath(installRoot);
            if (!Fs.Exists(path)) return null;
            var plan = JsonSerializer.Deserialize<StagedPlan>(Fs.ReadAllText(path), JsonOptions);
            return plan is { Actions: not null } ? plan : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>删掉暂存内容（落地成功、内容已被消费完之后）。</summary>
    public static void ClearNew(string installRoot) => DeleteTree(NewRoot(installRoot));

    /// <summary>删掉备份（回退窗口止于"下一次落地开始"，§12.7）。</summary>
    public static void ClearOld(string installRoot) => DeleteTree(OldRoot(installRoot));

    /// <summary>删掉待提交计划（落地成功、本地清单已推进之后）。</summary>
    public static void DeletePlan(string installRoot) => Fs.Delete(PlanPath(installRoot));

    /// <summary>清空整个暂存区（换目标版本、或宿主显式放弃待提交更新时用）。</summary>
    public static void Reset(string installRoot)
    {
        ClearNew(installRoot);
        ClearOld(installRoot);
        DeletePlan(installRoot);
    }

    /// <summary>删掉 <c>new/</c> 里不在计划内的残留（上次半途失败留下的、已不再需要的暂存内容）。</summary>
    public static void PruneNewExcept(string installRoot, IEnumerable<string> keepRelativePaths)
    {
        try
        {
            var root = NewRoot(installRoot);
            if (!Directory.Exists(Fs.P(root))) return;
            var keep = new HashSet<string>(keepRelativePaths.Select(Normalize), StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(Fs.P(root), "*", SearchOption.AllDirectories))
            {
                var rel = Normalize(Path.GetRelativePath(root, file));
                if (!keep.Contains(rel)) Fs.Delete(file);
            }

            // 再收掉空目录（自底向上）
            foreach (var dir in Directory.EnumerateDirectories(Fs.P(root), "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                try { Directory.Delete(dir); } catch { /* 尽力 */ }
            }
        }
        catch
        {
            // 清理失败不影响正确性：残留的暂存文件最坏只是占点空间
        }
    }

    private static string Normalize(string relative) => relative.Replace('\\', '/').Trim('/');

    private static void DeleteTree(string path)
    {
        try { if (Directory.Exists(Fs.P(path))) Directory.Delete(Fs.P(path), recursive: true); } catch { /* 尽力 */ }
    }
}