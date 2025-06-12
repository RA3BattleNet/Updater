using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using static Ra3.BattleNet.Updater.Server.API;
using Ra3.BattleNet.Updater.Share.Utilities;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace Ra3.BattleNet.Updater.Server.CLI
{
    internal class CommandLineOptions
    {
        public string? OldManifestPath { get; set; }
        public string? NewManifestPath { get; set; }
        public string? OldBasePath { get; set; }
        public string? NewBasePath { get; set; }
        public string? OutputPath { get; set; }
    }

    internal static class CommandLineParser
    {
        public static CommandLineOptions Parse(string[] args)
        {
            CommandLineOptions options = new CommandLineOptions();

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--old-manifest":
                            options.OldManifestPath = args[++i];
                            break;
                        case "--new-manifest":
                            options.NewManifestPath = args[++i];
                            break;
                        case "--old-base":
                            options.OldBasePath = args[++i];
                            break;
                        case "--new-base":
                            options.NewBasePath = args[++i];
                            break;
                        case "--output":
                            options.OutputPath = args[++i];
                            break;
                    }
                }

                // 验证必要参数
                if (string.IsNullOrEmpty(options.OldManifestPath)
                    || string.IsNullOrEmpty(options.NewManifestPath)
                    || string.IsNullOrEmpty(options.OldBasePath)
                    || string.IsNullOrEmpty(options.NewBasePath))
                {
                    Logger.Fail("缺少必要参数\n");
                    ShowUsage();
                    Environment.Exit(-1);
                }

                options.OutputPath ??= Directory.GetCurrentDirectory();

                return options;
            }
            catch (Exception ex)
            {
                Logger.Fail($"参数无法解析{Environment.NewLine}Msg:{ex.Message}");
                ShowUsage();
                Environment.Exit(-2);
            }
            return options;
        }

        public static void ShowUsage()
        {
            Console.Write("使用方式:\n");
            Console.Write("--old-manifest [路径] 旧版本清单文件\n");
            Console.Write("--new-manifest [路径] 新版本清单文件\n");
            Console.Write("--old-base [路径]     旧版本文件目录\n");
            Console.Write("--new-base [路径]     新版本文件目录\n");
            Console.Write("--output [路径]       最终输出目录 (可选，默认当前目录)\n");
        }
    }

    internal class Program
    {
        static int Main(string[] args)
        {
#if DEBUG
            Logger.IsDebug = true;
#else
            Logger.IsDebug = false;
            Logger.IsDebug = args.Contains("--debug");
#endif
            Logger.Info("Updater Server init\n");
            Logger.Debug($"args: {string.Join(" ", args)}{Environment.NewLine}");
            // 解析
            CommandLineOptions options = CommandLineParser.Parse(args);
            
            //if (options == null)
            //{
            //    Logger.Fail("参数解析失败，请检查输入。\n");
            //    //options?.ShowUsage();
            //    return -1;
            //}

            ManifestModel oldManifest = new ManifestModel(options.OldManifestPath);
            
            if (oldManifest == null)
            {
                Logger.Fail($"旧版本清单加载失败，请检查路径和格式。\n");
                Environment.Exit(-3);
            }

            ManifestModel newManifest = new ManifestModel(options.NewManifestPath);
            if (oldManifest == null)
            {
                Logger.Fail("新版本清单加载失败，请检查路径和格式。\n");
                Environment.Exit(-4);
            }

            Logger.Info($" Manifest : v{oldManifest.Version}({oldManifest.Tags.Commit}) -> v{newManifest.Version}({newManifest.Tags.Commit})\n");
            List<PatchOperation> patchOperations = CalculatePatchOperations(
                oldManifest,
                newManifest,
                options.OldBasePath,
                options.NewBasePath,
                options.OutputPath);

            if (patchOperations == null)
            {
                Logger.Fail($"补丁计算出错{Environment.NewLine}");
                Environment.Exit(-5);
            }

            // 生成补丁包
            GeneratePatchPackage(patchOperations, newManifest, options.OutputPath);
            Logger.Success($"Patch Generated -> {Path.GetFullPath(options.OutputPath)}\n");
            return 0;
        }

    }


}