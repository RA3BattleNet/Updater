namespace Ra3.BattleNet.Updater.Client;

/// <summary>更新会话的结果类型（AGENT.md §4.12：宿主只需一个 switch）。</summary>
public enum UpdateOutcome
{
    /// <summary>已是最新，未做任何事。</summary>
    UpToDate,

    /// <summary>更新成功，本地 manifest 已更新。</summary>
    Updated,

    /// <summary>
    /// **正常分支**：本库交回宿主处理本次更新（本地状态不可信 / 达到资源保护阈值等）。
    /// 宿主自行决定怎么办 —— 通常是去拿整体安装包（BT / 直链），但那**不是本库的职责**，
    /// 本库协议里的「完整」只指 <c>files/{md5}.bin</c> 这种单个完整文件。
    /// </summary>
    NeedsHostFallback,

    /// <summary>
    /// 暂存模式阶段一成功：内容已全部暂存就绪，**但还没落地**（AGENT.md §12.7）。
    /// 宿主应当提示用户退出/重启，届时 applier 才把它换上去。
    /// </summary>
    Staged,

    /// <summary>**意外**：网络 / 磁盘 / 权限等导致的失败。</summary>
    Failed,
}

/// <summary>结构化结果。库绝不抛异常、绝不弹 UI、绝不退出进程。</summary>
public sealed record UpdateResult(
    UpdateOutcome Outcome,
    string Reason,
    int Total,
    int Skipped,
    int Moved,
    int Patched,
    int Full,
    int FailedCount,
    long BytesDownloaded,
    TimeSpan Elapsed,
    string Detail = "",
    string HttpVersion = "",
    long PayloadBytes = 0,
    long WireBytes = 0,
    long WireSentBytes = 0,
    long WireReceivedBytes = 0)
{
    /// <summary>
    /// 宿主是否可以跳过自己原有的更新逻辑。
    /// 注意 <see cref="UpdateOutcome.Staged"/> 也算：库这边该做的都做完了（内容已就绪、会在宿主退出后落地），
    /// 宿主**不该**因此回退到自己的整包更新流程。要区别对待的话看 <see cref="PendingRestart"/>。
    /// </summary>
    public bool Applied => Outcome is UpdateOutcome.UpToDate or UpdateOutcome.Updated or UpdateOutcome.Staged;

    /// <summary>已就绪但尚未生效，需要宿主退出/重启后才落地（暂存模式）。</summary>
    public bool PendingRestart => Outcome is UpdateOutcome.Staged;

    public override string ToString() =>
        $"{Outcome} reason={Reason} total={Total} skip={Skipped} move={Moved} patch={Patched} " +
        $"full={Full} fail={FailedCount} bytes={BytesDownloaded} payload={PayloadBytes} wire={WireBytes} " +
        $"ms={(long)Elapsed.TotalMilliseconds}" +
        (HttpVersion.Length > 0 ? $" http={HttpVersion}" : string.Empty);
}

/// <summary>原因码（写进日志的 reason 列，稳定枚举，只增不改）。</summary>
public static class UpdateReasons
{
    public const string None = "";
    public const string WorkloadTooLarge = "policy";
    public const string NoLocalManifest = "no_local_manifest";

    /// <summary>本地没有可用的前身。</summary>
    public const string NoLocal = "no_local";

    /// <summary>服务端没有该内容对的补丁（404）。</summary>
    public const string NoPatch = "no_patch";

    /// <summary>
    /// 本机的**补丁应用工具（hpatchz）不可用**，所以本轮在**计划阶段**就把补丁降级成了完整下载
    /// （区别于 <see cref="PatchFailed"/>：那是"补丁已经下下来了、打不上"）。
    /// 典型场景：32 位宿主（RID = win-x86）而随包工具只覆盖 x64；或工具目录配错。
    /// 有它才能把"服务端没做补丁"与"本机没工具"在日志里分开。
    /// </summary>
    public const string PatchToolMissing = "patch_tool_missing";

    /// <summary>补丁下载 / 应用 / 校验失败。</summary>
    public const string PatchFailed = "patch_failed";

    public const string DownloadFailed = "download_failed";
    public const string VerifyFailed = "verify_failed";
    public const string FileInUse = "file_in_use";
    public const string IoError = "io_error";
    public const string ManifestUnavailable = "manifest_unavailable";
    public const string LocalCorrupt = "local_corrupt";

    /// <summary>已有另一个更新实例在运行（AGENT.md §4.9）。</summary>
    public const string AlreadyRunning = "already_running";

    /// <summary>
    /// 本机上存在**已暂存但未落地**的更新，而本次要求走直接更新模式（AGENT.md §12.7）。
    /// 必须拒绝：否则随后运行的 applier 会拿旧计划覆盖刚由直接模式换好的新文件。
    /// </summary>
    public const string PendingStagedApply = "pending_staged_apply";
}
