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

    /// <summary>生成补丁：hdiffz -s -f &lt;old&gt; &lt;new&gt; &lt;patch&gt;（服务端发布流水线用，同步即可）</summary>
    public static bool Generate(string toolsDir, string oldFile, string newFile, string patchFile, out string error)
    {
        var (ok, err) = RunAsync(toolsDir, "hdiffz", new[] { "-s", "-f", oldFile, newFile, patchFile }, CancellationToken.None)
            .GetAwaiter().GetResult();
        error = err;
        return ok;
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
