using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ra3.BattleNet.Updater.Share.Utilities;

/// <summary>
/// HDiffPatch 外部工具（hdiffz / hpatchz）的定位与调用。
/// **必须**检查退出码并如实返回（AGENT.md §5.3 / §7.2：禁止吞错）。
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

    /// <summary>生成补丁：hdiffz -s -f &lt;old&gt; &lt;new&gt; &lt;patch&gt;</summary>
    public static bool Generate(string toolsDir, string oldFile, string newFile, string patchFile, out string error)
        => Run(toolsDir, "hdiffz", new[] { "-s", "-f", oldFile, newFile, patchFile }, out error);

    /// <summary>应用补丁：hpatchz -s -f &lt;old&gt; &lt;patch&gt; &lt;out&gt;</summary>
    public static bool Apply(string toolsDir, string oldFile, string patchFile, string outFile, out string error)
        => Run(toolsDir, "hpatchz", new[] { "-s", "-f", oldFile, patchFile, outFile }, out error);

    private static bool Run(string toolsDir, string toolName, string[] args, out string error)
    {
        error = string.Empty;

        var exePath = Find(toolsDir, toolName);
        if (exePath is null)
        {
            error = $"找不到外部工具 {toolName}（工具目录: {toolsDir}）";
            return false;
        }

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
        {
            error = $"无法启动 {exePath}";
            return false;
        }

        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit(TimeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
            error = $"{toolName} 超时（>{TimeoutMs}ms）";
            return false;
        }

        var so = stdout.GetAwaiter().GetResult();
        var se = stderr.GetAwaiter().GetResult();

        if (p.ExitCode != 0)
        {
            error = $"{toolName} 退出码 {p.ExitCode}：{se.Trim()} {so.Trim()}".Trim();
            return false;
        }

        return true;
    }
}
