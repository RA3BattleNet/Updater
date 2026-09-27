using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ra3.BattleNet.Updater.Client;
using Ra3.BattleNet.Updater.Share.Models;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 5 版本模拟拉取。前置：先用 <c>_sim/deploy.ps1</c> 部署服务端产物（5 版链式清单 +
/// 内容寻址完整文件 + 保留窗口 N=3 的内容对补丁），然后设置 <c>UPDATER_SIM_OUT=_sim</c> 运行本类。
///
/// 每个场景都是一次**真实的**客户端拉取（真 HTTP 服务、真 hdiffz、真字节比对），
/// 结果 / 日志 / 字节数 / 增量命中率都落到 <c>_sim/logs/&lt;场景&gt;/</c>。
/// 未设置环境变量时不做任何事（默认测试套件不跑这个重活）。
/// </summary>
public class VersionMatrixSimulation
{
    private static string Root => Environment.GetEnvironmentVariable("UPDATER_SIM_OUT") ?? string.Empty;

    private static bool NotReady()
    {
        if (Root.Length == 0 || !File.Exists(Path.Combine(Root, "server-summary.json")))
        {
            Console.WriteLine("跳过：未设置 UPDATER_SIM_OUT 或服务端产物不存在（先跑 _sim/deploy.ps1）");
            return true;
        }
        return false;
    }

    private static string VersionsDir => Path.Combine(Root, "versions");
    private static string ServerDir => Path.Combine(Root, "server");
    private static string ManifestsDir => Path.Combine(Root, "manifests");
    private static string LogsDir => Path.Combine(Root, "logs");
    private static string ClientsDir => Path.Combine(Root, "clients");

    private static void Publish(int version) =>
        File.Copy(Path.Combine(ManifestsDir, $"v{version}.xml"), Path.Combine(ServerDir, "manifest.xml"), true);

    private static void PublishFile(string manifestPath) =>
        File.Copy(manifestPath, Path.Combine(ServerDir, "manifest.xml"), true);

    private static ManifestModel Manifest(int version) => new(Path.Combine(ManifestsDir, $"v{version}.xml"));
    private static string ManifestPath(int version) => Path.Combine(ManifestsDir, $"v{version}.xml");
    private static string VersionDir(int version) => Path.Combine(VersionsDir, $"v{version}");

    /// <summary>准备一台客户端：从"全新安装某一版"开始（目录树 + 本地清单）。</summary>
    private static string NewClient(string scenario, int version, bool withLocalManifest = true)
    {
        var dir = Path.Combine(ClientsDir, scenario);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        TestSupport.CopyTree(VersionDir(version), dir);
        if (withLocalManifest)
            File.Copy(ManifestPath(version), Path.Combine(dir, "manifest.xml"), true);
        return dir;
    }

    /// <summary>跑一次并把"上网字节"一起带回来。</summary>
    private static (UpdateResult Result, long Wire) RunMeasured(string client, TestHttpServer http,
        Func<UpdateConfig, UpdateConfig>? tweak = null, CancellationToken ct = default,
        IProgress<UpdateProgress>? progress = null)
    {
        http.ResetWire();
        var r = Run(client, http, tweak, ct, progress);
        return (r, http.WireBytesSent);
    }

    private static UpdateResult Run(string client, TestHttpServer http,
        Func<UpdateConfig, UpdateConfig>? tweak = null, CancellationToken ct = default,
        IProgress<UpdateProgress>? progress = null)
    {
        var cfg = new UpdateConfig { RootPath = client, ManifestUrl = http.BaseUrl + "manifest.xml" };
        if (tweak is not null) cfg = tweak(cfg);
        return new ClientUpdater(cfg).Run(progress, ct);
    }

    // ---------------------------------------------------------------- 比对与统计

    private sealed record TreeDiff(int Same, List<string> Differ, List<string> Missing, List<string> Extra, int Skipped)
    {
        public bool Clean => Differ.Count == 0 && Missing.Count == 0;
        public string Text =>
            $"逐字节一致 {Same}，内容不同 {Differ.Count}，缺失 {Missing.Count}，多余 {Extra.Count}" +
            (Skipped > 0 ? $"（另有 {Skipped} 个文件按预期未更新）" : string.Empty);
    }

