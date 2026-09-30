using Ra3.BattleNet.Updater.Client;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// applier 的**参数契约**：库里只有一份解析实现（<see cref="ApplierConfig.FromArgs"/>），
/// 两个入口两种严格度。这里要钉住的是"生成端与解析端不会漂移"——
/// 那两半以前是各写一遍的（生成在库里，解析在 CLI 里手写），一改就可能在**无人值守的落地时刻**炸。
///
/// 两种严格度各自存在的理由：
///   - applier（<see cref="UnknownArgPolicy.Ignore"/>）：现场那个 exe 可能还是旧的，必须能吃下新参数；
///   - CLI（<see cref="UnknownArgPolicy.Strict"/>）：给人用，防"把两个模式的参数混着递"。
/// </summary>
public class ApplierConfigTests
{
    private static (string Root, string Cache, string Local, string Log) Paths()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "upd-appliercfg-" + Guid.NewGuid().ToString("N"));
        return (Path.Combine(tmp, "install"), Path.Combine(tmp, "cache"),
            Path.Combine(tmp, "install", "manifest.xml"), Path.Combine(tmp, "cache", "update.log"));
    }

    /// <summary>往返：<c>FromArgs(BuildApplyCommand(cfg)) == cfg</c>（身份三件套由命令自动填，单独核）。</summary>
    [Fact]
    public void FromArgs_RoundTripsWhatBuildApplyCommandEmits()
    {
        var (root, cache, local, log) = Paths();
        var cfg = new ApplierConfig
        {
            RootPath = root,
            CacheDir = cache,
            LocalManifestPath = local,
            LogPath = log,
            ApplierQuiescenceTimeout = TimeSpan.FromSeconds(90),
            ApplierPollInterval = TimeSpan.FromSeconds(3),
        };

        var command = StagedApplier.BuildApplyCommand("applier.exe", cfg);
        var parsed = ApplierConfig.FromArgs(command.ArgumentList, UnknownArgPolicy.Strict);

        Assert.Equal(Path.GetFullPath(root), parsed.RootPath);
        Assert.Equal(Path.GetFullPath(cache), parsed.CacheDir);
        Assert.Equal(Path.GetFullPath(local), parsed.LocalManifestPath);
        Assert.Equal(Path.GetFullPath(log), parsed.LogPath);
        Assert.Equal(TimeSpan.FromSeconds(90), parsed.ApplierQuiescenceTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), parsed.ApplierPollInterval);
        // 身份三件套由 BuildApplyCommand 在"宿主进程里"自动填，装的是当前进程
        Assert.Equal(Environment.ProcessId, parsed.WaitForProcessId);
        Assert.False(string.IsNullOrEmpty(parsed.WaitForProcessName));
        Assert.NotNull(parsed.WaitForProcessStartTicks);
        Assert.False(parsed.RestartAfterApply);
        // 没显式给 --manifest-file 时不该带它：两边用**同一个默认值**
        Assert.Null(parsed.ManifestFile);
        Assert.Equal(Path.Combine(Path.GetFullPath(cache), "manifest.remote.xml"), parsed.ResolveManifestFile());
    }

    /// <summary>
    /// **向前兼容**（宽松策略的核心价值）：将来 <c>BuildApplyCommand</c> 加了新参数，
    /// 现场那个**旧 applier exe** 必须照常干活 —— 不认识就不处理、不报错。
    /// </summary>
    [Fact]
    public void FromArgs_WithIgnore_SwallowsFutureParameters()
    {
        var (root, cache, _, _) = Paths();
        var args = new[]
        {
            "--root", root, "--cache-dir", cache,
            "--retry-landing", "3",            // 假想的未来参数（带取值）
            "--future-switch",                 // 假想的未来开关（不带取值）
        };

        var parsed = ApplierConfig.FromArgs(args, UnknownArgPolicy.Ignore);

        Assert.Equal(Path.GetFullPath(root), parsed.RootPath);
        Assert.Equal(Path.GetFullPath(cache), parsed.CacheDir);
        Assert.Equal(TimeSpan.FromMinutes(10), parsed.ApplierQuiescenceTimeout);   // 其余仍是默认
    }

    /// <summary>严格策略（CLI）：白名单之外一律拒，且报错要能指名道姓。</summary>
    [Fact]
    public void FromArgs_WithStrict_RejectsUnknownParameters()
    {
        var (root, _, _, _) = Paths();

        var ex = Assert.Throws<ArgumentException>(() =>
            ApplierConfig.FromArgs(["--root", root, "--manifest-url", "https://example.invalid/manifest.xml"],
                UnknownArgPolicy.Strict));

        Assert.Contains("--manifest-url", ex.Message);
        Assert.Contains("阶段一", ex.Message);      // 报错要把"这是更新会话的参数"说清楚
    }

    /// <summary>必需参数在**两种**策略下都要响亮失败 —— 宽松只管"多出来的参数"。</summary>
    [Fact]
    public void FromArgs_RequiresRoot_InBothPolicies()
    {
        Assert.Throws<ArgumentException>(() =>
            ApplierConfig.FromArgs(["--cache-dir", "x"], UnknownArgPolicy.Strict));
        Assert.Throws<ArgumentException>(() =>
            ApplierConfig.FromArgs(["--cache-dir", "x"], UnknownArgPolicy.Ignore));
    }

    /// <summary>取值缺失（`--root` 后面什么都没有）也要报错，而不是静默吃掉。</summary>
    [Fact]
    public void FromArgs_WithAMissingValue_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ApplierConfig.FromArgs(["--root"], UnknownArgPolicy.Ignore));
        Assert.Contains("缺少取值", ex.Message);
    }
}