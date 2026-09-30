using Ra3.BattleNet.Updater.Client;

namespace Ra3.BattleNet.Updater.Applier;

/// <summary>
/// 落地进程的**专用入口**：只做一件事 —— 把阶段一暂存好的那一版换上去（AGENT.md §12.5 阶段二/三）。
///
/// 【必须】只认落地阶段的参数，而且**宽松**（不认识的忽略、不报错）：现场这个 exe 可能还是旧的
/// （它很少被更新、又必须能被更新），而参数是新版库生成的 —— 它必须能吃下自己不认识的新参数。
/// 【必须】不创建窗口、不做 UI、不联网、不打补丁。窗口是宿主的事；库只负责给出事实字段
/// （`IProgress&lt;UpdateProgress&gt;` 的阶段/计数/文件名/等待时长）。
/// 【禁止】把 CLI 那套功能搬进来：这是**给客户端用的落地入口**，不是第二个 `Client.CLI`。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        ApplierConfig cfg;
        try
        {
            cfg = ApplierConfig.FromArgs(args, UnknownArgPolicy.Ignore);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"applier 参数错误：{ex.Message}");
            return 2;
        }

        UpdateResult result;
        try
        {
            result = new StagedApplier(cfg).Run();
        }
        catch (Exception ex)
        {
            // 库本身不抛异常；这里兜住配置/环境层面的意外（退出码与 Failed 同）
            Console.Error.WriteLine($"落地过程异常：{ex.Message}");
            return 1;
        }

        // 成功**不给任何输出**（stdout 不是本 exe 的契约）：没有待提交的计划时同样静默退出 0。
        // 失败留一行到 stderr 便于人工看；机器可读的结论在 update.log 的 C 行（result/reason/detail）与 R 行。
        if (!result.Applied)
            Console.Error.WriteLine($"落地未完成：{result.Outcome} reason={result.Reason} {result.Detail}");

        return result.Applied ? 0 : 1;
    }
}