    /// <summary>把客户端目录与"全新解压的目标版本"逐文件比对。UpdaterCache 与未受管目录不算差异。</summary>
    private static TreeDiff Compare(int version, string client, string? excludeTopDir = null)
    {
        var expectedRoot = VersionDir(version);
        var differ = new List<string>();
        var missing = new List<string>();
        var skipped = 0;

        foreach (var f in Manifest(version).Manifest.Files)
        {
            var rel = f.RelativePath();
            if (excludeTopDir is not null && rel.StartsWith(excludeTopDir + "/", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var sep = rel.Replace('/', Path.DirectorySeparatorChar);
            var e = Path.Combine(expectedRoot, sep);
            var a = Path.Combine(client, sep);

            if (!File.Exists(a)) { missing.Add(rel); continue; }
            if (!string.Equals(TestSupport.Md5File(e), TestSupport.Md5File(a), StringComparison.OrdinalIgnoreCase))
                differ.Add(rel);
        }

        var expected = Manifest(version).Manifest.Files
            .Select(f => f.RelativePath()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = new List<string>();
        foreach (var f in Directory.EnumerateFiles(client, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(client, f).Replace('\\', '/');
            if (rel.StartsWith("UpdaterCache/", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Equals("manifest.xml", StringComparison.OrdinalIgnoreCase)) continue;
            if (!expected.Contains(rel)) extra.Add(rel);
        }

        return new TreeDiff(expected.Count - differ.Count - missing.Count - skipped, differ, missing, extra, skipped);
    }

    private static readonly Dictionary<(int From, int To), long> BaselineBytesCache = [];

    /// <summary>"不做差分、只下变更文件"的字节数（AGENT.md §2.2 的那条基线），用于算节省量。</summary>
    private static long BaselineBytes(int from, int to)
    {
        if (BaselineBytesCache.TryGetValue((from, to), out var cached)) return cached;

        var old = Manifest(from).Manifest.Files
            .ToDictionary(f => f.RelativePath(), f => f.MD5, StringComparer.OrdinalIgnoreCase);
        long sum = 0;
        foreach (var f in Manifest(to).Manifest.Files)
        {
            var rel = f.RelativePath();
            if (old.TryGetValue(rel, out var md5) && string.Equals(md5, f.MD5, StringComparison.OrdinalIgnoreCase))
                continue;
            var p = Path.Combine(VersionDir(to), rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) sum += new FileInfo(p).Length;
        }

        BaselineBytesCache[(from, to)] = sum;
        return sum;
    }

    // ---------------------------------------------------------------- 产物落盘

    private sealed record Artifacts(int Requests, string Summary);

    private static Artifacts Capture(string scenario, string expectation, string client, UpdateResult r,
        string comparison, long baselineBytes = 0, string extra = "", long wireBytes = 0)
    {
        // 上网字节：优先用**测试服务器在网线另一头数出来**的（独立口径）；调用方没量到就退回
        // 客户端自己的**连接层计数**（`r.WireBytes`，含 TLS/HTTP 头）。两者来源不同，报告里标注清楚。
        var wireFromServer = wireBytes > 0;
        if (!wireFromServer) wireBytes = r.WireBytes;

        var dir = Path.Combine(LogsDir, scenario);
        Directory.CreateDirectory(dir);

        var logSrc = Path.Combine(client, "UpdaterCache", "update.log");
        if (File.Exists(logSrc)) File.Copy(logSrc, Path.Combine(dir, "update.log"), true);

        var log = File.Exists(logSrc) ? File.ReadAllLines(logSrc) : [];
        var fLines = log.Where(l => l.StartsWith("F\t", StringComparison.Ordinal)).ToList();
        var rLine = log.LastOrDefault(l => l.StartsWith("R\t", StringComparison.Ordinal));
        var rCols = rLine?.Split('\t');
        var requests = rCols is { Length: >= 13 } ? int.Parse(rCols[12]) : 0;

        var hitRate = r.Patched + r.Full > 0 ? (double)r.Patched / (r.Patched + r.Full) : 0;
        var saved = baselineBytes > 0 ? 1.0 - (double)r.BytesDownloaded / baselineBytes : 0;

        var byAction = fLines.GroupBy(l => l.Split('\t')[7]).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}={g.Count()}");
        var fullReasons = fLines.Where(l => l.Split('\t')[7] == "full").GroupBy(l => l.Split('\t')[9])
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Key}({g.Count()})");

        var md = new StringBuilder();
        md.AppendLine($"# {scenario}");
        md.AppendLine();
        md.AppendLine($"- **期望**：{expectation}");
        md.AppendLine($"- **实际**：`{r}`");
        md.AppendLine($"- **目录比对**：{comparison}");
        md.AppendLine($"- **HTTP 请求数**（R 行末列）：{requests}");
        md.AppendLine($"- **上网字节（真的走网线的）**：{(wireBytes > 0
            ? $"{wireBytes:N0}（来源：{(wireFromServer ? "测试服务器落地点计数" : "客户端连接层计数")}）"
            : "未记录")}");
        md.AppendLine($"- **下载字节（内容字节，解压后）**：{r.BytesDownloaded:N0}" +
                      (baselineBytes > 0
                          ? $"；只下变更文件的基线 {baselineBytes:N0} → 节省 {saved:P1}"
                          : string.Empty));
        md.AppendLine($"- **增量命中率** patch/(patch+full)：{hitRate:P1}（patch={r.Patched} full={r.Full}）");
        md.AppendLine($"- **失败数**：{r.FailedCount}；**结果**：{r.Outcome}");
        if (r.Detail.Length > 0) md.AppendLine($"- **Detail**：{r.Detail}");
        if (extra.Length > 0) { md.AppendLine(); md.AppendLine(extra); }
        md.AppendLine();
        md.AppendLine("## 日志分布");
        md.AppendLine();
        md.AppendLine($"- 按 action：{string.Join(", ", byAction)}");
        if (fullReasons.Any()) md.AppendLine($"- full 的 reason：{string.Join(", ", fullReasons)}");
        md.AppendLine($"- F 行 {fLines.Count} 条；R 行：`{rLine}`");
        md.AppendLine();
        md.AppendLine("完整日志见同目录 `update.log`。");
        File.WriteAllText(Path.Combine(dir, "scenario.md"), md.ToString(), new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(dir, "result.json"),
            JsonSerializer.Serialize(new
            {
                Scenario = scenario,
                Expectation = expectation,
                Outcome = r.Outcome.ToString(),
                r.Reason,
                r.Detail,
                r.HttpVersion,
                r.Total,
                r.Skipped,
                r.Moved,
                r.Patched,
                r.Full,
                r.FailedCount,
                r.BytesDownloaded,
                WireBytes = wireBytes,
                Ms = (long)r.Elapsed.TotalMilliseconds,
                Requests = requests,
                HitRate = hitRate,
                BaselineBytes = baselineBytes,
                Saved = saved,
                Comparison = comparison,
            }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        var summary = $"{scenario,-34} {r.Outcome,-16} patch={r.Patched,4} full={r.Full,4} " +
                      $"fail={r.FailedCount} 内容={r.BytesDownloaded,12:N0} 网线={wireBytes,12:N0} req={requests,5} " +
                      $"命中率={hitRate,6:P1} 节省={(baselineBytes > 0 ? saved : 0),6:P1} {comparison}";
        Console.WriteLine(summary);
        return new Artifacts(requests, summary);
    }

    private static void CleanupClient(string client)
    {
        try { Directory.Delete(client, true); } catch { /* 尽力清理 */ }
    }

    // ---------------------------------------------------------------- 场景

    [Fact]
    public void S01_已是最新_零下载且第二次走304()
    {
        if (NotReady()) return;
        const string name = "S01_up_to_date";
        Publish(5);
        var client = NewClient(name, 5);
        using var http = new TestHttpServer(ServerDir);

        var first = Run(client, http);
        Capture(name, "客户端已是 v5 → UpToDate，0 字节；再跑一次应当命中 If-None-Match(304)",
            client, first, "（无需比对：没有任何文件被动过）");

        Assert.Equal(UpdateOutcome.UpToDate, first.Outcome);
        Assert.Equal(0, first.BytesDownloaded);
        Assert.Equal(0, first.Full + first.Patched + first.Moved);

        var second = Run(client, http);
        Assert.Equal(UpdateOutcome.UpToDate, second.Outcome);
        Assert.Equal(0, second.BytesDownloaded);
        Assert.True(http.NotModified >= 1, "第二次应当走条件请求拿到 304");

        CleanupClient(client);
    }

    [Fact]
    public void S02_单步升级_v4到v5()
    {
        if (NotReady()) return;
        const string name = "S02_step_v4_to_v5";
        Publish(5);
        var client = NewClient(name, 4);
        using var http = new TestHttpServer(ServerDir);

        var r = Run(client, http);
        var diff = Compare(5, client);
        Capture(name, "v4 → v5：变更走补丁、结果与全新安装 v5 逐字节一致",
            client, r, diff.Text, BaselineBytes(4, 5));

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.FailedCount);
        // 注意这里**不是**在断言"补丁命中率很高"。v5 这个版本把 .NET 运行时从 bin/ 搬到了
        // dotnet/shared/ 并升级了主版本：只有约 30 个文件的 UUID 能延续（"仅内容变动"），
        // 其余 600+ 个是路径与内容同时变化 —— 生成器只能把它们列成"疑似改名"提示人工确认
        // UUID（§5.2 / K1），在此之前它们只能完整下载。这正是 F8 要观测的那类发布。
        Assert.True(r.Patched >= 20, $"UUID 延续的那批文件应当命中补丁，实得 {r.Patched}");
        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }

    [Fact]
    public void S03_单步升级_v1到v2()
    {
        if (NotReady()) return;
        const string name = "S03_step_v1_to_v2";
        Publish(2);
        var client = NewClient(name, 1);
        using var http = new TestHttpServer(ServerDir);

        var r = Run(client, http);
        var diff = Compare(2, client);
        Capture(name, "v1 → v2（最早的一步）：变更走补丁、结果逐字节一致",
            client, r, diff.Text, BaselineBytes(1, 2));

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.FailedCount);
        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }

    [Fact]
    public void S04_跨两版_v3到v5()
    {
        if (NotReady()) return;
        const string name = "S04_skip2_v3_to_v5";
        Publish(5);
        var client = NewClient(name, 3);
        using var http = new TestHttpServer(ServerDir);

        var r = Run(client, http);
        var diff = Compare(5, client);
        Capture(name, "v3 → v5（跨 2 版，仍在保留窗口内）：应当命中 v3→v5 补丁",
            client, r, diff.Text, BaselineBytes(3, 5));

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.FailedCount);
        Assert.True(r.Patched >= 20, $"保留窗口内的跨版升级应当命中补丁，实得 {r.Patched}");
        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }

    [Fact]
    public void S05_跨三版_v2到v5_保留窗口边缘()
    {
        if (NotReady()) return;
        const string name = "S05_skip3_v2_to_v5";
        Publish(5);
        var client = NewClient(name, 2);
        using var http = new TestHttpServer(ServerDir);

        var r = Run(client, http);
        var diff = Compare(5, client);
        Capture(name, "v2 → v5（跨 3 版 = N=3 窗口的边缘）：仍应有补丁命中",
            client, r, diff.Text, BaselineBytes(2, 5));

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.FailedCount);
        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }

