namespace Ra3.BattleNet.Updater.Client;

/// <summary>
/// 删除前的"链接穿透"护栏（AGENT.md §4.14 的**补集**，见 `DELETE_DESIGN.md` §9）。
///
/// 路径逃逸（`..`、绝对路径、盘符、UNC）由 <see cref="Ra3.BattleNet.Updater.Share.Models.PathSafety"/>
/// 在整轮开始前拦掉 —— 它看的是**字符串**。但 `<root>\data\link\foo.dll` 这种路径在字符串上完全合法，
/// 而 `<root>\data\link` 若是 junction / 符号链接，改名或删除就会**穿透**到安装根之外去动文件。
/// 清单本身是干净的（发布树里没有链接），逃逸来自客户端磁盘状态，所以只能在这里补。
///
/// 每个会话一个实例：缓存只在本次会话内有效（跨会话可能"目录已变成链接"而缓存还是旧的）。
/// 只查**根以内**的层级 —— 根本身是链接属于宿主的部署选择，不算逃逸。
/// </summary>
internal sealed class ReparseGuard
{
    private readonly string _root;
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hit = new(StringComparer.OrdinalIgnoreCase);

    public ReparseGuard(string rootPath) =>
        _root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>父目录链上是否存在 junction / 符号链接。查不出来（目录不存在等）当"不是"。</summary>
    public bool HasLinkAncestor(string filePath)
    {
        for (var dir = Path.GetDirectoryName(filePath); dir is not null && dir.Length > _root.Length;
             dir = Path.GetDirectoryName(dir))
        {
            if (_hit.Contains(dir)) return true;
            if (_checked.Contains(dir)) continue;
            _checked.Add(dir);

            bool isLink;
            try { isLink = (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0; }
            catch { isLink = false; }
            if (isLink) { _hit.Add(dir); return true; }
        }
        return false;
    }
}