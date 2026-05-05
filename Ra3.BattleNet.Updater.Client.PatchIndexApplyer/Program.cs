using Ra3.BattleNet.Updater.Share.Log;
using System.Diagnostics;

namespace Ra3.BattleNet.Updater.Client.PatchIndexApplyer
{
    internal class Program
    {
        internal class CommandLineOptions
        {
            public string? localRootPath { get; set; }
            public string? localManifestPath { get; set; }
            public string? NewestManifestPath { get; set; }
            public string? NewestPatchesPath { get; set; }
            public string? NewestDownloadPath { get; set; }
            public string? Relaunch { get; set; }

            public void ShowUsage()
            {
                Console.Write("使用方式:\n");
                Console.Write("--local-rootPath <本地待更新文件夹路径>\n" +
                    "--local-manifestPath <本地manifest文件路径>\n" +
                    "--newest-manifestPath <最新的manifest文件路径或URL>\n" +
                    "--newest-patchesPath <最新的patches文件路径或URL>\n" +
                    "--newest-downloadPath <最新的download文件路径或URL>\n" +
                    "--relaunch <更新完成后启动的exe路径>\n" +
                    "--debug  输出更多日志\n");
            }
        }

        private static CommandLineOptions ParseArgs(string[] args)
        {
            var o = new CommandLineOptions();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--local-rootPath":      o.localRootPath = args[++i]; break;
                    case "--local-manifestPath":   o.localManifestPath = args[++i]; break;
                    case "--newest-manifestPath":  o.NewestManifestPath = args[++i]; break;
                    case "--newest-patchesPath":   o.NewestPatchesPath = args[++i]; break;
                    case "--newest-downloadPath":  o.NewestDownloadPath = args[++i]; break;
                    case "--relaunch":             o.Relaunch = args[++i]; break;
                }
            }
            return o;
        }

        static async Task<int> Main(string[] args)
        {
            if (args.Contains("--help"))
            {
                new CommandLineOptions().ShowUsage();
                return 0;
            }

            Logger.IsDebug = args.Contains("--debug");
            Logger.Info("Client Updater Starting\n");

            var opt = ParseArgs(args);

            if (string.IsNullOrEmpty(opt.localRootPath) ||
                string.IsNullOrEmpty(opt.localManifestPath) ||
                string.IsNullOrEmpty(opt.NewestManifestPath) ||
                string.IsNullOrEmpty(opt.NewestPatchesPath) ||
                string.IsNullOrEmpty(opt.NewestDownloadPath))
            {
                Logger.Fail("缺少必要参数\n");
                new CommandLineOptions().ShowUsage();
                return -1;
            }

            var engine = new PatchIndexApplyerEngine(
                new PatchIndexApplyerEngine.Options(
                    opt.localRootPath,
                    opt.localManifestPath,
                    opt.NewestManifestPath,
                    opt.NewestPatchesPath,
                    opt.NewestDownloadPath),
                new Progress<PatchIndexApplyerEngine.ProgressReport>(r =>
                    Console.WriteLine($"[{r.Current}/{r.Total}] {r.FileName} {r.Status}")));

            try
            {
                await engine.RunAsync();
            }
            catch (Exception ex)
            {
                Logger.Fail($"更新过程发生错误：{ex.Message}\n");
            }

            RelaunchAndExit(opt);
            return 0;
        }

        private static void RelaunchAndExit(CommandLineOptions opt)
        {
            if (!string.IsNullOrEmpty(opt.Relaunch))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = opt.Relaunch,
                        WorkingDirectory = opt.localRootPath!,
                        UseShellExecute = true
                    });
                }
                catch { }
            }
            Environment.Exit(0);
        }
    }
}
