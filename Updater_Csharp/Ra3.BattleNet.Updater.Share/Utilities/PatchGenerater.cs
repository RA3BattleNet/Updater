using Ra3.BattleNet.Updater.Share.Log;

namespace Ra3.BattleNet.Updater.Share.Utilities;

/// <summary>
/// 生成 HDiffPatch 补丁。
/// **必须**检查外部进程退出码并如实返回（AGENT.md §5.3：禁止吞错）。
/// </summary>
public class PatchGenerater
{
    public static bool GeneratePatch(string oldFile, string newFile, string deltaFile)
    {
        var toolsDir = AppContext.BaseDirectory;
        if (HdiffTool.Generate(toolsDir, oldFile, newFile, deltaFile, out var error))
            return true;

        Logger.Fail($"生成补丁失败：{error}{Environment.NewLine}");
        return false;
    }
}
