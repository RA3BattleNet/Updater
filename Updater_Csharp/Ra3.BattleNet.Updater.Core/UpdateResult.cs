namespace Ra3.BattleNet.Updater.Core;

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
    /// 本库协议里的「完整」只指 <c>files/{md5}</c> 这种单个完整文件。
    /// </summary>
    NeedsHostFallback,

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
    string Detail = "")
{
    /// <summary>宿主是否可以跳过自己原有的更新逻辑。</summary>
    public bool Applied => Outcome is UpdateOutcome.UpToDate or UpdateOutcome.Updated;

    public override string ToString() =>
        $"{Outcome} reason={Reason} total={Total} skip={Skipped} move={Moved} patch={Patched} " +
        $"full={Full} fail={FailedCount} bytes={BytesDownloaded} ms={(long)Elapsed.TotalMilliseconds}";
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
}
