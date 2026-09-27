using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ra3.BattleNet.Updater.Share.Utilities;

/// <summary>
/// HDiffPatch 外部工具（hdiffz / hpatchz）的定位与调用。
/// **必须**检查退出码并如实返回（AGENT.md §5.3 / §7.2：禁止吞错）。
/// 提供异步入口：补丁应用发生在客户端每文件的并发路径上，
/// 用同步 WaitForExit 会白占一个线程池线程。
/// </summary>
public static class HdiffTool
{
    public static readonly string Rid = RuntimeInformation.RuntimeIdentifier;

    public const int TimeoutMs = 300_000;

    /// <summary>按 工具目录 → 工具目录/RID → 程序目录 → 程序目录/hdiffpatch_bin/RID → PATH 的顺序查找。</summary>
    public static string? Find(string toolsDir, string toolName)
    {
        var exe = OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;
        var candidates = new[]
        {
            Path.Combine(toolsDir, exe),
            Path.Combine(toolsDir, Rid, exe),
            Path.Combine(AppContext.BaseDirectory, exe),
            Path.Combine(AppContext.BaseDirectory, "tools", exe),
            Path.Combine(AppContext.BaseDirectory, "tools", Rid, exe),
            Path.Combine(AppContext.BaseDirectory, "hdiffpatch_bin", Rid, exe),
        };

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var c = Path.Combine(dir, exe);
            if (File.Exists(c)) return c;
        }

        return null;
    }

    /// <summary>
    /// 生成补丁：<c>hdiffz [diff模式] [-c-压缩] -f old new patch</c>。
    ///
    /// 实测（v4→v5 最大的 12 个内容对、95.9 MiB 目标内容、同一个 hdiffz v4.8.0）：
    /// <code>
    ///   -s -f           70,109,124 B  (69.7% of 目标文件)   ← 原来的写法
    ///   -m -c-zstd -f   22,793,727 B  (22.7% of 目标文件)   ← 3.08 倍
    /// </code>
    /// 两处都得改，而且**客户端一行都不用动**（hpatchz 认格式头）：
    /// <list type="number">
    /// <item><c>-s</c> 在 hdiffz 里是"整个文件按**流**匹配（省内存、快）"，**不是压缩**，
    /// 而且明显比默认的 <c>-m</c> 大。只有当文件大到放不进内存时才该用 <c>-s</c>
    /// （§7.1 当初选 HDiffPatch 就是冲"大于内存的大文件"去的，所以这个判断要保留）。</item>
    /// <item>补丁**默认不压缩**（<c>-c-...</c> 的默认值是 uncompress）—— 规范里
    /// "补丁本身已被压缩、不要再压"这个假设是错的。加 <c>-c-zstd</c> 之后客户端照旧解压。</item>
    /// </list>
    /// </summary>
    public static bool Generate(string toolsDir, string oldFile, string newFile, string patchFile, out string error)
    {
        var (ok, err) = RunAsync(toolsDir, "hdiffz", BuildDiffArgs(oldFile, newFile, patchFile), CancellationToken.None)
            .GetAwaiter().GetResult();
        error = err;
        return ok;
    }

    /// <summary>`-m` 需要 <c>newSize + oldSize*5</c> 字节内存（hdiffz -h 的原文）；超出预算就退回 `-s`。</summary>
    public const long MemoryBudgetBytes = 512L * 1024 * 1024;

    private static string[] BuildDiffArgs(string oldFile, string newFile, string patchFile)
    {
        long oldSize = new FileInfo(oldFile).Length;
        long newSize = new FileInfo(newFile).Length;

        // 放得进内存就用 -m（最小补丁），否则退回 -s（流式，省内存）
        var mode = newSize + oldSize * 5 <= MemoryBudgetBytes ? "-m" : "-s";

        // 压缩器选 lzma：实测在同一批内容对上，它比 zstd **又小又快**
        //（21.5% / 8.2s  vs  23.5% / 12.9s，抽样最大 6 对、70 MiB 目标内容）。
        // 调级别没用：-c-lzma 与 -c-lzma-9 输出完全一样（默认 7 已饱和）。
        return [mode, "-c-lzma", "-f", oldFile, newFile, patchFile];
    }

    /// <summary>应用补丁（同步入口，离线补丁包链路用）。</summary>
    public static bool Apply(string toolsDir, string oldFile, string patchFile, string outFile, out string error)
    {
        var (ok, err) = RunAsync(toolsDir, "hpatchz", new[] { "-s", "-f", oldFile, patchFile, outFile }, CancellationToken.None)
            .GetAwaiter().GetResult();
        error = err;
        return ok;
    }

    /// <summary>应用补丁（异步入口，客户端并发路径用）。</summary>
    public static async Task<bool> ApplyAsync(
        string toolsDir, string oldFile, string patchFile, string outFile, CancellationToken ct)
    {
        var (ok, _) = await RunAsync(toolsDir, "hpatchz",
            new[] { "-s", "-f", oldFile, patchFile, outFile }, ct).ConfigureAwait(false);
        return ok;
    }

    private static async Task<(bool Ok, string Error)> RunAsync(
        string toolsDir, string toolName, string[] args, CancellationToken ct)
    {
        var exePath = Find(toolsDir, toolName);
        if (exePath is null)
            return (false, $"找不到外部工具 {toolName}（工具目录: {toolsDir}）");

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p is null)
            return (false, $"无法启动 {exePath}");

        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);

        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
            return (false, ct.IsCancellationRequested
                ? $"{toolName} 已取消"
                : $"{toolName} 超时（>{TimeoutMs}ms）");
        }

        var so = await stdout.ConfigureAwait(false);
        var se = await stderr.ConfigureAwait(false);

        if (p.ExitCode != 0)
            return (false, $"{toolName} 退出码 {p.ExitCode}：{se.Trim()} {so.Trim()}".Trim());

        return (true, string.Empty);
    }
}
