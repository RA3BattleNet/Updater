using System.Text;
using Ra3.BattleNet.Updater.Server;
using Ra3.BattleNet.Updater.Share.Log;

namespace Ra3.BattleNet.Updater.XmlGenerator;

/// <summary>
/// 清单生成壳。业务逻辑在 Ra3.BattleNet.Updater.Server.ManifestGenerator（AGENT.md §6.1）。
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

        string? oldXml = null, oldRoot = null, targetDir = null, outXml = null, reportPath = null;
        var exclude = new List<string>();
        var autoLink = AutoLinkMode.Off;

        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--old-xmlpath": oldXml = Next(args, ref i); break;
                    case "--old-root": oldRoot = Next(args, ref i); break;
                    case "--target-dir": targetDir = Next(args, ref i); break;
                    case "--new-xmloutputpath": outXml = Next(args, ref i); break;
                    case "--report": reportPath = Next(args, ref i); break;
                    case "--exclude-dirs":
                        exclude.AddRange(Next(args, ref i).Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(d => d.Trim()));
                        break;
                    // 自动把"消失 × 新增"里成对的改名接上 UUID（理由与逻辑见 README.md）
                    case var a when a == "--auto-link-uuids" || a.StartsWith("--auto-link-uuids=", StringComparison.Ordinal):
                        autoLink = a.Contains("=name", StringComparison.OrdinalIgnoreCase)
                            ? AutoLinkMode.ByName
                            : AutoLinkMode.ByNameAndSize;
                        break;
                    case "--debug": Logger.IsDebug = true; break;
                    default:
                        Logger.Fail($"未知参数：{args[i]}");
                        ShowUsage();
                        return -2;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Fail($"参数无法解析：{ex.Message}");
            return -2;
        }

        if (string.IsNullOrEmpty(targetDir) || string.IsNullOrEmpty(outXml))
        {
            Logger.Fail("缺少必要参数");
            ShowUsage();
            return -1;
        }

        var result = ManifestGenerator.Generate(targetDir, oldXml, exclude, oldRoot, toolsDir: null, autoLink);
        result.Manifest.SaveToXml(outXml);

        var report = ManifestGenerator.FormatReport(result, oldXml, oldRoot);
        Console.Write(report);

        if (!string.IsNullOrEmpty(reportPath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(reportPath, report, new UTF8Encoding(false));
        }

        return 0;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} 缺少取值");
        return args[++i];
    }

    private static void ShowUsage()
    {
        Console.WriteLine("用法: XmlGenerator.exe --target-dir <目录> --new-xmloutputpath <路径> [选项]");
        Console.WriteLine();
        Console.WriteLine("选项:");
        Console.WriteLine("  --old-xmlpath <路径>       上一版清单（用于继承 UUID；链式生成时必须提供）");
        Console.WriteLine("  --old-root <目录>          上一版文件根目录（提供后自检会计算疑似改名的补丁率）");
        Console.WriteLine("  --target-dir <目录>        必需，本版文件所在目录");
        Console.WriteLine("  --new-xmloutputpath <路径> 必需，本版清单输出路径");
        Console.WriteLine("  --exclude-dirs <列表>      逗号分隔的顶层目录名，其下文件标记为 Mode=Skip");
        Console.WriteLine("  --report <路径>            把自检报告额外写一份到文件");
        Console.WriteLine("  --auto-link-uuids[=name]   自动把疑似改名成对的 UUID 接上（默认关闭；");
        Console.WriteLine("                             =name 只用「同名」这个强信号，不带则再加「尺寸接近」兜底）");
        Console.WriteLine("  --help / --debug");
    }
}
