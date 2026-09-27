using Ra3.BattleNet.Updater.Share.Log;

namespace Ra3.BattleNet.Updater.Share.Utilities;

/// <summary>
/// 应用 HDiffPatch 补丁。
/// **必须**检查外部进程退出码并如实返回（AGENT.md §7.2：禁止吞错）。
/// </summary>
public class PatchApplyer
{
    /// <param name="oldFile">旧文件路径</param>
    /// <param name="diffFile">补丁文件路径</param>
    /// <param name="outNewPath">新文件输出路径</param>
    public static bool ApplyPatch(string oldFile, string diffFile, string outNewPath)
    {
        var toolsDir = AppContext.BaseDirectory;
        if (HdiffTool.Apply(toolsDir, oldFile, diffFile, outNewPath, out var error))
            return true;

        Logger.Fail($"应用补丁失败：{error}{Environment.NewLine}");
        return false;
    }
}
