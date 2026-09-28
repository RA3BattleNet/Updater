using System.Text.Json;
using Ra3.BattleNet.Updater.Client;
using ClientUpdater = Ra3.BattleNet.Updater.Client.Updater;

namespace Ra3.BattleNet.Updater.Client.CLI;

/// <summary>
/// 独立进程壳：跑一次更新并给出结构化结果（AGENT.md §4.12）。
/// 退出码：0 = 已最新 / 已更新 / 已落地；3 = 已暂存待落地（暂存模式，宿主退出后生效）；1 = 需要完整包 / 失败；2 = 参数或配置错误。
/// 原因另以一行 JSON 输出到 stdout。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        UpdateConfig config;
        var json = args.Contains("--json");
        var apply = args.Contains("--apply");

        try
        {
            config = Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            ShowUsage();
            return 2;
        }


        IProgress<UpdateProgress>? progress = json
            ? null
            : new Progress<UpdateProgress>(p =>
                Console.WriteLine($"[{p.Current}/{p.Total}] {p.FileName} {p.Stage}"));

        UpdateResult result;
        try
        {
            result = apply
                ? new StagedApplier(config).Run(progress)
                : new ClientUpdater(config).Run(progress);
        }
        catch (Exception ex)
        {
            // 库本身不抛异常；这里兜住配置/环境层面的意外
            Console.Error.WriteLine($"更新过程异常：{ex.Message}");
            return 1;
        }

        // 机器可读的字段集由库定义（UpdateResult.ToJson），壳不再自己抄一份
        Console.WriteLine(json ? result.ToJson() : result.ToString());

        // 暂存模式：内容已就绪但**还没生效**，必须与"已更新"分开表达（AGENT.md §12.7）
        if (result.Outcome == UpdateOutcome.Staged) return 3;

        return result.Applied ? 0 : 1;
    }

    private static UpdateConfig Parse(string[] args)
    {
        string? root = null, manifestUrl = null, localManifest = null, cacheDir = null, toolsDir = null, log = null;
        var exclude = new List<string>();
        var fallback = new List<string>();
        var concurrency = 4;
        var verifyUnchanged = false;
        var applyMode = ApplyMode.InPlace;
        int? waitForPid = null;
        string? waitForName = null;
        long? waitForStart = null;
        string? restartExe = null;
        string? restartArgs = null;
        string? restartCwd = null;
        var restartDelaySeconds = 1;
        var quiescenceSeconds = 600;
        var pollSeconds = 2;
        // 保险丝默认**关闭**（AGENT.md §4.4 / Q4）：变更文件多正是增量该发挥作用的场景。
        // 这里原来是 500 / 0.30 —— 等于默认开启，会让"变更文件多"直接被判成需要完整包。
        var thresholdFiles = 0;
        var thresholdRatio = 0.0;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root": root = Next(args, ref i); break;
                case "--manifest-url": manifestUrl = Next(args, ref i); break;
                case "--local-manifest": localManifest = Next(args, ref i); break;
                case "--cache-dir": cacheDir = Next(args, ref i); break;
                case "--tools-dir": toolsDir = Next(args, ref i); break;
                case "--log": log = Next(args, ref i); break;
                case "--exclude": exclude.AddRange(Split(Next(args, ref i))); break;
                case "--fallback": fallback.AddRange(Split(Next(args, ref i))); break;
                case "--concurrency": concurrency = int.Parse(Next(args, ref i)); break;
                case "--threshold-files": thresholdFiles = int.Parse(Next(args, ref i)); break;
                case "--threshold-ratio": thresholdRatio = double.Parse(Next(args, ref i)); break;
                case "--verify-unchanged": verifyUnchanged = true; break;
                case "--apply-mode": applyMode = ParseApplyMode(Next(args, ref i)); break;
                case "--apply": break;
                case "--wait-for-pid": waitForPid = int.Parse(Next(args, ref i)); break;
                case "--wait-for-name": waitForName = Next(args, ref i); break;
                case "--wait-for-start": waitForStart = long.Parse(Next(args, ref i)); break;
                case "--restart": restartExe = Next(args, ref i); break;
                case "--restart-args": restartArgs = Next(args, ref i); break;
                case "--restart-cwd": restartCwd = Next(args, ref i); break;
                case "--restart-delay": restartDelaySeconds = int.Parse(Next(args, ref i)); break;
                case "--quiescence-timeout": quiescenceSeconds = int.Parse(Next(args, ref i)); break;
                case "--poll-seconds": pollSeconds = int.Parse(Next(args, ref i)); break;
                case "--json": break;
                case "--help": ShowUsage(); Environment.Exit(0); break;
                default: throw new ArgumentException($"未知参数：{args[i]}");
            }
        }

        if (string.IsNullOrEmpty(root)) throw new ArgumentException("缺少 --root");
        if (string.IsNullOrEmpty(manifestUrl)) throw new ArgumentException("缺少 --manifest-url");

        return new UpdateConfig
        {
            RootPath = Path.GetFullPath(root),
            ManifestUrl = manifestUrl,
            LocalManifestPath = localManifest is null ? null : Path.GetFullPath(localManifest),
            CacheDir = cacheDir is null ? null : Path.GetFullPath(cacheDir),
            ToolsDir = toolsDir is null ? null : Path.GetFullPath(toolsDir),
            LogPath = log is null ? null : Path.GetFullPath(log),
            ExcludedDirs = exclude,
            FallbackBaseUrls = fallback,
            MaxConcurrency = concurrency,
            FullPackageThresholdFiles = thresholdFiles,
            FullPackageThresholdRatio = thresholdRatio,
            VerifyUnchangedFiles = verifyUnchanged,
            ApplyMode = applyMode,
            WaitForProcessId = waitForPid,
            WaitForProcessName = waitForName,
            WaitForProcessStartTicks = waitForStart,
            // CLI 里出现 --restart 就是"要重启"，不必再要一个开关
            RestartAfterApply = restartExe is not null,
            RestartExecutable = restartExe,
            RestartArguments = restartArgs,
            RestartWorkingDirectory = restartCwd,
            RestartDelay = TimeSpan.FromSeconds(Math.Max(0, restartDelaySeconds)),
            ApplierQuiescenceTimeout = TimeSpan.FromSeconds(Math.Max(1, quiescenceSeconds)),
            ApplierPollInterval = TimeSpan.FromSeconds(Math.Max(1, pollSeconds)),
        };
    }

    private static ApplyMode ParseApplyMode(string value) => value.ToLowerInvariant() switch
    {
        "inplace" or "in-place" or "direct" => ApplyMode.InPlace,
        "staged" or "stage" => ApplyMode.Staged,
        _ => throw new ArgumentException($"--apply-mode 只接受 inplace 或 staged（实得 {value}）"),
    };

    private static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} 缺少取值");
        return args[++i];
    }

    private static void ShowUsage()
    {
        Console.WriteLine("用法: Client.CLI.exe --root <安装目录> --manifest-url <远端清单地址> [选项]");
        Console.WriteLine();
        Console.WriteLine("  --local-manifest <路径>   本地清单路径（默认 <root>/manifest.xml）");
        Console.WriteLine("  --cache-dir <目录>        缓存目录（默认 <系统临时目录>/updater-cache/<安装根指纹>）");
        Console.WriteLine("  --tools-dir <目录>        外部工具目录（默认程序目录）");
        Console.WriteLine("  --log <路径>              日志路径（默认 <cache-dir>/update.log；未指定 cache-dir 时用用户目录下的 updater-logs）");
        Console.WriteLine("  --exclude <列表>          不受管顶层目录，逗号分隔");
        Console.WriteLine("  --fallback <列表>         备用基准地址，逗号分隔");
        Console.WriteLine("  --concurrency <N>         并发上限（默认 4）");
        Console.WriteLine("  --threshold-files <N>     待下载文件数阈值（默认 0 = 关闭）");
        Console.WriteLine("  --threshold-ratio <R>     待下载文件数占比阈值（默认 0 = 关闭）");
        Console.WriteLine("  --apply-mode <模式>       inplace（默认，就地替换）或 staged（只暂存，宿主退出后由 applier 落地）");
        Console.WriteLine("  --verify-unchanged        对判定无需更新的文件重新校验哈希（慢）");
        Console.WriteLine("  --json                    只输出一行 JSON 结果");
        Console.WriteLine();
        Console.WriteLine("暂存更新的落地（宿主退出后跑；库不自己 spawn 进程，见 AGENT.md §12.5）：");
        Console.WriteLine("  --apply                   只做落地：把 UpdaterStage 里已就绪的内容换上去");
        Console.WriteLine("  --wait-for-pid <PID>      --apply 时先等这个进程退出（宿主把自己的 PID 传进来）");
        Console.WriteLine("  --wait-for-name <名字>    连同 PID 一起认宿主（PID 会被系统复用，光凭 PID 认不准）");
        Console.WriteLine("  --wait-for-start <ticks>  宿主的启动时刻，唯一实例标识（后两个由 BuildApplyCommand 自动填，宿主不用管）");
        Console.WriteLine("  --quiescence-timeout <秒> --apply 时等树静的时限（默认 600）");
        Console.WriteLine("  --poll-seconds <秒>       --apply 时的轮询间隔（默认 2）");
        Console.WriteLine("  --restart <exe>           落地成功后把宿主拉起来（可选；宿主一般用 BuildApplyCommand 自动带上）");
        Console.WriteLine("  --restart-args <原文>     原样传回宿主的参数（原始命令行去掉 exe 那段的原文，引号不重新解释）");
        Console.WriteLine("  --restart-cwd <目录>      宿主的工作目录       --restart-delay <秒>  拉起前等待（默认 1）");
    }
}
