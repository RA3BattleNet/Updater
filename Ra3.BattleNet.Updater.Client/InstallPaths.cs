using System.Security.Cryptography;
using System.Text;

namespace Ra3.BattleNet.Updater.Client;

/// <summary>
/// 缓存与日志的**默认位置**（AGENT.md §4.8 / §12.3）。
///
/// 为什么值得单独一个类型：阶段一（<see cref="UpdateConfig"/>）与落地阶段（<see cref="ApplierConfig"/>）
/// 是两个**不同的进程**，它们必须对"缓存在哪、日志在哪"求出**逐字相同**的结果 ——
/// 否则 applier 会去别处找阶段一留下的远端清单、写另一份日志，而更新锁也是按缓存目录定位的
/// （不同目录 = 锁失效 = 两个实例同时动同一棵树）。
/// 所以这里只有一份实现，两个配置类型都调它。
/// </summary>
internal static class InstallPaths
{
    public static string CacheDir(string? explicitCacheDir, string rootPath) =>
        explicitCacheDir ?? Path.Combine(Path.GetTempPath(), "updater-cache", Fingerprint(rootPath));

    /// <summary>
    /// 日志路径。优先级（历史语义，不动）：显式 <paramref name="explicitLog"/> → 显式
    /// <paramref name="explicitCacheDir"/> 下的 <c>update.log</c>（宿主既然指定了位置，日志就跟着走）→
    /// <c>&lt;LocalApplicationData&gt;/updater-logs/&lt;安装根指纹&gt;/update.log</c>。
    /// 最后那种**故意不放临时目录**：日志是故障发生**之后**才要看的东西，而临时目录会被清理。
    /// </summary>
    public static string LogPath(string? explicitLog, string? explicitCacheDir, string rootPath) =>
        explicitLog
        ?? (explicitCacheDir is null
            ? Path.Combine(LogRoot(), "updater-logs", Fingerprint(rootPath), "update.log")
            : Path.Combine(CacheDir(explicitCacheDir, rootPath), "update.log"));

    /// <summary>
    /// 安装根指纹：同一台机器上不同安装目录必须各有各的缓存与日志。
    /// 理由：单实例锁（§4.9）与续传状态都按安装根隔离；两个安装共用一份缓存会让它们互相判成「已有实例在运行」。
    /// 形式 = 根目录名（最多 24 字符，便于人工在临时目录里认出来）+ 根路径哈希前 16 位。
    /// Windows 路径大小写不敏感，因此先归一化再取哈希：同一目录传 "C:\App" 与 "c:\app" 必须得到同一个指纹，
    /// 否则会拿到两份缓存，而锁是按缓存目录定位的 —— 那会让「同一安装根只允许一个会话」失效。
    /// </summary>
    public static string Fingerprint(string rootPath)
    {
        var full = Path.GetFullPath(rootPath);
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
}