using System.Diagnostics;
using System.Xml;
using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;

namespace Ra3.BattleNet.Updater.Server
{// 命令行参数解析类
    internal class CommandLineOptions
    {
        public string OldManifestPath { get; set; }
        public string OldBasePath { get; set; }
        public string BasePath { get; set; }
        public string PatchOutputPath { get; set; }
        public string OutputPath { get; set; }
        public Version NewVersion { get; set; }
        public string CommitMessage { get; set; } = "自动生成";
        public void ShowUsage()
        {
            Console.WriteLine("使用方式:\n" +
                              "--old-manifest [路径] 旧版本清单文件\n" +
                              "--old-base [路径]     旧版本文件目录 (可选，默认同manifest目录)\n" +
                              "--base [路径]         新版本文件目录\n" +
                              "--new-version [版本]  新版本号 (格式: major.minor.build)\n" +
                              "--patch-output [路径] 补丁输出目录 (可选，默认临时目录)\n" +
                              "--output [路径]       最终输出目录 (可选，默认当前目录)\n" +
                              "--commit [消息]       提交信息 (可选)\n");
        }
    }
    internal class Program
    {
        static int Main(string[] args)
        {
#if DEBUG
            Logger.IsDebug = true;
#else
            Logger.IsDebug = args.Contains("--debug");
#endif
            Logger.Info("Updater Server init\n");

            // 解析
            var options = CommandLineParser.Parse(args);
            //if (options == null) return -1;

            // 加载旧版本清单
            ManifestModel oldManifest = LoadOldManifest(options.OldManifestPath);
            //if (oldManifest == null) return -2;

            // 生成新版本清单
            ManifestModel newManifest = GenerateNewManifest(
                options.NewVersion,
                options.BasePath,
                oldManifest,
                options.CommitMessage);

            // 计算文件差异并生成补丁
            var patchOperations = CalculatePatchOperations(oldManifest, newManifest, options);
            if (patchOperations == null) return -3;

            // 输出结果
            OutputResults(newManifest, patchOperations, options.OutputPath);

            Logger.Success("服务端处理完成\n");
            return 0;
        }

        private static ManifestModel LoadOldManifest(string manifestPath)
        {
            try
            {
                Logger.Info($"加载旧版本清单: {manifestPath}\n");
                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.Load(manifestPath);

                XmlNode? metadataNode = xmlDoc.SelectSingleNode("/Metadata");
                if (metadataNode == null)
                {
                    Logger.Fail("XML 解析失败: Metadata 节点不存在\n");
                    return null;
                }

                return new ManifestModel(metadataNode);
            }
            catch (Exception ex)
            {
                Logger.Fail($"清单加载失败: {ex.Message}\n");
                return null;
            }
        }

        private static ManifestModel GenerateNewManifest(
            Version newVersion,
            string basePath,
            ManifestModel oldManifest,
            string commitMessage)
        {
            Logger.Info($"生成新版本清单: v{newVersion}\n");

            var newManifest = new ManifestModel(newVersion, commitMessage)
            {
                Tags = new Tags(commitMessage)
            };

            // 扫描目录并生成文件清单
            foreach (var filePath in Directory.GetFiles(basePath, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(basePath, filePath);
                string directory = Path.GetDirectoryName(relativePath) ?? string.Empty;
                string fileName = Path.GetFileName(relativePath);

                string md5 = PublicMethod.GetMD5(filePath);
                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(filePath);
                Version fileVersion = new Version(
                    versionInfo.FileMajorPart,
                    versionInfo.FileMinorPart,
                    versionInfo.FileBuildPart);

                // 查找旧版本中对应的文件（如果存在）
                var oldFile = oldManifest.Manifest.Files
                    .FirstOrDefault(f =>
                        f.Path.Equals(directory, StringComparison.OrdinalIgnoreCase) &&
                        f.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));

                // 创建新的清单文件项
                var manifestFile = new ManifestFile(
                    oldFile?.UUID ?? Guid.NewGuid(),
                    fileName,
                    md5,
                    directory,
                    fileVersion.ToString(),
                    FileTypeEnum.Bin,
                    oldFile?.Mode ?? FileModeEnum.Auto,
                    oldFile?.KindOf ?? "")
                {
                    // 如果文件类型发生变化则更新
                    Type = IsTextFile(filePath) ? FileTypeEnum.Text : FileTypeEnum.Bin
                };

                newManifest.Manifest.Files.Add(manifestFile);
            }

            return newManifest;
        }

        private static bool IsTextFile(string filePath)
        {
            try
            {
                // 简单的文本文件检测（可根据需要扩展）
                var extension = Path.GetExtension(filePath).ToLower();
                return new[] { ".txt", ".xml", ".json", ".config", ".ini" }.Contains(extension);
            }
            catch
            {
                return false;
            }
        }

