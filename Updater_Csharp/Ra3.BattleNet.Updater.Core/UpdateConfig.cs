namespace Ra3.BattleNet.Updater.Core;

/// <summary>
/// 更新会话配置。产品差异**只能**通过这里表达（AGENT.md §6.3）。
/// </summary>
public sealed record UpdateConfig
{
    /// <summary>安装根目录。</summary>
    public required string RootPath { get; init; }

    /// <summary>远端 manifest 地址。</summary>
    public required string ManifestUrl { get; init; }

    /// <summary>本地 manifest 路径。默认 {RootPath}/manifest.xml。</summary>
    public string? LocalManifestPath { get; init; }

    /// <summary>缓存目录。默认 {RootPath}/UpdaterCache。</summary>
    public string? CacheDir { get; init; }

    /// <summary>外部工具（hdiffz / hpatchz）所在目录。默认 {程序目录}/tools。</summary>
    public string? ToolsDir { get; init; }

    /// <summary>不受管目录（顶层目录名，大小写不敏感）。其下文件永不参与更新。</summary>
    public IReadOnlyList<string> ExcludedDirs { get; init; } = [];

    /// <summary>备用资源根地址；主地址失败时按顺序回退（AGENT.md §4.6）。</summary>
    public IReadOnlyList<string> FallbackBaseUrls { get; init; } = [];

    /// <summary>并发下载上限（AGENT.md §4.6：2 起、上限 4）。</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>
    /// 可选保险丝：待下载文件数超过该值即交回宿主。**默认 0 = 关闭**。
    /// 这不是「文件多就走全量」——变更文件越多，增量越有价值。
    /// </summary>
    public int FullPackageThresholdFiles { get; init; }

    /// <summary>同上，按占比。**默认 0 = 关闭**。</summary>
    public double FullPackageThresholdRatio { get; init; }

    /// <summary>占比判据生效的最小文件数；避免小规模产品误判。</summary>
    public int FullPackageRatioMinFiles { get; init; } = 50;

    /// <summary>日志路径。默认 {CacheDir}/update.log。</summary>
    public string? LogPath { get; init; }

    /// <summary>
    /// 是否对「判定为无需更新」的文件重新计算哈希。
    /// 慢（要读全量文件），但能发现本地被篡改/损坏；默认关闭（AGENT.md §4.5）。
    /// </summary>
    public bool VerifyUnchangedFiles { get; init; }

    public string ResolveLocalManifestPath() => LocalManifestPath ?? Path.Combine(RootPath, "manifest.xml");

    public string ResolveCacheDir() => CacheDir ?? Path.Combine(RootPath, "UpdaterCache");

    public string ResolveLogPath() => LogPath ?? Path.Combine(ResolveCacheDir(), "update.log");

    public string ResolveToolsDir() => ToolsDir ?? Path.Combine(AppContext.BaseDirectory, "tools");

    /// <summary>远端 manifest 所在目录，作为 files/ 与 patches/ 的基准地址。</summary>
    public string ResolveBaseUrl()
    {
        var uri = new Uri(ManifestUrl, UriKind.Absolute);
        return new Uri(uri, ".").ToString();
    }
}
