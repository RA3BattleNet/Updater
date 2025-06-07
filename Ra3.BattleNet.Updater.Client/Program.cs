using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using Ra3.BattleNet.Updater.Share;
namespace Ra3.BattleNet.Updater.Client
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

            // 解析命令行参数
            var options = CommandLineParser.Parse(args);
            Logger.Debug($"Args: {String.Join(" ",args)}\n");
            if (options == null) return -1;

            // 加载补丁清单
            PatchManifest patchManifest = LoadPatchManifest(options.PatchPath);
            if (patchManifest == null) return -2;

            // 执行更新操作
            bool success = ApplyPatchOperations(patchManifest, options.TargetPath, options.PatchPath);

            return success ? 0 : -3;
        }

        private static PatchManifest LoadPatchManifest(string patchPath)
        {
            try
            {
                string manifestPath = Path.Combine(patchPath, "patch-manifest.json");
                Logger.Info($"加载补丁清单: {manifestPath}\n");

                string json = File.ReadAllText(manifestPath);
                return JsonSerializer.Deserialize<PatchManifest>(json);
            }
            catch (Exception ex)
            {
                Logger.Fail($"补丁清单加载失败: {ex.Message}\n");
                return null;
            }
        }

        private static bool ApplyPatchOperations(
            PatchManifest patchManifest,
            string targetPath,
            string patchPath)
        {
            bool allSuccess = true;

            foreach (var operation in patchManifest.Operations)
            {
                string fullTargetPath = Path.Combine(targetPath, operation.FilePath.TrimStart('/'));
                string fullSourcePath = operation.RelativePath != null
                    ? Path.Combine(patchPath, operation.RelativePath)
                    : null;

                try
                {
                    switch (operation.Type.ToLower())
                    {
                        case "add":
                            Logger.Info($"添加文件: {operation.FilePath}\n");
                            Directory.CreateDirectory(Path.GetDirectoryName(fullTargetPath));
                            File.Copy(fullSourcePath, fullTargetPath, overwrite: true);
                            break;

                        case "patch":
                            Logger.Info($"应用补丁: {operation.FilePath}\n");
                            string tempFile = Path.GetTempFileName();

                            if (!PatchApplyer.ApplyPatch(
                                fullTargetPath,
                                fullSourcePath,
                                tempFile))
                            {
                                throw new Exception("补丁应用失败");
                            }

                            // 验证MD5
                            string newMd5 = PublicMethod.GetMD5(tempFile);
                            if (newMd5 != operation.TargetMD5)
                            {
                                throw new Exception($"MD5校验失败 (预期: {operation.TargetMD5}, 实际: {newMd5})");
                            }

                            File.Copy(tempFile, fullTargetPath, overwrite: true);
                            File.Delete(tempFile);
                            break;

                        case "delete":
                            Logger.Info($"删除文件: {operation.FilePath}\n");
                            if (File.Exists(fullTargetPath))
                            {
                                File.Delete(fullTargetPath);
                            }
                            break;

                        default:
                            Logger.Warning($"未知操作类型: {operation.Type}\n");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Fail($"操作失败 [{operation.Type} {operation.FilePath}]: {ex.Message}\n");
                    allSuccess = false;
                }
            }

            return allSuccess;
        }
    }

    // 命令行参数解析
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

    //// 补丁服务实现
    //internal class PatchService
    //{
    //    public bool ApplyPatch(string oldFile, string patchFile, string outputFile)
    //    {
    //        try
    //        {
    //            // 查找 hpatchz 工具位置
    //            string toolPath = FindPatchTool("hpatchz");
    //            if (toolPath == null)
    //            {
    //                Logger.Fail("找不到 hpatchz 工具\n");
    //                return false;
    //            }

    //            var process = new Process
    //            {
    //                StartInfo =
    //                {
    //                    FileName = toolPath,
    //                    Arguments = $"\"{oldFile}\" \"{patchFile}\" \"{outputFile}\"",
    //                    CreateNoWindow = true,
    //                    UseShellExecute = false,
    //                    RedirectStandardOutput = true,
    //                    RedirectStandardError = true
    //                }
    //            };

    //            process.Start();
    //            process.WaitForExit(60000); // 最多等待1分钟

    //            if (process.ExitCode != 0)
    //            {
    //                Logger.Fail($"hpatchz 执行失败 (代码: {process.ExitCode})\n");
    //                Logger.Fail($"错误输出: {process.StandardError.ReadToEnd()}\n");
    //                return false;
    //            }

    //            return true;
    //        }
    //        catch (Exception ex)
    //        {
    //            Logger.Fail($"补丁应用异常: {ex.Message}\n");
    //            return false;
    //        }
    //    }

    //    private string FindPatchTool(string toolName)
    //    {
    //        // 检查当前目录
    //        string currentDir = Path.Combine(Directory.GetCurrentDirectory(), toolName);
    //        if (File.Exists(currentDir)) return currentDir;

    //        // 检查系统PATH
    //        string path = Environment.GetEnvironmentVariable("PATH");
    //        if (!string.IsNullOrEmpty(path))
    //        {
    //            foreach (var dir in path.Split(Path.PathSeparator))
    //            {
    //                string fullPath = Path.Combine(dir, toolName);
    //                if (File.Exists(fullPath)) return fullPath;
    //            }
    //        }

    //        return null;
    //    }
    //}

}

