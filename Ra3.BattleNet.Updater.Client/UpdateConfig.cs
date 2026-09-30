namespace Ra3.BattleNet.Updater.Client;

/// <summary>
/// **阶段一（更新会话）的配置**。产品差异**只能**通过这里表达（AGENT.md §6.3）。
///
/// 【必须】落地阶段（applier）用的是**另一个**类型 <see cref="ApplierConfig"/>：
/// 它不联网、也不该能表达"去哪儿取远端"，所以这里的 applier 专用项（等宿主、拉起宿主、
/// 静默时限…）**已经全部搬走**，两者不共享、不派生。改错地方会立刻是编译错误，而不是静默失效。
/// </summary>
public sealed record UpdateConfig
{
    /// <summary>安装根目录。</summary>
    public required string RootPath { get; init; }

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

    /// <summary>
    /// **没有可信基线**（本地清单不存在，或它坏了）时，是否用**磁盘哈希**合成一份「仅本次规划」的基线：
    /// 逐个核对受管文件，磁盘上已经是目标内容的判成「无需更新」。默认 **true**（AGENT.md §4.10）。
    ///
    /// 用它的理由不是省带宽这一点：**没有基线时工作量保险丝会看到假的工作量** ——
    /// 计划里每个文件都是「完整下载」，于是即使树 90% 已经对，也会先一步判"工作量过大"、
    /// 折回宿主整包（§4.4 的实施约束就是这条）。合成基线发生在**规划之前**，保险丝才看到真实工作量。
    ///
    /// 【重要】因此**开着 <see cref="FullPackageThresholdFiles"/> / <see cref="FullPackageThresholdRatio"/>
    /// 时不要关掉本项**：首次更新（或重装后第一次运行）没有本地清单，会**直接撞阈值**。
    /// 关掉它等于放弃这条保护，宿主必须自己知情。
    ///
    /// 【必须】本项只影响**本次计划**：合成出来的基线不落盘，本地清单仍然只在"逐文件哈希验证通过"
    /// 或"落地成功"之后才写，且写的永远是远端原文。清单是验证的产物，不是假设的产物。
    /// 已有可用的本地清单时本项**不生效**（绝不用磁盘推断去覆盖一个存在的基线）。
    /// </summary>
    public bool AdoptLocalTreeWhenNoBaseline { get; init; } = true;

    public string ResolveLocalManifestPath() => LocalManifestPath ?? Path.Combine(RootPath, "manifest.xml");

    /// <summary>
    /// 缓存目录。默认 <c>&lt;系统临时目录&gt;/updater-cache/&lt;安装根指纹&gt;</c>（§12.3）。
    /// 落地阶段（<see cref="ApplierConfig.ResolveCacheDir"/>）必须求出**同一个**结果 ——
    /// 锁与阶段一留下的远端清单都在那里；实现只有一份（<see cref="InstallPaths"/>）。
    /// </summary>
    public string ResolveCacheDir() => InstallPaths.CacheDir(CacheDir, RootPath);

    /// <summary>日志路径；默认规则见 AGENT.md §4.8（与落地阶段共用 <see cref="InstallPaths"/>）。</summary>
    public string ResolveLogPath() => InstallPaths.LogPath(LogPath, CacheDir, RootPath);


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
