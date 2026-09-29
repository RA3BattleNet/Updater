using System.Security.Cryptography;
using System.Text;

namespace Ra3.BattleNet.Updater.Client;

/// <summary>
/// 更新会话配置。产品差异**只能**通过这里表达（AGENT.md §6.3）。
/// </summary>
public sealed record UpdateConfig
{
    /// <summary>安装根目录。</summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// applier 要等的宿主进程号（null = 不等待特定进程）。由 CLI 的 --wait-for-pid 设置；
    /// 与"树内进程名扫描"一起构成静默判据（AGENT.md §12.5）。
    /// </summary>
    public int? WaitForProcessId { get; init; }

    /// <summary>
    /// 宿主进程名（不含 .exe）。与 <see cref="WaitForProcessId"/> 一起校验：
    /// **PID 会被系统复用**，光看 PID 会把"抢到同一个 PID 的无关进程"当成宿主还在，白等到超时。
    /// 由 <c>StagedApplier.BuildApplyCommand</c> 自动填 —— 宿主不用管。
    /// </summary>
    public string? WaitForProcessName { get; init; }

    /// <summary>
    /// 宿主进程的启动时刻（<c>Process.StartTime.Ticks</c>）。它才是**唯一**的实例标识：
    /// 同 PID + 同名字仍可能是"用户又启动了一次同名程序"（例如又双击了一次启动器），
    /// 启动时刻对不上就一定不是同一个进程。同样由 BuildApplyCommand 自动填。
    /// </summary>
    public long? WaitForProcessStartTicks { get; init; }

    /// <summary>
    /// 落地**成功之后**把宿主拉起来（§12.5）。默认关 —— 这是行为改变，必须由宿主显式开。
    /// 用 <c>StagedApplier.BuildApplyCommand</c> 时，只要把这个开关打开，
    /// 宿主自己的可执行文件与**原始参数原文**会被自动填好，宿主侧不需要别的代码。
    /// </summary>
    public bool RestartAfterApply { get; init; }

    /// <summary>要拉起的可执行文件；<c>BuildApplyCommand</c> 会自动填成当前进程（即宿主自己）。</summary>
    public string? RestartExecutable { get; init; }

    /// <summary>
    /// 原样传给宿主的参数 —— 是宿主**原始命令行里去掉 exe 那一段的原文**，所以引号不会被我们重新解释
    /// （自己拼引号是这类功能最常见的翻车点）。
    /// </summary>
    public string? RestartArguments { get; init; }

    /// <summary>宿主的工作目录；不给就沿用 applier 的当前目录。</summary>
    public string? RestartWorkingDirectory { get; init; }

    /// <summary>拉起前的等待，默认 1 秒（给系统收尾和文件句柄释放留一点余量）。</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>applier 等待"树静默"的总时限（§12.5）：超时即整轮不落地，什么都不动。</summary>
    public TimeSpan ApplierQuiescenceTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>applier 的静默轮询间隔。默认 2 秒（实测：按名字扫描约 4.5ms/次）。</summary>
    public TimeSpan ApplierPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 落地模式（AGENT.md §12.1）。默认 <see cref="ApplyMode.InPlace"/>：
    /// 会话内就地替换，全部成功即生效。选 <see cref="ApplyMode.Staged"/> 时只暂存到
    /// <c>UpdaterStage</c>，由独立 applier 在宿主退出后落地 —— 自更新必须走这条路。
    /// </summary>
    public ApplyMode ApplyMode { get; init; } = ApplyMode.InPlace;

    /// <summary>远端 manifest 地址。</summary>
    public required string ManifestUrl { get; init; }

    /// <summary>本地 manifest 路径。默认 {RootPath}/manifest.xml。</summary>
    public string? LocalManifestPath { get; init; }

    /// <summary>
    /// 缓存目录。默认 &lt;系统临时目录&gt;/updater-cache/&lt;安装根指纹&gt;（AGENT.md §12.3）。
    /// 它只放可丢弃的下载产物；被系统清理只意味着重下。
    /// </summary>
    public string? CacheDir { get; init; }

    /// <summary>外部工具（hdiffz / hpatchz）所在目录。默认 {程序目录}/tools。</summary>
    public string? ToolsDir { get; init; }

    /// <summary>不受管目录（顶层目录名，大小写不敏感）。其下文件永不参与更新。</summary>
    public IReadOnlyList<string> ExcludedDirs { get; init; } = [];

    /// <summary>备用资源根地址；主地址失败时按顺序回退（AGENT.md §4.6）。</summary>
    /// <summary>
    /// **内容**（`files/` 与 `patches/`）的基准地址。清单地址与内容地址**允许不同源**：
    /// 例如清单放一台小主机、内容放 CDN。
    /// 不填时退回"`ManifestUrl` 所在目录"（默认行为不变）；
    /// 末尾斜杠可有可无（内部会补齐）。
    /// </summary>
    public string? BaseUrl { get; init; }

    public IReadOnlyList<string> FallbackBaseUrls { get; init; } = [];

    /// <summary>自适应并发的**起始值**（AGENT.md §4.6：2 起）。</summary>
    public const int StartConcurrency = 2;