        private static List<PatchOperation> CalculatePatchOperations(
            ManifestModel oldManifest,
            ManifestModel newManifest,
            CommandLineOptions options)
        {
            var operations = new List<PatchOperation>();
            var patchService = new PatchService();

            // 确保补丁目录存在
            Directory.CreateDirectory(options.PatchOutputPath);

            foreach (var newFile in newManifest.Manifest.Files)
            {
                var oldFile = oldManifest.Manifest.Files
                    .FirstOrDefault(f =>
                        f.UUID == newFile.UUID ||
                        (f.Path == newFile.Path && f.FileName == newFile.FileName));

                if (oldFile == null)
                {
                    // 新增文件
                    operations.Add(new PatchOperation
                    {
                        Type = OperationType.Add,
                        File = newFile,
                        SourcePath = Path.Combine(options.BasePath, newFile.Path, newFile.FileName)
                    });
                    Logger.Info($"检测到新增文件: {newFile.FileName}\n");
                }
                else if (oldFile.MD5 != newFile.MD5)
                {
                    if (oldFile.Mode == FileModeEnum.Skip)
                    {
                        Logger.Warning($"文件配置为跳过补丁: {newFile.FileName}\n");
                        continue;
                    }

                    // 处理文件更新
                    string oldFilePath = Path.Combine(options.OldBasePath, oldFile.Path, oldFile.FileName);
                    string newFilePath = Path.Combine(options.BasePath, newFile.Path, newFile.FileName);
                    string patchFileName = $"{newFile.UUID:N}.hdiff";
                    string patchFilePath = Path.Combine(options.PatchOutputPath, patchFileName);

                    if (!File.Exists(oldFilePath))
                    {
                        Logger.Warning($"旧版本文件不存在，将完整添加: {oldFile.FileName}\n");
                        operations.Add(new PatchOperation
                        {
                            Type = OperationType.Add,
                            File = newFile,
                            SourcePath = newFilePath
                        });
                    }
                    else
                    {
                        // 生成二进制补丁
                        if (patchService.GeneratePatch(oldFilePath, newFilePath, patchFilePath))
                        {
                            operations.Add(new PatchOperation
                            {
                                Type = OperationType.Patch,
                                File = newFile,
                                PatchPath = patchFilePath,
                                PatchSize = new FileInfo(patchFilePath).Length
                            });
                            Logger.Info($"生成补丁: {newFile.FileName} ({FormatSize(new FileInfo(patchFilePath).Length)})\n");
                        }
                        else
                        {
                            Logger.Fail($"补丁生成失败: {newFile.FileName}\n");
                            return null;
                        }
                    }
                }
            }

            // 检测删除的文件
            foreach (var oldFile in oldManifest.Manifest.Files)
            {
                if (!newManifest.Manifest.Files.Any(f => f.UUID == oldFile.UUID))
                {
                    operations.Add(new PatchOperation
                    {
                        Type = OperationType.Delete,
                        File = oldFile
                    });
                    Logger.Info($"检测到删除文件: {oldFile.FileName}\n");
                }
            }

            return operations;
        }

        private static void OutputResults(
            ManifestModel newManifest,
            List<PatchOperation> operations,
            string outputPath)
        {
            Logger.Info($"输出结果到: {outputPath}\n");
            Directory.CreateDirectory(outputPath);

            // 1. 保存新的清单文件
            string manifestPath = Path.Combine(outputPath, "manifest.xml");
            SaveManifest(newManifest, manifestPath);

            // 2. 生成补丁报告
            string reportPath = Path.Combine(outputPath, "patch-report.txt");
            GeneratePatchReport(operations, reportPath);

            // 3. 复制补丁文件到输出目录
            string patchesDir = Path.Combine(outputPath, "patches");
            Directory.CreateDirectory(patchesDir);

            foreach (var op in operations.Where(o => o.Type == OperationType.Patch))
            {
                string destPath = Path.Combine(patchesDir, Path.GetFileName(op.PatchPath));
                File.Copy(op.PatchPath, destPath, true);
            }
        }

        private static void SaveManifest(ManifestModel manifest, string path)
        {
            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = System.Text.Encoding.UTF8
            };

            using (var writer = XmlWriter.Create(path, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("Metadata");
                writer.WriteAttributeString("Version", manifest.Version.ToString());

                // 写入Tags
                writer.WriteStartElement("Tags");
                writer.WriteElementString("UUID", manifest.Tags.UUID.ToString("N"));
                writer.WriteElementString("GenTime", manifest.Tags.GenTime.ToUnixTimeSeconds().ToString());
                writer.WriteElementString("Commit", manifest.Tags.Commit);
                writer.WriteEndElement(); // Tags

                // 写入Manifest
                writer.WriteStartElement("Manifest");
                foreach (var file in manifest.Manifest.Files)
                {
                    writer.WriteStartElement("File");
                    writer.WriteElementString("UUID", file.UUID.ToString("N"));
                    writer.WriteElementString("FileName", file.FileName);
                    writer.WriteElementString("MD5", file.MD5);
                    writer.WriteElementString("Path", file.Path);
                    writer.WriteElementString("Version", file.Version.ToString());
                    writer.WriteElementString("Type", file.Type.ToString());
                    writer.WriteElementString("Mode", file.Mode.ToString());
                    writer.WriteElementString("KindOf", file.KindOf);
                    writer.WriteEndElement(); // File
                }
                writer.WriteEndElement(); // Manifest

                writer.WriteEndElement(); // Metadata
                writer.WriteEndDocument();
            }
        }

