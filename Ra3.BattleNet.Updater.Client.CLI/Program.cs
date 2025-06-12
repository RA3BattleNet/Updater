using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using static Ra3.BattleNet.Updater.Client.API;
using Ra3.BattleNet.Updater.Share.Utilities;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
namespace Ra3.BattleNet.Updater.Client.CLI
{
    internal class Program
    {
        static int Main(string[] args)
        {
#if DEBUG
            Logger.IsDebug = true;
#else
            Logger.IsDebug=false;
            if (args.Contains("--debug"))
                Logger.IsDebug = true;

#endif
            Logger.Info("增量更新客户端启动\n");

            CommandLineOptions options = CommandLineParser.Parse(args);
            Logger.Debug($"Args: {String.Join(" ", args)}\n");
            if (options == null) return -1;

            PatchManifest patchManifest = LoadPatchManifest(options.PatchPath);
            if (patchManifest == null) return -2;

            bool success = ApplyPatchOperations(patchManifest, options.TargetPath, options.PatchPath);

            return success ? 0 : -3;
        }

        internal class CommandLineOptions
        {
            public string PatchPath { get; set; }  // 补丁包目录路径
            public string TargetPath { get; set; } // 客户端目标目录
        }

        internal static class CommandLineParser
        {
            public static CommandLineOptions Parse(string[] args)
            {
                var options = new CommandLineOptions();

                try
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        switch (args[i])
                        {
                            case "--patch":
                                options.PatchPath = args[++i];
                                break;
                            case "--target":
                                options.TargetPath = args[++i];
                                break;
                        }
                    }

                    // 验证必要参数
                    if (string.IsNullOrEmpty(options.PatchPath))
                        throw new Exception("缺少 --patch 参数");

                    if (string.IsNullOrEmpty(options.TargetPath))
                        throw new Exception("缺少 --target 参数");

                    return options;
                }
                catch (Exception ex)
                {
                    Logger.Fail($"参数解析失败: {ex.Message}\n");
                    Logger.Info("使用方式:\n");
                    Logger.Info("--patch [路径] 补丁包目录路径\n");
                    Logger.Info("--target [路径] 客户端目标目录\n");
                    return null;
                }
            }
        }

    }
}

