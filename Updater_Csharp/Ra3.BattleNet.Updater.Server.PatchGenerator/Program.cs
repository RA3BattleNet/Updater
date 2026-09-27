using Ra3.BattleNet.Updater.Server;

namespace Ra3.BattleNet.Updater.Tools;

/// <summary>
/// 生成内容对命名的补丁与内容寻址的完整文件（AGENT.md §5.3）。
/// 无状态、无数据库；输出可直接作为静态文件树部署。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Length == 0)
        {
            ShowUsage();
            return args.Length == 0 ? -1 : 0;
        }

        string? manifest = null, manifestRoot = null, output = null, tools = null;
        long minSize = PatchGenerator.DefaultMinFileSize;
        var verify = true;
        var prune = false;
        var baselines = new List<Baseline>();

        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--manifest": manifest = Next(args, ref i); break;
                    case "--manifest-root": manifestRoot = Next(args, ref i); break;
                    case "--baseline":
                        baselines.Add(new Baseline(Next(args, ref i), string.Empty));
                        break;
                    case "--baseline-root":
                        if (baselines.Count == 0) throw new ArgumentException("--baseline-root 之前必须有一个 --baseline");
                        baselines[^1] = baselines[^1] with { RootPath = Next(args, ref i) };
                        break;
                    case "--output": output = Next(args, ref i); break;
                    case "--tools": tools = Next(args, ref i); break;
                    case "--min-size": minSize = long.Parse(Next(args, ref i)); break;
                    case "--no-verify": verify = false; break;
                    case "--prune": prune = true; break;
                    default:
                        Console.Error.WriteLine($"未知参数：{args[i]}");
                        ShowUsage();
                        return -2;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"参数无法解析：{ex.Message}");
            return -2;
        }

        if (manifest is null || manifestRoot is null || output is null)
        {
            Console.Error.WriteLine("缺少必要参数");
            ShowUsage();
            return -1;
        }

        var resolved = baselines
            .Where(b => !string.IsNullOrEmpty(b.RootPath))
            .Select(b => b with { RootPath = Path.GetFullPath(b.RootPath) })
            .ToList();

        var summary = PatchGenerator.Generate(
            Path.GetFullPath(manifest), Path.GetFullPath(manifestRoot), resolved,
            Path.GetFullPath(output), minSize, verify, prune, tools);

        Console.WriteLine($"基线 {summary.Baselines} 个");
        Console.WriteLine($"补丁：新建 {summary.PatchesCreated}，已存在 {summary.PatchesSkipped}，失败 {summary.PatchesFailed}，合计 {summary.PatchBytes:N0} 字节");
        Console.WriteLine($"完整文件：复制 {summary.FilesCopied}；清理补丁 {summary.PatchesPruned}$");
        Console.WriteLine($"输出目录：{Path.GetFullPath(output)}");
        return summary.PatchesFailed > 0 ? 1 : 0;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} 缺少取值");
        return args[++i];
    }

    private static void ShowUsage()
    {
        Console.WriteLine("用法: PatchGenerator.exe --manifest <xml> --manifest-root <dir> --output <dir>");
        Console.WriteLine("                              [--baseline <xml> --baseline-root <dir>]... [选项]");
        Console.WriteLine();
        Console.WriteLine("  --manifest / --manifest-root   新版本的清单与其文件根目录");
        Console.WriteLine("  --baseline / --baseline-root   基线版本清单与其根目录；可重复多组");
        Console.WriteLine("  --output                       输出目录（files/ 与 patches/ 会在此建立）");
        Console.WriteLine("  --min-size <字节>              小于该大小的文件不生成补丁（默认 16384）");
        Console.WriteLine("  --tools <目录>                 外部工具目录（默认程序目录）");
        Console.WriteLine("  --prune                        删除不属于当前基线集合的补丁文件");
        Console.WriteLine("  --no-verify                    跳过「补丁可用性」校验（默认会校验）");
    }
}
