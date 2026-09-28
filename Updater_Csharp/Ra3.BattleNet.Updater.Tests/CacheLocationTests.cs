using Ra3.BattleNet.Updater.Client;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 缓存/日志的默认位置（AGENT.md §4.8 / §12.3）。
///
/// 这两个默认值不只是"放哪儿"：**单实例锁与续传状态都按缓存目录定位**，
/// 所以"不同安装根必须得到不同缓存"是 §4.9 能不能成立的硬前提，而不是偏好问题。
/// 其余测试一律用 <see cref="TestSupport.TestCacheDir"/> 显式指定缓存（把清理留在测试自己手里），
/// 因此默认值只有这里在验。
/// </summary>
public class CacheLocationTests
{
    private static UpdateConfig Cfg(string root) =>
        new() { RootPath = root, ManifestUrl = "http://example.invalid/manifest.xml" };

    [Fact]
    public void DefaultCacheDir_IsKeyedByInstallRoot_UnderTheSystemTempDirectory()
    {
        using var tmp = new TempDir();
        var a = tmp.Sub("install-a");
        var b = tmp.Sub("install-b");

        var ca = Cfg(a).ResolveCacheDir();
        var cb = Cfg(b).ResolveCacheDir();
        var temp = Path.GetFullPath(Path.GetTempPath());

        Assert.StartsWith(temp, ca, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(temp, cb, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("updater-cache", ca, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(ca, cb);                                                    // 两个安装 != 一份缓存（锁按它定位）
        Assert.False(ca.StartsWith(Path.GetFullPath(a), StringComparison.OrdinalIgnoreCase),
            "缓存不应再落在安装根目录下");
    }

    [Fact]
    public void DefaultCacheDir_IsStableForTheSameInstallRoot_CaseInsensitivelyOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var root = tmp.Sub("Install");
        var upper = Cfg(root).ResolveCacheDir();
        var lower = Cfg(root.ToLowerInvariant()).ResolveCacheDir();

        Assert.Equal(upper, lower);   // 同目录传不同大小写必须同指纹，否则锁保护会被绕过
    }

    [Fact]
    public void DefaultLogPath_IsStable_AndDeliberatelyNotInTheTempDirectory()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("install");
        var cfg = Cfg(root);
        var log = cfg.ResolveLogPath();

        Assert.EndsWith("update.log", log, StringComparison.OrdinalIgnoreCase);
        Assert.False(log.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase),
            "日志不得默认放临时目录（会被磁盘清理，而日志是事后才要看的）");
        Assert.False(log.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase),
            "日志不再落在安装根目录里");
        Assert.Contains("updater-logs", log, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(cfg.ResolveCacheDir(), Path.GetDirectoryName(log));          // 缓存与日志是两处，不混用
    }

    [Fact]
    public void ExplicitCacheAndLogPaths_StillWin()
    {
        using var tmp = new TempDir();
        var root = tmp.Sub("install");
        var cache = Path.Combine(tmp.Path, "my-cache");
        var log = Path.Combine(tmp.Path, "my.log");

        var cfg = new UpdateConfig
        {
            RootPath = root,
            ManifestUrl = "http://example.invalid/manifest.xml",
            CacheDir = cache,
            LogPath = log,
        };

        Assert.Equal(cache, cfg.ResolveCacheDir());
        Assert.Equal(log, cfg.ResolveLogPath());
    }
}