namespace Ra3.BattleNet.Updater.Share.Models;

/// <summary>
/// 路径信任边界（AGENT.md §4.14）：**manifest 是远端数据**，由它派生出来的相对路径必须落在安装根内。
/// 拒绝：绝对路径、盘符（含 ADS 的 `:`）、UNC、任何 `..` 段、以及 `\\?\` 之类的前缀注入。
/// 判据看的是**定义**而不是最终解析结果 —— `sub/../../x` 即使能解析回根内，也一律拒绝。
/// </summary>
public static class PathSafety
{
    // Windows 文件名里不可能合法出现的字符。`:` 顺带挡掉盘符与 ADS；`?` / `*` 挡掉通配与 `\\?\` 注入。
    private static readonly char[] Illegal = [':', '?', '*', '"', '<', '>', '|', '\0'];

    /// <summary>这条（由清单 / 计划派生出来的）相对路径是否安全。</summary>
    public static bool IsSafeRelative(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return false;
        if (relative.IndexOfAny(Illegal) >= 0) return false;
        if (relative[0] is '/' or '\\') return false;          // 绝对路径 / UNC
        foreach (var seg in relative.Split('/', '\\'))
            if (seg == "..") return false;                     // 逃出安装根
        return true;
    }

    /// <summary>返回第一条不安全的路径；全都安全则返回 <c>null</c>。</summary>
    public static string? FirstUnsafe(IEnumerable<string?> relativePaths)
    {
        foreach (var p in relativePaths)
            if (!IsSafeRelative(p)) return p;
        return null;
    }
}