        private static void GeneratePatchReport(List<PatchOperation> operations, string path)
        {
            using var writer = new StreamWriter(path);
            writer.WriteLine($"补丁报告生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine($"操作总数: {operations.Count}\n");

            writer.WriteLine("操作详情:");
            foreach (var op in operations)
            {
                writer.WriteLine($"- 类型: {op.Type}");
                writer.WriteLine($"  文件: {op.File?.Path}/{op.File?.FileName}");

                if (op.Type == OperationType.Patch)
                {
                    writer.WriteLine($"  补丁大小: {FormatSize(op.PatchSize)}");
                }
                writer.WriteLine();
            }

            // 计算统计信息
            int addCount = operations.Count(o => o.Type == OperationType.Add);
            int patchCount = operations.Count(o => o.Type == OperationType.Patch);
            int deleteCount = operations.Count(o => o.Type == OperationType.Delete);
            long totalPatchSize = operations.Where(o => o.Type == OperationType.Patch).Sum(o => o.PatchSize);

            writer.WriteLine("\n统计摘要:");
            writer.WriteLine($"新增文件: {addCount}");
            writer.WriteLine($"补丁文件: {patchCount}");
            writer.WriteLine($"删除文件: {deleteCount}");
            writer.WriteLine($"总补丁大小: {FormatSize(totalPatchSize)}");
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
                        case "--old-base":
                            options.OldBasePath = args[++i];
                            break;
                        case "--base":
                            options.BasePath = args[++i];
                            break;
                        case "--patch-output":
                            options.PatchOutputPath = args[++i];
                            break;
                        case "--output":
                            options.OutputPath = args[++i];
                            break;
                        case "--new-version":
                            options.NewVersion = Version.Parse(args[++i]);
                            break;
                        case "--commit":
                            options.CommitMessage = args[++i];
                            break;
                    }
                }

                // 验证必要参数
                if (string.IsNullOrEmpty(options.OldManifestPath)
                    || string.IsNullOrEmpty(options.BasePath)
                    || options.NewVersion == null)
                {
                    throw new Exception("缺少必要参数");
                }

                // 设置默认值
                options.OldBasePath ??= Path.GetDirectoryName(options.OldManifestPath);
                options.PatchOutputPath ??= Path.Combine(Path.GetTempPath(), "updater_patches");
                options.OutputPath ??= Directory.GetCurrentDirectory();

                return options;
            }
            catch (Exception ex)
            {
                Logger.Fail($"参数解析失败: {ex.Message}\n");
                Logger.Info("使用方式:\n");
                Logger.Info("--old-manifest [路径] 旧版本清单文件\n");
                Logger.Info("--old-base [路径]     旧版本文件目录 (可选，默认同manifest目录)\n");
                Logger.Info("--base [路径]         新版本文件目录\n");
                Logger.Info("--new-version [版本]  新版本号 (格式: major.minor.build)\n");
                Logger.Info("--patch-output [路径] 补丁输出目录 (可选，默认临时目录)\n");
                Logger.Info("--output [路径]       最终输出目录 (可选，默认当前目录)\n");
                Logger.Info("--commit [消息]       提交信息 (可选)\n");
                return null;
            }
        }
    }

    // 补丁操作模型
    internal class PatchOperation
    {
        public OperationType Type { get; set; }
        public ManifestFile File { get; set; }
        public string SourcePath { get; set; }    // 用于新增文件
        public string PatchPath { get; set; }     // 用于补丁文件
        public long PatchSize { get; set; }
    }

    internal enum OperationType
    {
        Add,
        Patch,
        Delete
    }

    // 补丁服务实现
    internal class PatchService
    {
        public bool GeneratePatch(string oldFile, string newFile, string patchOutput)
        {
            try
            {
                // 查找 hdiffz 工具位置（实际部署时应配置在PATH中）
                string toolPath = FindPatchTool("hdiffz");
                if (toolPath == null)
                {
                    Logger.Fail("找不到 hdiffz 工具\n");
                    return false;
                }

                var process = new Process
                {
                    StartInfo =
                    {
                        FileName = toolPath,
                        Arguments = $"-f \"{oldFile}\" \"{newFile}\" \"{patchOutput}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                process.Start();
                process.WaitForExit(60000); // 最多等待1分钟

                if (process.ExitCode != 0)
                {
                    Logger.Fail($"hdiffz 执行失败 (代码: {process.ExitCode})\n");
                    Logger.Fail($"错误输出: {process.StandardError.ReadToEnd()}\n");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Fail($"补丁生成异常: {ex.Message}\n");
                return false;
            }
        }

        private string FindPatchTool(string toolName)
        {
            // 1. 检查当前目录
            string currentDir = Path.Combine(Directory.GetCurrentDirectory(), toolName);
            if (File.Exists(currentDir)) return currentDir;

            // 2. 检查系统PATH
            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var dir in path.Split(Path.PathSeparator))
                {
                    string fullPath = Path.Combine(dir, toolName);
                    if (File.Exists(fullPath)) return fullPath;
                }
            }

            return null;
        }
    }
}