using System.Text;
using Ra3.BattleNet.Updater.Server;
using CoreUpdater = Ra3.BattleNet.Updater.Core.Updater;
using Ra3.BattleNet.Updater.Core;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// 日志格式契约（AGENT.md §4.11）：UTF-8 无 BOM、换行 LF、分隔符 TAB、字段列数固定。
/// 分析脚本靠这个格式统计增量命中率，一次"顺手改成 CRLF"就会让所有脚本失效。
/// </summary>
public class LogFormatTests
{
    [Fact]
    public void Log_UsesLfTabAndNoBom_WithStableColumnCounts()
    {
        using var tmp = new TempDir();

        var v = tmp.Sub("v");
        TestSupport.WriteTree(v, ("bin/a.dll", TestSupport.Big("A")), ("bin/b.dll", TestSupport.Big("B")));

        var m = Path.Combine(tmp.Path, "v.xml");
        ManifestGenerator.Generate(v, null, []).Manifest.SaveToXml(m);

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m, v, [], server);
        File.Copy(m, Path.Combine(server, "manifest.xml"), overwrite: true);

        var client = tmp.Sub("client");
        using var http = new TestHttpServer(server);
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
        }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);

        var logPath = Path.Combine(client, "UpdaterCache", "update.log");
        var bytes = File.ReadAllBytes(logPath);

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "日志不得带 UTF-8 BOM");

        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("\r", text);                 // 必须是 LF，不是 CRLF
        Assert.EndsWith("\n", text);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var sLines = lines.Where(l => l.StartsWith("S\t", StringComparison.Ordinal)).ToList();
        var fLines = lines.Where(l => l.StartsWith("F\t", StringComparison.Ordinal)).ToList();
        var rLines = lines.Where(l => l.StartsWith("R\t", StringComparison.Ordinal)).ToList();

        // 每轮开始一行 S（run_id + UTC 时间）：一次进程里可能跑两轮（取消 + 重跑），
        // 它们的 F/R 行混在同一个文件里，分析脚本必须靠它分组（§4.11）。
        Assert.Single(sLines);
        Assert.Equal(3, sLines[0].Split('\t').Length);
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}T", sLines[0].Split('\t')[2]);

        Assert.Equal(2, fLines.Count);
        Assert.Single(rLines);

        // 列数写死：F=13 列、R=14 列（requests / payload 都是追加在行尾的列）
        Assert.All(fLines, l => Assert.Equal(13, l.Split('\t').Length));
        Assert.Equal(14, rLines[0].Split('\t').Length);

        // action / status 是封闭集合
        var actions = new[] { "skip", "move", "patch", "full" };
        var statuses = new[] { "0", "1", "2", "3", "4", "5", "9" };
        foreach (var l in fLines)
        {
            var c = l.Split('\t');
            Assert.Contains(c[7], actions);
            Assert.Contains(c[8], statuses);
            Assert.Matches("^[0-9a-f]{32}$", c[2]);        // uuid
            Assert.Matches("^[0-9a-f]{32}$", c[5]);        // new_md5
            Assert.True(long.TryParse(c[10], out _));      // bytes
            Assert.True(long.TryParse(c[11], out _));      // ms
            Assert.True(long.TryParse(c[12], out _));      // payload（响应正文里读到的字节）

            // 每一行 full 都必须带 reason（"为什么没走增量"是这条日志的全部意义，
            // 空的 full 行等于把命中率分析的手脚砍掉 —— I-3）
            if (c[7] == "full") Assert.False(string.IsNullOrEmpty(c[9]), "full 行的 reason 列不得为空");
        }

        // 这两个文件都是完整下载，payload 列必须真的被填上（不是"能解析成数字"就算过）
        Assert.All(fLines.Where(l => l.Split('\t')[7] == "full"),
            l => Assert.True(long.Parse(l.Split('\t')[12]) > 0));

        var r = rLines[0].Split('\t');
        Assert.Matches("^[0-9a-f]{32}$", r[2]);            // remote_manifest_hash
        Assert.Equal(result.Total, int.Parse(r[3]));
        Assert.Equal(result.Skipped, int.Parse(r[4]));
        Assert.Equal(result.Patched, int.Parse(r[6]));
        Assert.Equal(result.Full, int.Parse(r[7]));
        Assert.Equal(result.BytesDownloaded, long.Parse(r[9]));
        Assert.Equal("Updated", r[11]);
        Assert.True(int.Parse(r[12]) > 0, "requests 列必须被填写（F8）");
        Assert.True(long.Parse(r[13]) > 0, "payload 列必须被填写（响应正文读取字节）");

        // 口径：payload 是"从响应正文里读到的字节"，比内容字节多出清单那一份（这里没有传输压缩）。
        // 它**不是**网线字节（§4.11、OPEN_ISSUES M-1）：真要算带宽得看边缘出口统计。
        Assert.True(long.Parse(r[13]) >= long.Parse(r[9]),
            $"payload({r[13]}) 至少要覆盖内容字节({r[9]}) —— 清单也算在里面");
    }

    /// <summary>
    /// payload 列的口径（I-2）：它数的是**从响应正文里读到的字节**，所以
    /// ① 404 这种错误响应"服务端声明的正文长度"要计入（我们提前中断、未必真收全，但它是真实流量）；
    /// ② 304 是 0（零正文）。
    /// 这两条合起来把 payload 钉在"可见载荷"上，而不是"网线字节"（后者只有边缘出口统计能给，见 M-1）。
    /// </summary>
    [Fact]
    public void PayloadColumn_CountsDeclaredErrorBodies_AndIsZeroOn304()
    {
        using var tmp = new TempDir();

        var v1 = tmp.Sub("v1");
        TestSupport.WriteTree(v1, ("bin/a.dll", TestSupport.Big("A")), ("bin/b.dll", TestSupport.Big("B")));
        var v2 = tmp.Sub("v2");
        TestSupport.WriteTree(v2, ("bin/a.dll", TestSupport.Big("A2")), ("bin/b.dll", TestSupport.Big("B2")));

        var m1 = Path.Combine(tmp.Path, "v1.xml");
        ManifestGenerator.Generate(v1, null, []).Manifest.SaveToXml(m1);
        var m2 = Path.Combine(tmp.Path, "v2.xml");
        ManifestGenerator.Generate(v2, null, []).Manifest.SaveToXml(m2);

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m2, v2, [], server);
        File.Copy(m2, Path.Combine(server, "manifest.xml"), overwrite: true);

        // 客户端停在 v1 → 每个文件都有"本地前身"，于是会去探补丁；服务端一律 404
        var client = tmp.Sub("client");
        TestSupport.CopyTree(v1, client);
        File.Copy(m1, Path.Combine(client, "manifest.xml"), overwrite: true);

        const int notFoundBody = 30_000;   // 与 CF 实测的 404 页面同量级
        using var http = new TestHttpServer(server) { PatchNotFound = true, NotFoundBodyBytes = notFoundBody };
        var cfg = new UpdateConfig { RootPath = client, ManifestUrl = http.BaseUrl + "manifest.xml" };

        Assert.Equal(UpdateOutcome.Updated, new CoreUpdater(cfg).Run().Outcome);
        Assert.True(http.NotFound > 0, "应当真的探过补丁，否则这条测试没测到错误正文");

        var first = LastRLine(client);
        Assert.True(long.Parse(first[13]) >= long.Parse(first[9]) + (http.NotFound * (long)notFoundBody),
            $"404 的声明正文必须计入 payload：payload={first[13]}, 内容字节={first[9]}, 404×{http.NotFound}×{notFoundBody}");

        // 再跑一轮：本地已是最新 → 清单走 304（零正文），不该再产生任何 payload
        new CoreUpdater(cfg).Run();
        Assert.True(http.NotModified > 0, "第二轮应当命中清单 304");

        var second = LastRLine(client);
        Assert.Equal(0, long.Parse(second[13]));

        // 两轮跑在同一个 update.log 里（I-4）：靠每轮开头的 S 行才分得开两轮的账，
        // 否则 F/R 行混在一起，命中率会被算错。
        var all = File.ReadAllText(Path.Combine(client, "UpdaterCache", "update.log"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var runStarts = all.Where(l => l.StartsWith("S\t", StringComparison.Ordinal))
            .Select(l => l.Split('\t')[1]).ToList();
        var runEnds = all.Where(l => l.StartsWith("R\t", StringComparison.Ordinal))
            .Select(l => l.Split('\t')[1]).ToList();
        Assert.Equal(2, runStarts.Count);
        Assert.Equal(runStarts, runEnds);                  // 每轮一 S 一 R，run_id 一一对应
        // 同一个进程里跑两轮也必须拿到**不同**的 run_id —— 否则按 run_id 分组这条路根本走不通
        Assert.Equal(2, runStarts.Distinct().Count());
    }

    private static string[] LastRLine(string clientRoot)
    {
        var text = File.ReadAllText(Path.Combine(clientRoot, "UpdaterCache", "update.log"));
        var rLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("R\t", StringComparison.Ordinal))
            .ToList();
        return rLines[^1].Split('\t');
    }

    [Fact]
    public void OversizedLog_IsRotated_InsteadOfGrowingForever()
    {
        using var tmp = new TempDir();

        var v = tmp.Sub("v");
        TestSupport.WriteTree(v, ("a.bin", TestSupport.Big("A")));

        var m = Path.Combine(tmp.Path, "v.xml");
        ManifestGenerator.Generate(v, null, []).Manifest.SaveToXml(m);

        var server = tmp.Sub("server");
        PatchGenerator.Generate(m, v, [], server);
        File.Copy(m, Path.Combine(server, "manifest.xml"), overwrite: true);

        var client = tmp.Sub("client");
        var cacheDir = Path.Combine(client, "UpdaterCache");
        Directory.CreateDirectory(cacheDir);

        // 上一轮留下的日志已经"很大"（阈值调小，免得测试真写 8 MB）
        var logPath = Path.Combine(cacheDir, "update.log");
        File.WriteAllText(logPath, new string('#', 400));

        using var http = new TestHttpServer(server);
        var result = new CoreUpdater(new UpdateConfig
        {
            RootPath = client,
            ManifestUrl = http.BaseUrl + "manifest.xml",
            MaxLogBytes = 200,
        }).Run();

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);

        var rotated = logPath + ".1";
        Assert.True(File.Exists(rotated), "超过阈值时必须轮转出 update.log.1");
        Assert.Equal(400, new FileInfo(rotated).Length);
        Assert.True(new FileInfo(logPath).Length < 400, "当前日志应当是新一轮的内容");
        Assert.Contains("R\t", File.ReadAllText(logPath));
    }
}
