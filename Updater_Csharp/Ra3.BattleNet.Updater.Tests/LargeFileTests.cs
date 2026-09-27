using System.Diagnostics;
using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;
using CoreUpdater = Ra3.BattleNet.Updater.Core.Updater;
using Ra3.BattleNet.Updater.Core;

namespace Ra3.BattleNet.Updater.Tests;

/// <summary>
/// **大文件专项**：3 个 Windows ISO（4~6 GB）走完整链路。
/// 需要 UPDATER_ISO_TREES 指向放着 ISO 的目录（默认 test/）；未设置时不做任何事。
///
/// 目的不是"ISO 能不能增量"（ISO 里是已压缩的 WIM，本来就不指望），而是把大文件这条路上
/// 的**功能**压出来：
/// <list type="bullet">
/// <item>`-m` 的内存规则会不会正确退回 `-s`（5 GB 文件用 `-m` 要 ~30 GB 内存）；</item>
/// <item>工具超时（按体积缩放到 120 分钟）、客户端停滞超时（不受大小影响）够不够；</item>
/// <item>客户端在大文件上的下载/校验/落盘是否正常，最终是否**逐字节一致**；</item>
/// <item>补丁到底有没有用：补丁 ≥ 目标文件时必须被**弃用**并回落完整下载。</item>
/// </list>
/// </summary>
public class LargeFileTests
{
    private static string Root => Environment.GetEnvironmentVariable("UPDATER_ISO_TREES") ?? string.Empty;

    private static bool NotReady()
    {
        if (Root.Length == 0 || !Directory.Exists(Root)) { Console.WriteLine("跳过：未设置 UPDATER_ISO_TREES"); return true; }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newLink, string existingFile, IntPtr reserved);

    private const string IsoA = "cn_windows_10_consumer_editions_version_1909_updated_jan_2020_x64_dvd_47161f17.iso";
    private const string IsoB = "cn_windows_10_consumer_editions_version_20h2_x64_dvd_d4f7a83e.iso";
    private const string IsoC = "cn_windows_10_consumer_editions_version_20h2_x86_dvd_a66fccd6.iso";

    /// <summary>用硬链接把 ISO 挂进一个"版本目录"（省 5 GB 拷贝；这些目录只读）。</summary>
    private static void LinkAsVersion(string srcIso, string versionDir)
    {
        Directory.CreateDirectory(versionDir);
        var dst = Path.Combine(versionDir, "payload.iso");
        if (File.Exists(dst)) File.Delete(dst);
        // 硬链接（同卷免拷贝；.NET 没有公开 API，直接 P/Invoke）；失败就老实复制
        if (!CreateHardLinkW(dst, srcIso, IntPtr.Zero))
            File.Copy(srcIso, dst, overwrite: true);
    }

    private sealed record PatchProbe(long OldBytes, long NewBytes, long PatchBytes, long DiffMs, long ResaveMs, bool Kept)
    {
        public double Ratio => NewBytes > 0 ? (double)PatchBytes / NewBytes : 0;
        public string Text =>
            $"旧 {OldBytes:N0} B / 新 {NewBytes:N0} B → 补丁 {PatchBytes:N0} B（占新文件 {Ratio:P1}），" +
            $"差分 {DiffMs / 1000.0:F1}s" + (ResaveMs > 0 ? $" + 压缩 {ResaveMs / 1000.0:F1}s" : string.Empty) +
            $"，判定：{(Kept ? "保留（客户端会走补丁）" : "**弃用**（补丁不小于目标文件 → 客户端完整下载）")}";
    }

    /// <summary>直接用 hdiffz 探一遍补丁大小（不走 PatchGenerator，便于分别计时"差分"与"压缩"两步）。</summary>
    private static PatchProbe Probe(string oldPath, string newPath, string tmp, bool withLzma)
    {
        var tools = AppContext.BaseDirectory;
        var hdiffz = HdiffTool.Find(tools, "hdiffz") ?? throw new InvalidOperationException("找不到 hdiffz");
        var raw = Path.Combine(tmp, "a.hdiff");
        var resaved = Path.Combine(tmp, "b.hdiff");

        var psi = new ProcessStartInfo(hdiffz) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-s", "-f", oldPath, newPath, raw }) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using (var p = Process.Start(psi)!) { p.WaitForExit(); }
        sw.Stop();
        if (!File.Exists(raw)) throw new InvalidOperationException("hdiffz 未产出补丁");
        var rawSize = new FileInfo(raw).Length;

        long resaveMs = 0;
        if (withLzma)
        {
            var psi2 = new ProcessStartInfo(hdiffz) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-c-lzma", raw, resaved }) psi2.ArgumentList.Add(a);
            var sw2 = Stopwatch.StartNew();
            using (var p = Process.Start(psi2)!) { p.WaitForExit(); }
            sw2.Stop();
            resaveMs = sw2.ElapsedMilliseconds;
        }

        var size = withLzma && File.Exists(resaved) ? new FileInfo(resaved).Length : rawSize;
        var probe = new PatchProbe(new FileInfo(oldPath).Length, new FileInfo(newPath).Length, size,
            sw.ElapsedMilliseconds, resaveMs, size < new FileInfo(newPath).Length);
        try { File.Delete(raw); File.Delete(resaved); } catch { /* 尽力 */ }
        return probe;
    }

