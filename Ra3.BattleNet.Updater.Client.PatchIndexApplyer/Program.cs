using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using System.Diagnostics;
using Ra3.BattleNet.Updater.Client.PatchIndexApplyer.Models;
using System.Xml.Linq;

namespace Ra3.BattleNet.Updater.Client.PatchIndexApplyer
{
    internal class Program
    {
        internal class CommandLineOptions
        {
            internal object options;

            public string? localRootPath { get; set; }
            public string? localManifestPath { get; set; }
            public string? NewestManifestPath { get; set; }
            public string? NewestPatchesPath { get; set; }
            public string? NewestDownloadPath { get; set; }
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
                            case "--local-rootPath":
                                options.localRootPath = args[++i];
                                break;
                            case "--local-manifestPath":
                                options.localManifestPath = args[++i];
                                break;
                            case "--newest-manifestPath":
                                options.NewestManifestPath = args[++i];
                                break;
                            case "--newest-patchesPath":
                                options.NewestPatchesPath = args[++i];
                                break;
                            case "--newest-downloadPath":
                                options.NewestDownloadPath = args[++i];
                                break;
                        }
                    }

                    // 验证必要参数
                    if (string.IsNullOrEmpty(options.localRootPath) ||
                        string.IsNullOrEmpty(options.localManifestPath) ||
                        string.IsNullOrEmpty(options.NewestPatchesPath) ||
                        string.IsNullOrEmpty(options.NewestDownloadPath) ||
                        string.IsNullOrEmpty(options.NewestManifestPath))
                    {
                        Logger.Fail("缺少必要参数\n");
                        ShowUsage();
                        Environment.Exit(-1);
                    }
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
                Console.Write("--local-rootPath <本地待更新文件夹路径>\n" +
                    "--local-manifestPath <本地manifest文件路径>\n" +
                    "--newest-manifestPath <最新的manifest文件路径或URL>\n" +
                    "--newest-patchesPath <最新的patches文件路径或URL>\n" +
                    "--newest-downloadPath <最新的download文件路径或URL>\n" +
                    "--debug  输出更多日志\n");
            }
        }

        static async Task Main(string[] args)
        {
#if DEBUG
            Logger.IsDebug = true;
#else
            Logger.IsDebug = false;
#endif
            Logger.IsDebug = args.Contains("--debug");
            Logger.Info("Client Updater Starting\n");
            Logger.Debug($"args: {string.Join(" ", args)}{Environment.NewLine}");

            CommandLineOptions options = CommandLineParser.Parse(args);
            // 先获取最新manifest并与本地进行比对，相同则认为已是最新，无需更新
            // 若不同则会尝试读取patches以找到所需文件的patch信息
            // 若有记录则下载patch并补丁，如果无记录则直接下载文件

            Debug.Assert(options.localRootPath != null);
            Debug.Assert(Path.Exists(options.localRootPath));
            Debug.Assert(options.localManifestPath != null);
            Debug.Assert(File.Exists(options.localManifestPath));

            Debug.Assert(options.NewestManifestPath != null);
            Debug.Assert(options.NewestPatchesPath != null);
            Debug.Assert(options.NewestDownloadPath != null);
            try
            {
                //获取最新manifest
                ManifestModel newestManifest = new ManifestModel(options.NewestManifestPath);
                if (newestManifest == null)
                {
                    Logger.Fail("最新Manifest获取失败");
                    Environment.Exit(-3);
                    return;
                }

                Guid nuuid = newestManifest.Tags.UUID;

                //获取本地manifest
                ManifestModel localManifest = new ManifestModel(options.localManifestPath);
                if (newestManifest == null)
                {
                    Logger.Fail("本地Manifest获取失败");
                    Environment.Exit(-4);
                    return;
                }

                Guid luuid = localManifest.Tags.UUID;

                // 比对生成UUID
                if (luuid == nuuid)
                {
                    Logger.Info("客户端已是最新版本，无需更新");
                    Environment.Exit(0);
                    return;
                }
                Logger.Info($"发现新版本：{nuuid}（当前版本：{luuid}），开始尝试增量更新");

                string patchesStr = await LoadJsonAsync(options.NewestPatchesPath);
                if (string.IsNullOrEmpty(patchesStr))
                {
                    Logger.Fail("无法获取patches.json文件内容");
                    Environment.Exit(-5);
                    return;
                }
                PatchIndex patchesContent = new PatchIndex(patchesStr);
                if (patchesContent == null)
                {
                    Logger.Fail("无法解析patches.json文件内容");
                    Environment.Exit(-6);
                    return;
                }

                Logger.Info("成功获取patches.json文件，准备应用更新");

                // TODO:添加解析和应用更新的逻辑**
            }
            catch (Exception ex)
            {
                Logger.Fail($"更新过程发生错误：{ex.Message}");
                Environment.Exit(-7);
            }
        }

        /// <summary>
        /// 用于统一处理本地文件路径和网络URL的JSON加载
        /// </summary>
        /// <param name="path">本地路径或URL</param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        /// <exception cref="FileNotFoundException"></exception>
        public static async Task<string> LoadJsonAsync(string path)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out Uri uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                using (HttpClient client = new HttpClient())
                {
                    try
                    {
                        return await client.GetStringAsync(uri);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"无法从URL加载JSON: {path}", ex);
                    }
                }
            }
            else
            {
                // 本地文件路径
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException($"文件未找到: {path}");
                }
                return File.ReadAllText(path);
            }
        }
    }
}