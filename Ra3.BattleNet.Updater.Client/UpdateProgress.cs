namespace Ra3.BattleNet.Updater.Client;

/// <summary>
/// 进度上报：分母是**待更新文件数**（AGENT.md §4.7）。
///
/// 【必须】这里只给**事实字段**，不给 UI 语义 —— 没有百分比、没有"进度条"这种东西。
/// 宿主拿这些事实自己算进度条、判超时、写它自己的日志，或者干脆不用，都是它的事。
/// 【必须】字段只增不改（新增一律放末尾并带默认值），列语义与 <see cref="UpdateLog"/> 的契约同理。
/// </summary>
/// <param name="Current">本阶段的计数：已完成数（`wait` 阶段不用它，见 <paramref name="WaitElapsed"/>）。</param>
/// <param name="Total">本阶段的总数（`wait` 阶段不用它，见 <paramref name="WaitTimeout"/>）。</param>
/// <param name="FileName">当前文件名（没有具体文件时为空）。</param>
/// <param name="Stage">阶段标识，取值见 <see cref="UpdateStage"/>。</param>
/// <param name="Action">该文件被判的**动作**：`skip` / `move` / `patch` / `full`（没有具体文件时为空）。</param>
/// <param name="WaitElapsed">仅 `wait` 阶段：已经等了多久。</param>
/// <param name="WaitTimeout">仅 `wait` 阶段：时限（`ApplierQuiescenceTimeout`）。</param>
public sealed record UpdateProgress(
    int Current,
    int Total,
    string FileName,
    string Stage,
    string Action = "",
    TimeSpan? WaitElapsed = null,
    TimeSpan? WaitTimeout = null);

/// <summary>
/// 进度里的**阶段标识**（AGENT.md §4.7）。
///
/// 【必须】这个字段只放阶段；"这个文件被判成什么动作"走 <see cref="UpdateProgress.Action"/>。
/// 历史实现把动作名（`skip`/`move`/`patch`/`full`）塞进这个字段，宿主想分辨"在核对还是在提交"
/// 只能靠猜 —— 事实不完整就是 bug。
/// </summary>
public static class UpdateStage
{
    /// <summary>核对阶段：开工探测；以及"没有可信基线时按磁盘哈希核对"（§4.10）。</summary>
    public const string Check = "check";

    /// <summary>树内纯改名（阶段一逐文件）。</summary>
    public const string Move = "move";

    /// <summary>打补丁（阶段一逐文件）。</summary>
    public const string Patch = "patch";

    /// <summary>完整下载（阶段一逐文件）。</summary>
    public const string Download = "download";

    /// <summary>
    /// 等"树静默"：宿主进程退出 + 树内没有进程在跑（**仅 applier**，§12.5）。
    /// 这一段可能很长（`ApplierQuiescenceTimeout`），所以它必须是一个**可分辨的阶段**，
    /// 并带上"已等多久 / 时限"（<see cref="UpdateProgress.WaitElapsed"/> / <see cref="UpdateProgress.WaitTimeout"/>）。
    /// </summary>
    public const string Wait = "wait";

    /// <summary>暂存更新的落地阶段：逐个提交（**仅 applier**，§12.5）。</summary>
    public const string Apply = "apply";

    /// <summary>本轮收尾（两边都报一次）：此后这一轮不再有进度。</summary>
    public const string Done = "done";
}