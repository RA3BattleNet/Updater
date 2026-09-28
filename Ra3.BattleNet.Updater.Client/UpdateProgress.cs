namespace Ra3.BattleNet.Updater.Client;

/// <summary>进度上报：分母是**待更新文件数**（AGENT.md §4.7）。</summary>
public sealed record UpdateProgress(int Current, int Total, string FileName, string Stage);

public static class UpdateStage
{
    public const string Check = "check";
    public const string Move = "move";
    public const string Patch = "patch";
    public const string Download = "download";
    public const string Done = "done";

    /// <summary>暂存更新的落地阶段（applier，§12.5）。</summary>
    public const string Apply = "apply";
}
