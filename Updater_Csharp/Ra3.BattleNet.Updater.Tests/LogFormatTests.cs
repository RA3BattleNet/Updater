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
        var fLines = lines.Where(l => l.StartsWith("F\t", StringComparison.Ordinal)).ToList();
        var rLines = lines.Where(l => l.StartsWith("R\t", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, fLines.Count);
        Assert.Single(rLines);

        // 列数写死：F=13 列、R=14 列（requests / wire 都是追加在行尾的列）
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
            Assert.True(long.TryParse(c[12], out _));      // wire（真正走网线的字节）
        }

        var r = rLines[0].Split('\t');
        Assert.Matches("^[0-9a-f]{32}$", r[2]);            // remote_manifest_hash
        Assert.Equal(result.Total, int.Parse(r[3]));
        Assert.Equal(result.Skipped, int.Parse(r[4]));
        Assert.Equal(result.Patched, int.Parse(r[6]));
        Assert.Equal(result.Full, int.Parse(r[7]));
        Assert.Equal(result.BytesDownloaded, long.Parse(r[9]));
        Assert.Equal("Updated", r[11]);
        Assert.True(int.Parse(r[12]) > 0, "requests 列必须被填写（F8）");
        Assert.True(long.Parse(r[13]) > 0, "wire 列必须被填写（真正走网线的字节）");
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
