using System.Text.Json;
using Ra3.BattleNet.Updater.Core;
using CoreUpdater = Ra3.BattleNet.Updater.Core.Updater;

namespace Ra3.BattleNet.Updater.Client.Update;

/// <summary>
/// 独立进程壳：跑一次更新并给出结构化结果（AGENT.md §4.12）。
/// 退出码：0 = 已最新或已更新；1 = 需要完整包 / 失败；2 = 参数或配置错误。
/// 原因另以一行 JSON 输出到 stdout。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        UpdateConfig config;
        var json = args.Contains("--json");

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
            result = new CoreUpdater(config).Run(progress);
        }
        catch (Exception ex)
        {
            // 库本身不抛异常；这里兜住配置/环境层面的意外
            Console.Error.WriteLine($"更新过程异常：{ex.Message}");
            return 1;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Outcome = result.Outcome.ToString(),
                result.Reason,
                result.Detail,
                result.HttpVersion,
                result.Total,
                result.Skipped,
                result.Moved,
                result.Patched,
                result.Full,
                result.FailedCount,
                result.BytesDownloaded,
                result.PayloadBytes,
                result.WireBytes,
                result.WireSentBytes,
                result.WireReceivedBytes,
                Ms = (long)result.Elapsed.TotalMilliseconds,
            }));
        }
        else
        {
            Console.WriteLine(result.ToString());
        }

        return result.Applied ? 0 : 1;
    }

    private static UpdateConfig Parse(string[] args)
    {
        string? root = null, manifestUrl = null, localManifest = null, cacheDir = null, toolsDir = null, log = null;
        var exclude = new List<string>();
        var fallback = new List<string>();
        var concurrency = 4;
        var verifyUnchanged = false;
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
        };
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} 缺少取值");
        return args[++i];
    }

    private static void ShowUsage()
    {
        Console.WriteLine("用法: Client.Update.exe --root <安装目录> --manifest-url <远端清单地址> [选项]");
        Console.WriteLine();
        Console.WriteLine("  --local-manifest <路径>   本地清单路径（默认 <root>/manifest.xml）");
        Console.WriteLine("  --cache-dir <目录>        缓存目录（默认 <root>/UpdaterCache）");
        Console.WriteLine("  --tools-dir <目录>        外部工具目录（默认程序目录）");
        Console.WriteLine("  --log <路径>              日志路径（默认 <cache-dir>/update.log）");
        Console.WriteLine("  --exclude <列表>          不受管顶层目录，逗号分隔");
        Console.WriteLine("  --fallback <列表>         备用基准地址，逗号分隔");
        Console.WriteLine("  --concurrency <N>         并发上限（默认 4）");
        Console.WriteLine("  --threshold-files <N>     待下载文件数阈值（默认 0 = 关闭）");
        Console.WriteLine("  --threshold-ratio <R>     待下载文件数占比阈值（默认 0 = 关闭）");
        Console.WriteLine("  --verify-unchanged        对判定无需更新的文件重新校验哈希（慢）");
        Console.WriteLine("  --json                    只输出一行 JSON 结果");
    }
}