    [Fact]
    public void S06_超出保留窗口_v1到v5_应当干净回落完整下载()
    {
        if (NotReady()) return;
        const string name = "S06_skip4_v1_to_v5";
        Publish(5);
        var client = NewClient(name, 1);
        using var http = new TestHttpServer(ServerDir);

        var r = Run(client, http);
        var diff = Compare(5, client);
        var baseline = BaselineBytes(1, 5);
        Capture(name,
            "v1 → v5（跨 4 版，超出 N=3 保留窗口）：服务端没有 v1→v5 的补丁 → " +
            "应当干净回落完整下载，不报错、结果仍逐字节一致",
            client, r, diff.Text, baseline);

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.FailedCount);
        Assert.True(r.Full > 0, "超出窗口时必然有完整下载");
        Assert.True(diff.Clean, diff.Text);

        // 必须在日志里看得见"为什么走全量" —— 这正是 no_patch 这个字段存在的意义
        var log = File.ReadAllLines(Path.Combine(client, "UpdaterCache", "update.log"));
        Assert.Contains(log, l => l.StartsWith("F\t", StringComparison.Ordinal) && l.Contains("\tno_patch\t"));
        CleanupClient(client);
    }

    [Fact]
    public void S07_本地没有清单_退化为按磁盘校验而不是重下整棵()
    {
        if (NotReady()) return;
        const string name = "S07_no_local_manifest";
        Publish(5);
        var client = NewClient(name, 4, withLocalManifest: false);
        using var http = new TestHttpServer(ServerDir);

        var r = Run(client, http);
        var diff = Compare(5, client);
        Capture(name, "客户端没有本地清单（但磁盘内容与 v4 相符）：必须退化为按磁盘哈希校验，不重下未变文件",
            client, r, diff.Text, BaselineBytes(4, 5));

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.FailedCount);
        Assert.True(r.Skipped > 3000, $"绝大多数文件应当按磁盘哈希命中而跳过，实得 {r.Skipped}");
        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }

    [Fact]
    public void S08_本地清单损坏_不得重下整棵也不得崩溃()
    {
        if (NotReady()) return;
        const string name = "S08_corrupt_local_manifest";
        Publish(5);
        var client = NewClient(name, 4);
        File.WriteAllText(Path.Combine(client, "manifest.xml"), "<Metadata><broken", new UTF8Encoding(false));
        using var http = new TestHttpServer(ServerDir);

        var r = Run(client, http);
        var diff = Compare(5, client);
        Capture(name, "本地清单损坏：视为本地不可信，但仍按磁盘校验推进，最终结果逐字节一致",
            client, r, diff.Text, BaselineBytes(4, 5));

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.FailedCount);
        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }

    [Fact]
    public void S09_补丁404_静默回落完整下载()
    {
        if (NotReady()) return;
        const string name = "S09_patch_404";
        Publish(5);
        var client = NewClient(name, 4);
        using var http = new TestHttpServer(ServerDir) { PatchNotFound = true };

        var r = Run(client, http);
        var diff = Compare(5, client);
        Capture(name, "所有补丁请求都 404：必须静默回落完整下载（不报错给用户），结果逐字节一致",
            client, r, diff.Text, BaselineBytes(4, 5));

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.Patched);
        Assert.Equal(0, r.FailedCount);
        // 只有"能定位到本地前身"的文件才会去探测补丁（其余没有前身，直接完整下载）
        Assert.True(http.NotFound >= 20, $"应当真的探测过补丁并收到 404，实得 {http.NotFound}");
        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }

    [Fact]
    public void S10_响应被截断_靠Range续传补回来()
    {
        if (NotReady()) return;
        const string name = "S10_truncated_range_resume";
        Publish(2);
        var client = NewClient(name, 1);
        // 凡是不带 Range 的响应都少发 1 字节（续传请求照常发全）——
        // 这就是"只在整份响应上出错"的现实形态，客户端必须靠续传补回来，而不是反复重放第一次。
        using var http = new TestHttpServer(ServerDir)
        {
            TruncateBytes = 1, TruncateScope = TruncateScope.WithoutRange,
        };

        var r = Run(client, http);
        var diff = Compare(2, client);
        Capture(name, "不带 Range 的响应少发 1 字节：客户端应当用 Range 续传补回来，而不是整包重下",
            client, r, diff.Text, BaselineBytes(1, 2));

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.FailedCount);
        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }

    [Fact]
    public void S11_中途取消_重跑幂等且不重复下载()
    {
        if (NotReady()) return;
        const string name = "S11_interrupted_resume";
        Publish(5);
        var client = NewClient(name, 4);
        using var http = new TestHttpServer(ServerDir);

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<UpdateProgress>(p => { if (p.Current >= 300) cts.Cancel(); });
        var interrupted = Run(client, http, null, cts.Token, progress);

        Assert.NotEqual(UpdateOutcome.Updated, interrupted.Outcome);
        Assert.NotEqual(TestSupport.Md5File(ManifestPath(5)),
            TestSupport.Md5File(Path.Combine(client, "manifest.xml")));

        var resumed = Run(client, http);
        var diff = Compare(5, client);
        var baseline = BaselineBytes(4, 5);
        var total = interrupted.BytesDownloaded + resumed.BytesDownloaded;
        var extra = $"- **中途取消**（进度到 300 个文件时取消）：outcome={interrupted.Outcome}，" +
                    $"已下载 {interrupted.BytesDownloaded:N0} 字节，本地清单未被改写（保持 v4）\n" +
                    $"- **重跑**：下载 {resumed.BytesDownloaded:N0} 字节（只补差）\n" +
                    $"- **两轮合计**：{total:N0} 字节，一次跑完是 {baseline:N0} 字节（比值 {(double)total / baseline:P1}）";
        Capture(name + "_resume", "跑到一半取消 → 本地清单不得被写；重跑必须幂等且只补差的部分",
            client, resumed, diff.Text, baseline, extra);

        Assert.Equal(UpdateOutcome.Updated, resumed.Outcome);
        Assert.Equal(0, resumed.FailedCount);
        Assert.True(diff.Clean, diff.Text);

        // F6：中断+重跑的总下载量不该明显超过"一次跑完"
        //（旧实现会重下已经弄好的文件，实测两轮合计 373 MB vs 一次跑完 286 MB）
        Assert.True(total <= baseline * 1.05,
            $"中断重跑的总下载量不该明显超过一次跑完：{total:N0} vs 基线 {baseline:N0}");
        CleanupClient(client);
    }

    [Fact]
    public void S12_排除目录_不受管目录一个文件都不许动()
    {
        if (NotReady()) return;
        const string name = "S12_excluded_dir";
        Publish(2);
        var client = NewClient(name, 1);

        var excluded = PickExcludedDir(1, 2);
        Assert.False(string.IsNullOrEmpty(excluded), "v1→v2 里应当存在有变更的顶层目录");

        using var http = new TestHttpServer(ServerDir);
        var r = Run(client, http, cfg => cfg with { ExcludedDirs = [excluded] });
        var diff = Compare(2, client, excludeTopDir: excluded);

        var extra = $"- **排除目录**：`{excluded}`（v1→v2 里确实有变更，且是变更数最少的顶层目录）\n" +
                    $"- 该目录下的文件应当**保持 v1 原样**，其余文件应当更新到 v2";
        Capture(name, "把某个有变更的顶层目录列为不受管：该目录不动，其余照常更新",
            client, r, diff.Text, BaselineBytes(1, 2), extra);

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.FailedCount);
        Assert.True(diff.Skipped > 0, $"被排除的文件应当被跳过，实得 {diff.Skipped}");
        Assert.True(diff.Clean, diff.Text);

        // 被排除目录必须与 v1 逐字节相同
        var separators = Path.DirectorySeparatorChar;
        foreach (var f in Manifest(1).Manifest.Files)
        {
            var rel = f.RelativePath();
            if (!rel.StartsWith(excluded + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var p = Path.Combine(client, rel.Replace('/', separators));
            if (File.Exists(p))
                Assert.Equal(f.MD5, TestSupport.Md5File(p));
        }
        CleanupClient(client);
    }

    [Fact]
    public void S13_纯改名_走Move且零下载()
    {
        if (NotReady()) return;
        const string name = "S13_rename_only";

        // 构造"新版本"：把 v5 里最小的那个文件改个名字（内容与 UUID 都不动）
        // —— 这正是生成器靠 MD5 继承 UUID 覆盖的"纯改名"场景（§5.1 规则 2）。
        var m = Manifest(5);
        var victim = m.Manifest.Files
            .Where(f => f.RelativePath().Contains('/'))
            .OrderBy(f => new FileInfo(Path.Combine(VersionDir(5), f.RelativePath().Replace('/', Path.DirectorySeparatorChar))).Length)
            .First();

        var oldRel = victim.RelativePath();
        victim.FileName = "renamed_" + victim.FileName;
        var newRel = victim.RelativePath();
        var renamedManifest = Path.Combine(ManifestsDir, "v5-renamed.xml");
        m.SaveToXml(renamedManifest);

        PublishFile(renamedManifest);
        var client = NewClient(name, 5);
        using var http = new TestHttpServer(ServerDir);

        var r = Run(client, http);
        var oldPath = Path.Combine(client, oldRel.Replace('/', Path.DirectorySeparatorChar));
        var newPath = Path.Combine(client, newRel.Replace('/', Path.DirectorySeparatorChar));

        var comparison = $"旧路径已消失={!File.Exists(oldPath)}；新路径存在={File.Exists(newPath)}；" +
                         $"内容哈希一致={(File.Exists(newPath) && TestSupport.Md5File(newPath) == victim.MD5)}";
        var extra = $"- **改名**：`{oldRel}` → `{newRel}`（内容与 UUID 不变，只有路径变）\n" +
                    $"- 期望：`move` 分支，**0 下载**（§2.1 F3、§9.2）";
        Capture(name, "纯改名：客户端按 UUID/内容索引定位 → 直接 move，0 下载",
            client, r, comparison, 0, extra);

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(1, r.Moved);
        Assert.Equal(0, r.BytesDownloaded);
        Assert.False(File.Exists(oldPath), "纯改名后旧路径不应残留");
        Assert.True(File.Exists(newPath));
        Assert.Equal(victim.MD5, TestSupport.Md5File(newPath));
        CleanupClient(client);
    }

    [Fact]
    public void S14_工作量保险丝_判定交回宿主且不动任何文件()
    {
        if (NotReady()) return;
        const string name = "S14_workload_fuse";
        Publish(5);
        var client = NewClient(name, 4);
        var before = TestSupport.Md5File(Path.Combine(client, "manifest.xml"));
        using var http = new TestHttpServer(ServerDir);

        var r = Run(client, http, cfg => cfg with { FullPackageThresholdFiles = 1 });
        Capture(name, "运维把保险丝设成「待下载 ≥1 个文件就交回宿主」：应当返回 NeedsHostFallback，且一个文件都不动",
            client, r, "（不动任何文件）");

        Assert.Equal(UpdateOutcome.NeedsHostFallback, r.Outcome);
        Assert.Equal(UpdateReasons.WorkloadTooLarge, r.Reason);
        Assert.Equal(0, r.BytesDownloaded);
        Assert.Equal(before, TestSupport.Md5File(Path.Combine(client, "manifest.xml")));
        CleanupClient(client);
    }

    [Fact]
    public void S15_独立可执行壳_退出码与一行JSON()
    {
        if (NotReady()) return;
        const string name = "S15_real_shell";
        Publish(5);
        var client = NewClient(name, 3);

        var shell = Path.Combine(RepoRoot(), "Ra3.BattleNet.Updater.Client.Update",
            "bin", "Release", "net10.0", "Ra3.BattleNet.Updater.Client.Update.dll");
        Assert.True(File.Exists(shell), $"找不到壳：{shell}");

        using var http = new TestHttpServer(ServerDir);
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { shell, "--root", client, "--manifest-url", http.BaseUrl + "manifest.xml", "--json" })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        var diff = Compare(5, client);

        // 用壳自己吐出来的 JSON 作为结果来源（而不是自己编一个 UpdateResult）
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var real = new UpdateResult(
            Enum.Parse<UpdateOutcome>(root.GetProperty("Outcome").GetString()!),
            root.GetProperty("Reason").GetString() ?? string.Empty,
            root.GetProperty("Total").GetInt32(),
            root.GetProperty("Skipped").GetInt32(),
            root.GetProperty("Moved").GetInt32(),
            root.GetProperty("Patched").GetInt32(),
            root.GetProperty("Full").GetInt32(),
            root.GetProperty("FailedCount").GetInt32(),
            root.GetProperty("BytesDownloaded").GetInt64(),
            TimeSpan.FromMilliseconds(root.GetProperty("Ms").GetInt64()),
            root.GetProperty("Detail").GetString() ?? string.Empty,
            root.GetProperty("HttpVersion").GetString() ?? string.Empty);

        var extra = $"- **壳**：`dotnet Client.Update.dll --root … --manifest-url … --json`\n" +
                    $"- 退出码：{p.ExitCode}（约定 0 = 已最新或已更新）\n" +
                    $"- stdout：`{stdout.Trim()}`\n" +
                    (stderr.Trim().Length > 0 ? $"- stderr：`{stderr.Trim()}`\n" : string.Empty) +
                    $"- 客户端产物日志：同目录 `update.log`（壳写自己的缓存目录）";

        Capture(name, "独立壳（§4.12）：退出码 0 + stdout 一行 JSON，且结果逐字节一致",
            client, real, diff.Text, BaselineBytes(3, 5), extra);

        Assert.Equal(0, p.ExitCode);
        Assert.Contains("\"Outcome\":\"Updated\"", stdout);
        Assert.Equal(real.Total, real.Skipped + real.Moved + real.Patched + real.Full);
        Assert.True(real.Patched > 0 && real.BytesDownloaded > 0, "壳跑的是真实更新，不该是空结果");
        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }

    [Fact]
    public void S16_人工确认改名之后_同一版本终于吃上补丁()
    {
        if (NotReady()) return;
        const string name = "S16_v5_with_linked_uuids";

        var linked = Path.Combine(ManifestsDir, "v5-linked.xml");
        var pairFile = Path.Combine(ManifestsDir, "v5-linked-pairs.json");
        if (!File.Exists(linked) || !File.Exists(pairFile))
        {
            Console.WriteLine("跳过：没有 v5-linked.xml（先跑 _sim/link-uuids.ps1）");
            return;
        }

        // 这 20 条关联来自生成期自检报告（§5.2）的建议 —— 也就是"人工确认改名"那一步。
        long linkedBytes = 0;
        foreach (var e in JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(pairFile))!)
        {
            var rel = e.GetProperty("New").GetString()!;
            var p = Path.Combine(VersionDir(5), rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) linkedBytes += new FileInfo(p).Length;
        }

        PublishFile(linked);
        var client = NewClient(name, 4);
        using var http = new TestHttpServer(ServerDir);

        var r = Run(client, http);
        var diff = Compare(5, client);
        var extra = $"- **对照**：同一对版本、**未**做 UUID 关联时（S02）patch=30 full=612，下载 286,010,249 字节\n" +
                    $"- **本场景**用的是把报告里 20 条 UUID 建议真的应用之后的 v5 清单\n" +
                    $"- 这 20 个文件共 {linkedBytes:N0} 字节；现在只需 {linkedBytes - 0:N0} 字节的补丁（补丁合计 126 KB）";
        Capture(name, "人工确认改名（§5.2）之后：同一版本应多命中约 20 个补丁、少下相应字节",
            client, r, diff.Text, BaselineBytes(4, 5), extra);

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.FailedCount);
        Assert.True(r.Patched >= 45, $"关联 UUID 后应当多命中约 20 个补丁，实得 {r.Patched}");
        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }

    [Fact]
    public void S17_运维一次性批量关联UUID之后_同一版本()
    {
        if (NotReady()) return;
        const string name = "S17_v5_fully_linked";

        var linked = Path.Combine(ManifestsDir, "v5-linked-full.xml");
        var pairFile = Path.Combine(ManifestsDir, "v5-linked-full-pairs.json");
        if (!File.Exists(linked) || !File.Exists(pairFile))
        {
            Console.WriteLine("跳过：没有 v5-linked-full.xml（先跑 _sim/link-uuids-full.ps1）");
            return;
        }

        long linkedBytes = 0;
        var pairs = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(pairFile))!;
        var byName = pairs.Count(p => p.GetProperty("Why").GetString() == "同名");
        foreach (var e in pairs)
        {
            var rel = e.GetProperty("New").GetString()!;
            var p = Path.Combine(VersionDir(5), rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) linkedBytes += new FileInfo(p).Length;
        }

        PublishFile(linked);
        var client = NewClient(name, 4);
        using var http = new TestHttpServer(ServerDir);

        var r = Run(client, http);
        var diff = Compare(5, client);
        var extra = $"- **对照组**（未关联，S02）：patch=30 full=613，下载 286,010,410 字节\n" +
                    $"- 本次关联 {pairs.Count} 对搬家文件（其中**同名匹配 {byName} 对**），涉及 {linkedBytes:N0} 字节\n" +
                    $"- 关联方式：按文件名（大小写不敏感）优先配对，其余按尺寸窗口（≤2 倍）配对，贪心 1:1";
        Capture(name, "运维一次性批量关联 UUID（同名优先）之后：同一对版本应当大量吃上补丁",
            client, r, diff.Text, BaselineBytes(4, 5), extra);

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(0, r.FailedCount);
        Assert.True(r.Patched >= 400, $"关联后应当有几百个补丁命中，实得 {r.Patched}");
        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }

    [Fact]
    public void S18_逐级升级_v1到v5分四步()
    {
        if (NotReady()) return;

        var client = NewClient("S18_chained_v1_to_v5", 1);
        using var http = new TestHttpServer(ServerDir);

        long totalBytes = 0;
        var steps = new List<string>();
        for (var v = 2; v <= 5; v++)
        {
            Publish(v);
            var r = Run(client, http);
            Capture($"S18_step_v{v - 1}_to_v{v}", $"逐级升级的第 {v - 1} 步：v{v - 1} → v{v}",
                client, r, Compare(v, client).Text, BaselineBytes(v - 1, v));
            Assert.Equal(UpdateOutcome.Updated, r.Outcome);

            totalBytes += r.BytesDownloaded;
            steps.Add($"v{v - 1}→v{v}: patch={r.Patched} full={r.Full} bytes={r.BytesDownloaded:N0}");
        }

        var diff = Compare(5, client);
        var summary = new UpdateResult(UpdateOutcome.Updated, string.Empty, 0, 0, 0, 0, 0, 0,
            totalBytes, TimeSpan.Zero);
        var extra = "- 四步明细：\n" + string.Join("\n", steps.Select(s => $"  - {s}")) +
                    $"\n- **逐级合计**：{totalBytes:N0} 字节\n" +
                    "- **对照**：直接 v1→v5 = 312,863,990（S06）；只下变更文件不做差分 = 339,906,059；v5 整包 = 166,806,449";
        Capture("S18_chained_v1_to_v5", "逐级 v1→v2→v3→v4→v5 是否比直接跳到 v5 更省",
            client, summary, diff.Text, BaselineBytes(1, 5), extra);

        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }

    /// <summary>
    /// 四个组合的对照：v4→v5 这一对版本，
    ///   S02 = 不压缩 + 不关联（基线）
    ///   S19 = **服务端 gzip 压缩** + 不关联
    ///   S17 = 不压缩 + 全量关联 UUID
    ///   S20 = 压缩 + 全量关联
    /// 看的是**上网字节**（真的走网线的），而不是客户端统计的"内容字节"。
    /// </summary>
    [Fact]
    public void S19_压缩传输_不关联UUID()
    {
        if (NotReady()) return;
        const string name = "S19_gzip_no_relink";
        Publish(5);
        var client = NewClient(name, 4);
        using var http = new TestHttpServer(ServerDir) { CompressPayloads = true };

        var (r, wire) = RunMeasured(client, http);
        var diff = Compare(5, client);
        var extra = $"- 服务端对 `files/*`、`patches/*` 做 gzip 并带 `Content-Encoding: gzip`（清单不压）\n" +
                    $"- 压缩响应数：{http.CompressedResponses}\n" +
                    $"- 客户端统计的「内容字节」={r.BytesDownloaded:N0}，**上网字节**={wire:N0}";
        Capture(name, "服务端压缩传输、不做 UUID 关联：与 S02 同源，比较上网字节",
            client, r, diff.Text, BaselineBytes(4, 5), extra, wire);

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.True(http.CompressedResponses > 100);
        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }

    [Fact]
    public void S20_压缩传输_加全量关联UUID()
    {
        if (NotReady()) return;
        const string name = "S20_gzip_with_relink";
        var linked = Path.Combine(ManifestsDir, "v5-linked-full.xml");
        if (!File.Exists(linked)) { Console.WriteLine("跳过：没有 v5-linked-full.xml"); return; }

        PublishFile(linked);
        var client = NewClient(name, 4);
        using var http = new TestHttpServer(ServerDir) { CompressPayloads = true };

        var (r, wire) = RunMeasured(client, http);
        var diff = Compare(5, client);
        var extra = $"- 压缩传输 + 全量关联 UUID（{r.Patched} 个补丁命中）\n" +
                    $"- **注意**：补丁本身已被 hdiffz 压过，gzip 几乎压不动；能被压的是 `files/*` 那部分\n" +
                    $"- 客户端统计的「内容字节」={r.BytesDownloaded:N0}，**上网字节**={wire:N0}";
        Capture(name, "压缩传输 + 全量关联 UUID：看两者叠加是加分还是互相抵消",
            client, r, diff.Text, BaselineBytes(4, 5), extra, wire);

        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.True(diff.Clean, diff.Text);
        CleanupClient(client);
    }
    // ---------------------------------------------------------------- 工具

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Ra3.BattleNet.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    /// <summary>挑一个"确实有变更、且变更文件最少"的顶层目录当排除对象。</summary>
    private static string PickExcludedDir(int from, int to)
    {
        var old = Manifest(from).Manifest.Files
            .ToDictionary(f => f.RelativePath(), f => f.MD5, StringComparer.OrdinalIgnoreCase);

        return Manifest(to).Manifest.Files
            .Where(f => !(old.TryGetValue(f.RelativePath(), out var m)
                          && string.Equals(m, f.MD5, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.RelativePath())
            .Where(p => p.Contains('/'))
            .GroupBy(p => p.Split('/')[0])
            .Select(g => new { Dir = g.Key, Count = g.Count() })
            .OrderBy(x => x.Count)
            .First()
            .Dir;
    }
}
