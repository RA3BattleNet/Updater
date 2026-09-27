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

    /// <summary>
    /// 本仓库**随包提供**外部工具的平台。
    /// <list type="bullet">
    /// <item><c>win-x64</c> / <c>linux-x64</c>：v5.1.3（客户端与发布机的主场）。</item>
    /// <item><c>win-x86</c>：**v4.8.0** —— 官方 v5.1.3 发布包**没有** windows32 资产（只有 linux32），
    /// 而 x86 宿主（例如 32 位的 Desktop 宿主）的 `Rid` 就是 <c>win-x86</c>，不随包就会走到
    /// "找不到工具 → 白白下一份补丁再回落完整下载"。实测（2026-09-28）v4.8.0 的 win-x86 工具与
    /// v5.1.3 x64 生成的 <c>-c-lzma</c> 补丁**双向兼容**：x86 hpatchz 能正确应用 x64 hdiffz 的 lzma 补丁，
    /// x64 hpatchz 也能应用 x86 hdiffz 产出的补丁（含真实夹具的二进制内容对，退出码 0、哈希一致）。</item>
    /// </list>
    /// 其他平台（arm/arm64/riscv/loongarch/macos/其它 32 位）请自行放入 hdiffpatch_bin/&lt;rid&gt;/。
    /// </summary>
    public static readonly string[] ShippedRids = ["win-x64", "win-x86", "linux-x64"];

    /// <summary>
    /// 外部工具的兜底超时：**按体积缩放**（5 GB 的 ISO 做一次 <c>-s -c-lzma</c> 差分要几十分钟，
    /// 固定 5 分钟会直接把它掐死）。不是"预计耗时"，是"卡死多久算异常"。
    /// </summary>
    public static TimeSpan TimeoutFor(long oldSize, long newSize)
    {
        var minutes = 5 + (oldSize + newSize) / (1024.0 * 1024 * 1024) * 8;   // 每 GB 8 分钟
        return TimeSpan.FromMinutes(Math.Clamp(minutes, 5, 120));
    }

    /// <summary>
    /// **计划阶段**的探测入口：补丁应用工具（hpatchz）在当前环境下能不能用。
    /// 返回可执行文件路径，找不到返回 <c>null</c>。
    ///
    /// 为什么要有这个：补丁应用发生在**下载之后**，若等到那时才发现没有工具，
    /// 就已经把补丁白下了一遍（下载 → 应用失败 → 删掉补丁 → 再下完整文件，比纯完整下载还费流量）。
    /// 所以调用方应当在生成计划前先问一次，缺工具就把 <c>patch</c> 直接降级成 <c>full</c>（§7.1）。
    /// </summary>
    public static string? FindPatchTool(string toolsDir) => Find(toolsDir, "hpatchz");

    /// <summary>按 工具目录 → 工具目录/RID → 程序目录 → 程序目录/hdiffpatch_bin/RID → PATH 的顺序查找。</summary>
    public static string? Find(string toolsDir, string toolName) => Find(toolsDir, toolName, Rid);

    /// <summary>同上，但显式指定 RID（供测试与"按别的平台布局部署"的场景用）。</summary>
    internal static string? Find(string toolsDir, string toolName, string rid)
    {
        var exe = OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;
        var candidates = new[]
        {
            Path.Combine(toolsDir, exe),
            Path.Combine(toolsDir, rid, exe),
            Path.Combine(AppContext.BaseDirectory, exe),
            Path.Combine(AppContext.BaseDirectory, "tools", exe),
            Path.Combine(AppContext.BaseDirectory, "tools", rid, exe),
            Path.Combine(AppContext.BaseDirectory, "hdiffpatch_bin", rid, exe),
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
        var (ok, err) = RunAsync(toolsDir, "hdiffz", BuildDiffArgs(oldFile, newFile, patchFile),
                CancellationToken.None, Size(oldFile), Size(newFile))
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
        var (ok, err) = RunAsync(toolsDir, "hpatchz", new[] { "-s", "-f", oldFile, patchFile, outFile },
                CancellationToken.None, Size(oldFile), 0)
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

    private static long Size(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static async Task<(bool Ok, string Error)> RunAsync(
        string toolsDir, string toolName, string[] args, CancellationToken ct,
        long oldSize = 0, long newSize = 0)
    {
        var exePath = Find(toolsDir, toolName);
        if (exePath is null)
            return (false,
                $"找不到外部工具 {toolName}：当前 RID = {Rid}，本包随发的平台只有 " +
                $"{string.Join(" / ", ShippedRids)}。工具目录 = {toolsDir}。" +
                "请从 HDiffPatch 官方包把对应平台的二进制放进 hdiffpatch_bin/<rid>/；" +
                (toolName == "hpatchz"
                    ? "注意：缺 hpatchz 时**计划阶段**就会把补丁降级为完整下载（reason=patch_tool_missing），不会再浪费补丁流量。"
                    : "缺 hdiffz 时补丁生成会逐个失败并如实报错。"));

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

        var timeout = TimeoutFor(oldSize, newSize);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
            return (false, ct.IsCancellationRequested
                ? $"{toolName} 已取消"
                : $"{toolName} 超时（>{timeout.TotalMinutes:F0} 分钟）");
        }

        var so = await stdout.ConfigureAwait(false);
        var se = await stderr.ConfigureAwait(false);

        if (p.ExitCode != 0)
            return (false, $"{toolName} 退出码 {p.ExitCode}：{se.Trim()} {so.Trim()}".Trim());

        return (true, string.Empty);
    }
}
