namespace Ra3.BattleNet.Updater.Client;

/// <summary>暂存更新（AGENT.md §12）里，某个文件在提交阶段的磁盘状态。</summary>
public enum StageFileState
{
    /// <summary>目标已是新内容（磁盘 hash == 远端 manifest 的 MD5）。</summary>
    Applied,

    /// <summary>目标还是旧内容、或目标尚不存在（新增条目），而暂存内容在 —— 可以正常提交。</summary>
    Ready,

    /// <summary>目标缺失、暂存内容在、备份也在 —— 崩在两次改名之间。最多只会有一个文件处于这个状态。</summary>
    Interrupted,

    /// <summary>目标缺失、暂存内容没了、备份在 —— 新内容丢了，只能回退。</summary>
    Rollback,

    /// <summary>目标还是旧内容、暂存内容没了（两者都不在也一样）—— 需要重新下载。</summary>
    NeedDownload,
}

/// <summary>状态对应的动作。</summary>
public enum StageFileAction
{
    /// <summary>已经是最新，跳过。</summary>
    Skip,

    /// <summary>备份旧文件 → 暂存内容就位。</summary>
    Commit,

    /// <summary>暂存内容就位（目标已缺失，不必再备份）。</summary>
    ResumeCommit,

    /// <summary>备份改名回目标（回退）。</summary>
    RestoreBackup,

    /// <summary>重新下载后再按 Commit 走。</summary>
    Refetch,
}

/// <summary>
/// 暂存更新的状态判定与提交顺序：**纯函数、无 IO、无副作用**（AGENT.md §12.5 / §12.6）。
///
/// 单独抽出来的理由：整条落地链路里，只有"状态判定表"是纯靠推理就能验错的。
/// 它有 2^4 = 16 种输入，靠人眼审查必然漏；先把它钉住，实现阶段就不可能把某一格写错。
///
/// 可靠性前提：传入的"事实"必须来自 §4.5 的事实 A/B/C（远端 manifest、本地 manifest、
/// 磁盘真实 hash），**不得**来自 plan.json 之类可写文件 —— 那些只允许当提示（§12.4）。
/// </summary>
public static class StageRecovery
{
    /// <summary>按 §12.6 的表判定单个文件的状态。判定顺序即优先级。</summary>
    /// <param name="targetExists">目标文件存在。</param>
    /// <param name="targetIsNew">目标的 MD5 等于远端 manifest 里的 MD5。</param>
    /// <param name="stagedExists">暂存内容（<c>UpdaterStage/new/…</c>）存在。</param>
    /// <param name="backupExists">备份（<c>UpdaterStage/old/…</c>）存在。</param>
    public static StageFileState Classify(
        bool targetExists, bool targetIsNew, bool stagedExists, bool backupExists)
    {
        if (targetExists && targetIsNew) return StageFileState.Applied;
        if (!targetExists && stagedExists && backupExists) return StageFileState.Interrupted;
        if (!targetExists && !stagedExists && backupExists) return StageFileState.Rollback;
        if (targetExists && stagedExists) return StageFileState.Ready;
        if (!targetExists && stagedExists) return StageFileState.Ready;   // 新增文件：直接就位，无需备份
        return StageFileState.NeedDownload;                              // 目标是旧内容，或暂存内容已丢
    }

    /// <summary>状态 → 动作。</summary>
    public static StageFileAction Decide(StageFileState state) => state switch
    {
        StageFileState.Applied => StageFileAction.Skip,
        StageFileState.Ready => StageFileAction.Commit,
        StageFileState.Interrupted => StageFileAction.ResumeCommit,
        StageFileState.Rollback => StageFileAction.RestoreBackup,
        _ => StageFileAction.Refetch,
    };

    /// <summary>
    /// 提交顺序：入口（<c>*.exe</c>）排最后，组内保持原序（§12.5）。
    /// 理由：崩溃时"旧 exe + 新库"通常还能启动，"新 exe + 旧库"几乎必崩；
    /// 且旧 exe 还在意味着宿主仍能启动、再跑一次 updater 续做。
    /// </summary>
    public static IReadOnlyList<string> OrderForCommit(IEnumerable<string> relativePaths)
    {
        var all = relativePaths.ToList();
        return all.Where(p => !IsEntryExe(p)).Concat(all.Where(IsEntryExe)).ToList();
    }

    /// <summary>是否是入口可执行文件（按扩展名粗判，不需要宿主声明）。</summary>
    public static bool IsEntryExe(string relativePath) =>
        relativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}