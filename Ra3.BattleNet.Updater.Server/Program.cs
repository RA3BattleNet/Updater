using Ra3.BattleNet.Updater.Share;
using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace Ra3.BattleNet.Updater.Server
{
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
            Logger.Debug($"{string.Join(" ", args)}{Environment.NewLine}");
            // 解析
            CommandLineOptions? options = CommandLineParser.Parse(args);
            if (options == null)
            {
                Logger.Fail("参数解析失败，请检查输入。\n");
                //options?.ShowUsage();
                return -1;
            }

            ManifestModel oldManifest = new ManifestModel(options.OldManifestPath);
            if (oldManifest == null)
            {
                Logger.Fail("旧版本清单加载失败，请检查路径和格式。\n");
                return -2;
            }

            ManifestModel newManifest = new ManifestModel(options.NewManifestPath);
            if (oldManifest == null)
            {
                Logger.Fail("新版本清单加载失败，请检查路径和格式。\n");
                return -3;
            }

            Logger.Info($" Manifest : v{oldManifest.Version}({oldManifest.Tags.Commit}) -> v{newManifest.Version}({newManifest.Tags.Commit})\n");
            List<PatchOperation> patchOperations = CalculatePatchOperations(
                oldManifest,
                newManifest,
                options.OldBasePath,
                options.NewBasePath,
                options.OutputPath);

            if (patchOperations == null) return -4;

            // 生成补丁包
            GeneratePatchPackage(patchOperations, newManifest, options.OutputPath);
            Logger.Success($"Patch Generated -> {Path.GetFullPath(options.OutputPath)}\n");
            return 0;
        }

        private static List<PatchOperation> CalculatePatchOperations(
            ManifestModel oldManifest,
            ManifestModel newManifest,
            string oldBasePath,
            string newBasePath,
            string outputPath)
        {
            List<PatchOperation> operations = new List<PatchOperation>();

            Directory.CreateDirectory(outputPath);
            Logger.Debug($"Patch相对输出路径：{outputPath}{Environment.NewLine}");
            
            foreach (var newFile in newManifest.Manifest.Files)
            {
                var oldFile = oldManifest.Manifest.Files
                    .FirstOrDefault(f => f.UUID == newFile.UUID);

                // 检测新增文件
                if (oldFile == null)
                {
                    operations.Add(new PatchOperation
                    {
                        Type = OperationTypeEnum.ForceCopy,
                        File = newFile,
                        SourcePath = Path.Combine(newBasePath, newFile.Path.TrimStart('/', '\\'), newFile.FileName),
                    });
                    Logger.Info($"新文件: {newFile.FileName}{newFile.UUID}\n");
                    continue;
                }

                // 检测修改文件
                if (oldFile.MD5 != newFile.MD5)
                {
                    if (newFile.Mode == FileModeEnum.Skip)
                    {
                        Logger.Warning($"此文件被标记跳过: {newFile.FileName}\n");
                        continue;
                    }

                    string oldFilePath = Path.Combine(Environment.CurrentDirectory, oldBasePath, oldFile.Path.TrimStart('/', '\\'), oldFile.FileName);
                    string newFilePath = Path.Combine(Environment.CurrentDirectory, newBasePath, newFile.Path.TrimStart('/', '\\'), newFile.FileName);

                    if (!File.Exists(oldFilePath))
                    {
                        Logger.Warning($"旧版本文件不存在，将提供完整文件: {newFile.FileName}\n");
                        operations.Add(new PatchOperation
                        {
                            Type = OperationTypeEnum.ForceCopy,
                            File = newFile,
                            SourcePath = newFilePath
                        });
                    }
                    else
                    {
                        string patchFileName = $"{newFile.UUID:N}.hdiff";
                        string patchFilePath = Path.Combine(outputPath, "files",patchFileName);

                        if (PatchGenerater.GeneratePatch(oldFilePath, newFilePath, patchFilePath))
                        {
                            operations.Add(new PatchOperation
                            {
                                Type = OperationTypeEnum.Patch,
                                File = newFile,
                                PatchPath = patchFilePath,
                                PatchSize = new FileInfo(patchFilePath).Length,
                                SourceMD5 = oldFile.MD5,
                                TargetMD5 = newFile.MD5
                            });
                            Logger.Info($"生成补丁: {newFile.FileName} ({FormatSize(new FileInfo(patchFilePath).Length)})\n");
                        }
                        else
                        {
                            Logger.Fail($"补丁生成失败: {newFile.FileName}\n");
                            // 如果补丁生成失败，直接复制新文件
                            operations.Add(new PatchOperation
                            {
                                Type = OperationTypeEnum.ForceCopy,
                                File = newFile,
                                SourcePath = newFilePath
                            });
                            Logger.Info($"补丁生成失败，使用完整文件: {newFile.FileName}{Environment.NewLine}");
                            continue;
                        }
                    }
                }
                else
                {
                    Logger.Info($"文件未改变，不更新: {newFile.FileName} ({newFile.UUID}){Environment.NewLine}");
                }
            }

            //// 检测删除的文件
            //foreach (var oldFile in oldManifest.Manifest.Files)
            //{
            //    if (!newManifest.Manifest.Files.Any(f => f.UUID == oldFile.UUID))
            //    {
            //        operations.Add(new PatchOperation
            //        {
            //            Type = OperationTypeEnum.Delete,
            //            File = oldFile
            //        });
            //        Logger.Info($"检测到删除文件: {oldFile.FileName}\n");
            //    }
            //}

            return operations;
        }

        private static void GeneratePatchPackage(
            List<PatchOperation> operations,
            ManifestModel newManifest,
            string outputPath)
        {
            Logger.Info($"创建补丁包到: {outputPath}\n");
            Directory.CreateDirectory(outputPath);

            // 1. 创建文件目录
            string filesDir = Path.Combine(outputPath, "files");
            Directory.CreateDirectory(filesDir);

            // 2. 复制补丁文件和新增文件
            foreach (PatchOperation op in operations)
            {
                switch (op.Type)
                {
                    case OperationTypeEnum.ForceCopy:
                        string destPath = Path.Combine(filesDir, $"{op.File.UUID:N}");
                        //File.Copy(op.SourcePath, destPath, true);
                        op.RelativePath = $"files/{op.File.UUID:N}";
                        break;

                    case OperationTypeEnum.Patch:
                        string patchDest = Path.Combine(filesDir, $"{op.File.UUID:N}.hdiff");
                        //File.Copy(op.PatchPath, patchDest, true);
                        op.RelativePath = $"files/{op.File.UUID:N}.hdiff";
                        break;
                       
                }
            }

            // 3. 生成补丁清单
            var patchManifest = new PatchManifest
            {
                BaseVersion = newManifest.Version.ToString(),
                TargetVersion = newManifest.Version.ToString(),
                Operations = operations.Select(op => new OperationInfo
                {
                    Type = op.Type.ToString().ToLower(),
                    FilePath = $"{op.File.Path}/{op.File.FileName}",
                    UUID = op.File.UUID.ToString("N"),
                    RelativePath = op.RelativePath,
                    Size = op.Type == OperationTypeEnum.Patch ? op.PatchSize : 0,
                    SourceMD5 = op.SourceMD5,
                    TargetMD5 = op.TargetMD5
                }).ToList()
            };

            string manifestPath = Path.Combine(outputPath, "patch-manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(patchManifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            }));

            Logger.Success($"补丁清单已生成: {manifestPath}\n");
        }

        private static string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double size = bytes;

            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.##} {sizes[order]}";
        }
    }

    // 命令行参数解析类
    internal class CommandLineOptions
    {
        public string OldManifestPath { get; set; }
        public string NewManifestPath { get; set; }
        public string OldBasePath { get; set; }
        public string NewBasePath { get; set; }
        public string OutputPath { get; set; }
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
                    throw new Exception("缺少必要参数");
                }

                options.OutputPath ??= Directory.GetCurrentDirectory();

                return options;
            }
            catch (Exception ex)
            {
                Logger.Fail($"参数解析失败: {ex.Message}\n");
                Logger.Info("使用方式:\n");
                Logger.Info("--old-manifest [路径] 旧版本清单文件\n");
                Logger.Info("--new-manifest [路径] 新版本清单文件\n");
                Logger.Info("--old-base [路径]     旧版本文件目录\n");
                Logger.Info("--new-base [路径]     新版本文件目录\n");
                Logger.Info("--patch-output [路径] 补丁输出目录 (可选，默认临时目录)\n");
                Logger.Info("--output [路径]       最终输出目录 (可选，默认当前目录)\n");
                return null;
            }
        }
    }
}