    private static void WriteArtifact(string name, string text)
    {
        var dir = Path.Combine(Root, "..", "_sim", "logs", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "scenario.md"), text);
        Console.WriteLine(text);
    }

    /// <summary>正例：同日冕 x64 的 1909 → 20h2，走完整链路（清单 → 补丁 → 客户端）。</summary>
    [Fact]
    public void ISO_1_x64_1909_to_20h2_EndToEnd()
    {
        if (NotReady()) return;
        using var tmp = new TempDir();
        var oldIso = Path.Combine(Root, IsoA);
        var newIso = Path.Combine(Root, IsoB);
        Assert.True(File.Exists(oldIso) && File.Exists(newIso), $"缺 ISO：{oldIso} / {newIso}");

        var vOld = tmp.Sub("v_old");
        var vNew = tmp.Sub("v_new");
        LinkAsVersion(oldIso, vOld);
        LinkAsVersion(newIso, vNew);

        var mOld = Path.Combine(tmp.Path, "old.xml");
        ManifestGenerator.Generate(vOld, null, []).Manifest.SaveToXml(mOld);
        var mNew = Path.Combine(tmp.Path, "new.xml");
        ManifestGenerator.Generate(vNew, mOld, [], oldRoot: vOld).Manifest.SaveToXml(mNew);

        var probe = Probe(Path.Combine(vOld, "payload.iso"), Path.Combine(vNew, "payload.iso"), tmp.Path,
            withLzma: Environment.GetEnvironmentVariable("UPDATER_ISO_LZMA") == "1");

        // 服务端产物（补丁按真实流水线生成；补丁 ≥ 目标文件时会被弃用）
        var server = tmp.Sub("server");
        var swGen = Stopwatch.StartNew();
        var summary = PatchGenerator.Generate(mNew, vNew, [new Baseline(mOld, vOld)], server, minFileSize: 0);
        swGen.Stop();
        File.Copy(mNew, Path.Combine(server, "manifest.xml"), overwrite: true);

        // 客户端：从旧版出发
        var client = tmp.Sub("client");
        TestSupport.CopyTree(vOld, client);
        File.Copy(mOld, Path.Combine(client, "manifest.xml"), overwrite: true);

        using var http = new TestHttpServer(server);
        var sw = Stopwatch.StartNew();
        var result = new CoreUpdater(new UpdateConfig { RootPath = client, ManifestUrl = http.BaseUrl + "manifest.xml" }).Run();
        sw.Stop();

        var exact = TestSupport.Md5File(Path.Combine(vNew, "payload.iso")) == TestSupport.Md5File(Path.Combine(client, "payload.iso"));
        var text = $"""
            # ISO_1：x64 1909 → 20h2（正例，走完整链路）

            - 探针（`-s` 差分）：{probe.Text}
            - PatchGenerator：{summary.PatchesCreated} 个补丁 / {summary.PatchBytes:N0} B，耗时 {swGen.Elapsed.TotalSeconds:F1}s
            - 客户端：{result}
            - 客户端耗时 {sw.Elapsed.TotalSeconds:F1}s；请求数 {http.Requests}
            - 结果逐字节一致：{exact}
            - 磁盘/内存提示：本用例走 `-s`（`-m` 需要新+旧×5 ≈ 32 GB 内存，必须退回流式）
            """;
        WriteArtifact("ISO_1_x64_1909_to_20h2", text);

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.True(exact, "更新后的 ISO 必须与目标版本逐字节一致");
    }

    /// <summary>负例：x64 20h2 → x86 20h2（内容基本无关，看补丁会不会被正确弃用）。</summary>
    [Fact]
    public void ISO_2_x64_to_x86_NegativeControl()
    {
        if (NotReady()) return;
        using var tmp = new TempDir();
        var vOld = tmp.Sub("v_old");
        var vNew = tmp.Sub("v_new");
        LinkAsVersion(Path.Combine(Root, IsoB), vOld);
        LinkAsVersion(Path.Combine(Root, IsoC), vNew);

        var probe = Probe(Path.Combine(vOld, "payload.iso"), Path.Combine(vNew, "payload.iso"), tmp.Path,
            withLzma: Environment.GetEnvironmentVariable("UPDATER_ISO_LZMA") == "1");
        var text = $"""
            # ISO_2：x64 20h2 → x86 20h2（负例，跨架构）

            - {probe.Text}
            """;
        WriteArtifact("ISO_2_x64_to_x86", text);
    }

    /// <summary>上界例：把旧 ISO 复制一份、只改中间 1 个字节（补丁应当极小）。</summary>
    [Fact]
    public void ISO_3_one_byte_change_IsTheUpperBound()
    {
        if (NotReady()) return;
        using var tmp = new TempDir();
        var vOld = tmp.Sub("v_old");
        var vNew = tmp.Sub("v_new");
        LinkAsVersion(Path.Combine(Root, IsoA), vOld);
        Directory.CreateDirectory(vNew);

        var dst = Path.Combine(vNew, "payload.iso");
        File.Copy(Path.Combine(vOld, "payload.iso"), dst);
        using (var fs = new FileStream(dst, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(fs.Length / 2, SeekOrigin.Begin);
            var b = fs.ReadByte();
            fs.Seek(-1, SeekOrigin.Current);
            fs.WriteByte((byte)(b ^ 0xFF));
        }

        var probe = Probe(Path.Combine(vOld, "payload.iso"), dst, tmp.Path,
            withLzma: Environment.GetEnvironmentVariable("UPDATER_ISO_LZMA") == "1");
        var text = $"""
            # ISO_3：同一个 ISO 只改中间 1 个字节（理论上界）

            - {probe.Text}
            """;
        WriteArtifact("ISO_3_one_byte", text);
    }
    /// <summary>
    /// **客户端大文件路径**：5.63 GB 完整下载，并在传输中人为截断 1 字节 → 强制走 Range 续传。
    /// 这条用例不生成补丁（那一步对 ISO 又慢又没用），专测：停滞超时够不够、`.part` 续传、
    /// 5.6 GB 的哈希校验、落盘原子性、以及最终逐字节一致。
    /// </summary>
    [Fact]
    public void ISO_4_large_full_download_WithForcedResume()
    {
        if (NotReady()) return;
        using var tmp = new TempDir();
        var newIso = Path.Combine(Root, IsoB);
        Assert.True(File.Exists(newIso), $"缺 ISO：{newIso}");

        var vNew = tmp.Sub("v_new");
        LinkAsVersion(newIso, vNew);

        var mNew = Path.Combine(tmp.Path, "new.xml");
        var gen = ManifestGenerator.Generate(vNew, null, []);
        gen.Manifest.SaveToXml(mNew);

        // 只产出完整文件（不给基线 → 不生成补丁）
        var server = tmp.Sub("server");
        var swServer = Stopwatch.StartNew();
        PatchGenerator.Generate(mNew, vNew, [], server, minFileSize: 0);
        swServer.Stop();
        File.Copy(mNew, Path.Combine(server, "manifest.xml"), overwrite: true);

        // 客户端从"空目录"开始：必然完整下载
        var client = tmp.Sub("client");

        using var http = new TestHttpServer(server) { TruncateBytes = 1 };   // 每次响应少发 1 字节 → 逼出续传
        var sw = Stopwatch.StartNew();
        var result = new CoreUpdater(new UpdateConfig { RootPath = client, ManifestUrl = http.BaseUrl + "manifest.xml" }).Run();
        sw.Stop();

        var isoFile = Path.Combine(client, "payload.iso");
        var exact = File.Exists(isoFile) && TestSupport.Md5File(isoFile) == TestSupport.Md5File(newIso);
        var text = $"""
            # ISO_4：5.63 GB 完整下载 + 强制 Range 续传（客户端大文件路径）

            - 目标文件：{new FileInfo(newIso).Length:N0} B（{new FileInfo(newIso).Length / 1024.0 / 1024 / 1024:F2} GiB）
            - 服务端准备（复制成内容寻址 blob）：{swServer.Elapsed.TotalSeconds:F1}s
            - 客户端：{result}
            - 客户端耗时 {sw.Elapsed.TotalSeconds:F1}s；请求数 {http.Requests}（截断 1 字节 → 必然有一次续传）
            - 上网字节 {result.WireDownloaded:N0} B（含清单；续传那一次只补最后 1 字节）
            - 结果逐字节一致：{exact}
            """;
        WriteArtifact("ISO_4_large_full_download", text);

        Assert.Equal(UpdateOutcome.Updated, result.Outcome);
        Assert.Equal(0, result.FailedCount);
        Assert.True(exact, "5.6 GB 下载后必须逐字节一致");
        Assert.True(result.WireDownloaded >= new FileInfo(newIso).Length, "上网字节应当至少等于文件大小");
    }
}