    /// <summary>并发下载**上限**（AGENT.md §4.6：全成功则逐步加到该值，出现失败就回退到起始值）。</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>
    /// 「连完整下载都失败」的文件数达到该值即判定本地状态不可信、交回宿主。
    /// 这是**绝对下限**，最终阈值取 <c>max(本值, ceil(比例 × 待处理文件数))</c>。
    /// 注意：**不是**"每个文件补丁重试 N 次"——补丁失败是**当次立即回落完整下载**，不重试补丁本身。
    /// </summary>
    public int MinFailuresForHostFallback { get; init; } = 5;

    /// <summary>同上，按比例（相对本次待处理文件数）。大发布里少量噪声失败不该惊动宿主。</summary>
    public double FailRatioForHostFallback { get; init; } = 0.10;

    /// <summary>按本次待处理文件数算出的实际失败容忍度。</summary>
    public int EffectiveFailTolerance(int plannedFiles) =>
        Math.Max(MinFailuresForHostFallback,
                 (int)Math.Ceiling(Math.Max(0, plannedFiles) * FailRatioForHostFallback));

    /// <summary>
    /// 可选保险丝：待下载文件数超过该值即交回宿主。**默认 0 = 关闭**。
    /// 这不是「文件多就走全量」——变更文件越多，增量越有价值。
    /// </summary>
    public int FullPackageThresholdFiles { get; init; }

    /// <summary>同上，按占比。**默认 0 = 关闭**。</summary>
    public double FullPackageThresholdRatio { get; init; }

    /// <summary>占比判据生效的最小文件数；避免小规模产品误判。</summary>
    public int FullPackageRatioMinFiles { get; init; } = 50;

    /// <summary>
    /// 日志路径。默认取三种情况之一：
    /// ① 显式给了本项 → 用它；
    /// ② 否则显式给了 CacheDir → 跟在缓存目录里（历史语义：宿主既然指定了位置，日志就跟着走）；
    /// ③ 两者都没给 → &lt;LocalApplicationData&gt;/updater-logs/&lt;安装根指纹&gt;/update.log。
    /// 情况 ③ **故意不放临时目录**（§12.3）：日志是故障发生**之后**才要看的东西，而临时目录会被清理。
    /// </summary>
    public string? LogPath { get; init; }

    /// <summary>
    /// 日志超过该字节数就在下一次运行时轮转成 <c>update.log.1</c>（只留一代）。
    /// 默认 8 MB；0 = 不轮转。见 AGENT.md §4.11。
    /// </summary>
    public long MaxLogBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// 单个会话的**整体时限**（AGENT.md §4.6「整体有时限」）。默认 2 小时。
    /// 它是最后一道保险：单请求超时管不住"每次都刚好没超时、但总也跑不完"的情况。
    /// </summary>
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>
    /// 是否对「判定为无需更新」的文件重新计算哈希。
    /// 慢（要读全量文件），但能发现本地被篡改/损坏；默认关闭（AGENT.md §4.5）。
    /// </summary>
    public bool VerifyUnchangedFiles { get; init; }

    public string ResolveLocalManifestPath() => LocalManifestPath ?? Path.Combine(RootPath, "manifest.xml");

    public string ResolveCacheDir() =>
        CacheDir ?? Path.Combine(Path.GetTempPath(), "updater-cache", InstallFingerprint());

    public string ResolveLogPath() =>
        LogPath
        ?? (CacheDir is null
            ? Path.Combine(LogRoot(), "updater-logs", InstallFingerprint(), "update.log")
            : Path.Combine(ResolveCacheDir(), "update.log"));

    /// <summary>
    /// 安装根指纹：同一台机器上不同安装目录必须各有各的缓存与日志。
    /// 理由：单实例锁（§4.9）与续传状态都按安装根隔离；两个安装共用一份缓存会让它们互相判成「已有实例在运行」。
    /// 形式 = 根目录名（最多 24 字符，便于人工在临时目录里认出来）+ 根路径哈希前 16 位。
    /// Windows 路径大小写不敏感，因此先归一化再取哈希：同一目录传 "C:\App" 与 "c:\app" 必须得到同一个指纹，
    /// 否则会拿到两份缓存，而锁是按缓存目录定位的 —— 那会让「同一安装根只允许一个会话」失效。
    /// </summary>
    private string InstallFingerprint()
    {
        var full = Path.GetFullPath(RootPath);
        var key = OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        var leaf = SafeName(Path.GetFileName(
            key.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        return leaf.Length == 0 ? hash : leaf + "-" + hash;
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var cleaned = sb.ToString().Trim().TrimEnd('.');
        return cleaned.Length > 24 ? cleaned[..24] : cleaned;
    }

    /// <summary>日志根目录：LocalApplicationData（Linux 上映射到 ~/.local/share）；拿不到就退到主目录。</summary>
    private static string LogRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) return local;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? Path.GetTempPath() : home;
    }

    public string ResolveToolsDir() => ToolsDir ?? Path.Combine(AppContext.BaseDirectory, "tools");

    /// <summary>
    /// **内容**（files/ 与 patches/）的基准地址。
    /// 显式给了 <see cref="BaseUrl"/> 就用它 —— 清单与内容允许不同源（§3.2）；
    /// 没给才退回"manifest 所在目录"，与历史行为一致。
    /// </summary>
    public string ResolveBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(BaseUrl))
            return BaseUrl!.EndsWith('/') ? BaseUrl : BaseUrl + "/";

        var uri = new Uri(ManifestUrl, UriKind.Absolute);
        return new Uri(uri, ".").ToString();
    }
}
