using System.Security.Cryptography;
using System.Text;
using Ra3.BattleNet.Updater.Client;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// applier 是**签入仓库的构建产物**（`applier_bin/{RID}/Client.Applier.exe`），由库的 `Content` 声明
/// 随宿主产出走（与 `hdiffpatch_bin` 同一机制）。这里钉住两件事：
///   ① 签入的那份与**当前源码**是一对；
///   ② 它确实随库的输出走进了引用方的目录，并且库自己能找到它。
/// </summary>
public class ApplierPackagingTests
{
    private sealed record Package(string SourcesHash, string Rid, int Sources);

    /// <summary>参与构建 applier 的三份源码（闭包：它引用 Client，Client 引用 Share）。</summary>
    private static readonly string[] FingerprintDirs =
    [
        "Ra3.BattleNet.Updater.Client.Applier",
        "Ra3.BattleNet.Updater.Client",
        "Ra3.BattleNet.Updater.Share",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ra3.BattleNet.sln")))
            dir = dir.Parent;

        Assert.True(dir is not null, "找不到仓库根（含 Ra3.BattleNet.sln 的目录）");
        return dir!.FullName;
    }

    private static string RecordPath(string repoRoot) =>
        Path.Combine(repoRoot, "Ra3.BattleNet.Updater.Share", "applier_bin", "applier.src.json");

    /// <summary>
    /// 与 `refresh-applier.ps1` **逐字一致**的算法：每个源文件一行 <c>相对路径:小写MD5</c>，
    /// 按相对路径的**序数序**排、以 LF 连接（无尾随换行、UTF-8 无 BOM），再取整个文本的 MD5。
    /// 两端算法必须一样 —— 改一处就得改另一处（这条测试就是它们的对照物）。
    /// </summary>
    private static string SourcesFingerprint(string repoRoot)
    {
        var rows = new List<(string Rel, string Md5)>();
        foreach (var d in FingerprintDirs)
        {
            var baseDir = Path.Combine(repoRoot, d);
            if (!Directory.Exists(baseDir)) continue;
            foreach (var f in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(repoRoot, f).Replace('\\', '/');
                if (rel.Contains("/bin/", StringComparison.Ordinal)
                    || rel.Contains("/obj/", StringComparison.Ordinal)) continue;
                if (!rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add((rel, TestSupport.Md5File(f)));
            }
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));
        var text = string.Join("\n", rows.Select(r => $"{r.Rel}:{r.Md5}"));
        return Convert.ToHexStringLower(MD5.HashData(new UTF8Encoding(false).GetBytes(text)));
    }

    private static Package ReadPackage(string repoRoot)
    {
        var path = RecordPath(repoRoot);
        Assert.True(File.Exists(path), $"缺少承载记录：{path}（跑 refresh-applier.ps1）");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        return new Package(root.GetProperty("SourcesHash").GetString()!,
            root.GetProperty("Rid").GetString()!,
            root.GetProperty("Sources").GetInt32());
    }

    /// <summary>
    /// **守卫**：签入的 applier 与当前源码必须是一对。
    ///
    /// 挡的是"改了 applier（或它依赖的 Client/Share）却忘了跑 `refresh-applier.ps1`"：
    /// 那种情况下项目编译出来的 exe 是新的、测试跑的是它、全绿 —— 而随包发出去的是**旧 exe**，
    /// 于是"代码里有修复、现场跑的是旧行为"，且没人会发现。这正是本项目吃过一次的那类亏。
    /// </summary>
    [Fact]
    public void CheckedInApplier_MatchesCurrentSources()
    {
        var repoRoot = RepoRoot();
        var pkg = ReadPackage(repoRoot);

        Assert.Equal(pkg.SourcesHash, SourcesFingerprint(repoRoot));   // 不一致 → 跑 refresh-applier.ps1
        Assert.True(pkg.Sources > 0, "指纹里一个源文件都没有？");
        Assert.True(File.Exists(ApplierPath(repoRoot, pkg.Rid)),
            $"承载目录里没有 applier 产物：{ApplierPath(repoRoot, pkg.Rid)}");
    }

    private static string ApplierPath(string repoRoot, string rid) =>
        Path.Combine(repoRoot, "Ra3.BattleNet.Updater.Share", "applier_bin", rid,
            OperatingSystem.IsWindows() ? "Client.Applier.exe" : "Client.Applier");

    /// <summary>它必须随库的输出进到引用方的目录里 —— 宿主"只引用 client 库"就够，不需要自己做任何部署。</summary>
    [Fact]
    public void ApplierTravelsWithTheLibraryOutput()
    {
        var found = StagedApplier.FindDefaultApplierExe();
        Assert.False(string.IsNullOrEmpty(found), "在本库的输出目录里找不到 applier（Content 没带上？）");
        Assert.True(File.Exists(found!));
        Assert.Contains("applier_bin", found!, StringComparison.OrdinalIgnoreCase);
    }
}