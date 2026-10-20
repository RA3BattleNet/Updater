using System.Security.Cryptography;
using System.Text;
using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Share.Utilities;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// applier 是**签入仓库的构建产物**（`Applier/{RID}/Client.Applier.exe`），由库的 `Content` 声明
/// 随宿主产出走（与 `HDiffPatch` 同一机制）。这里钉住两件事：
///   ① 签入的那份与**当前源码**是一对；
///   ② 它确实随库的输出走进了引用方的目录，并且库自己能找到它。
/// </summary>
public class ApplierPackagingTests
{
    private sealed record Package(string SourcesHash, string[] Rids, int Sources, string Mode);

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

    /// <summary>
    /// 构建期记录。**它不放在 `Applier/` 里**：那个目录整目录随宿主产出走（`Content`），
    /// 而记录运行时没人读 —— 放进去等于把一个带开发机路径的文件塞进用户的安装树。
    /// </summary>
    private static string RecordPath(string repoRoot) =>
        Path.Combine(repoRoot, "Ra3.BattleNet.Updater.Share", "applier_build.json");

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
            root.GetProperty("Rids").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            root.GetProperty("Sources").GetInt32(),
            root.TryGetProperty("Mode", out var mode) ? mode.GetString() ?? "" : "");
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
        Assert.NotEmpty(pkg.Rids);
        foreach (var rid in pkg.Rids)
            Assert.True(File.Exists(ApplierPath(repoRoot, rid)),
                $"承载目录里没有 applier 产物：{ApplierPath(repoRoot, rid)}");
    }

    /// <summary>
    /// **策略**：签入的 applier 必须是 **Native AOT** 产物。
    ///
    /// 宿主端的要求是"目标机上没有 .NET 也能跑"：宿主可以 self-contained，但 **applier 是独立进程**
    /// （它要等宿主退出才动手，继承不到宿主的自包含运行时）。框架依赖产物在零 runtime 的机器上
    /// 直接起不来 —— 而且只有用户机器上才发现。所以产物形态本身就是一条要被钉住的策略，
    /// 不能只比"源指纹对得上"。
    ///
    /// 本机没有 C++ 工具链时可以用 `-Fdd` 刷一份调试用产物，**但签入仓库的必须是 AOT** ——
    /// 用 `-Fdd` 刷过之后这条会红，这是故意的。
    /// </summary>
    [Fact]
    public void CheckedInApplier_IsNativeAot() =>
        Assert.Equal("aot", ReadPackage(RepoRoot()).Mode);

    /// <summary>
    /// **策略测试**：随包 applier 必须覆盖 `HdiffTool.ShippedRids` 里的每一个平台。
    ///
    /// 理由：仓库已经用测试钉住了"32 位 Windows 宿主是受支持场景"（`PatchToolTests.ShippedRids_CoverThe32BitWindowsHost`）。
    /// applier 若漏掉某个平台，那个平台的宿主上 `FindDefaultApplierExe()` 只会返回 `null` ——
    /// **自更新直接不可用，而且只有运行时才发现**。
    /// 若某平台确实决定暂时不带，必须**同时改这条测试并写明原因** —— 让决定是显式的，不是漏掉的。
    /// </summary>
    [Fact]
    public void ApplierCoversEveryShippedRid()
    {
        var repoRoot = RepoRoot();
        foreach (var rid in HdiffTool.ShippedRids)
        {
            var path = ApplierPath(repoRoot, rid);
            Assert.True(File.Exists(path),
                $"随包 RID {rid} 没有 applier 产物（跑 refresh-applier.ps1，或显式改这条策略测试并说明原因）：{path}");
        }
    }

    /// <summary>
    /// 承载目录里的产物路径。**文件名按 RID 判**（不是按当前 OS 判）：这里可能是在核对**别的平台**的产物
    /// （例如在 Windows 上核对 `linux-x64/Client.Applier`），照当前 OS 拼名字会找错文件。
    /// </summary>
    private static string ApplierPath(string repoRoot, string rid) =>
        Path.Combine(repoRoot, "Ra3.BattleNet.Updater.Share", "Applier", rid,
            rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? "Client.Applier.exe" : "Client.Applier");

    /// <summary>它必须随库的输出进到引用方的目录里 —— 宿主"只引用 client 库"就够，不需要自己做任何部署。</summary>
    [Fact]
    public void ApplierTravelsWithTheLibraryOutput()
    {
        var found = StagedApplier.FindDefaultApplierExe();
        Assert.False(string.IsNullOrEmpty(found), "在本库的输出目录里找不到 applier（Content 没带上？）");
        Assert.True(File.Exists(found!));
        // 【必须】路径里必须**带承载目录**（只断言 "Applier" 会被文件名 Client.Applier.exe 蒙混过去）
        Assert.Contains(Path.Combine("Applier", HdiffTool.Rid), found!);
    }
}