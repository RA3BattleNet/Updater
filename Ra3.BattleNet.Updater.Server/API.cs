using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace Ra3.BattleNet.Updater.Server
{
    public static class API
    {
        public static List<PatchOperation> CalculatePatchOperations(
            ManifestModel oldManifest,
            ManifestModel newManifest,
            string oldBasePath,
            string newBasePath,
            string outputPath)
        {
            List<PatchOperation> operations = new List<PatchOperation>();

            Directory.CreateDirectory(outputPath);
            Directory.CreateDirectory(Path.Combine(outputPath, "files"));
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
                    Logger.Info($"检测到新文件: {newFile.FileName}{newFile.UUID}\n");
                    continue;
                }

                // 检测修改文件
                if (oldFile.MD5 != newFile.MD5)
                {
                    if (newFile.Mode == FileModeEnum.Skip)
                    {
                        Logger.Warning($"此文件被标记跳过: {oldFile.FileName} -> {newFile.FileName}\n");
                        continue;
                    }

                    if (newFile.Mode == FileModeEnum.Force)
                    {
                        Logger.Warning($"此文件被标记强制更新：{oldFile.FileName} -> {newFile.FileName}\n");
                        operations.Add(new PatchOperation
                        {
                            Type = OperationTypeEnum.ForceCopy,
                            File = newFile,
                            SourcePath = Path.Combine(newBasePath, newFile.Path.TrimStart('/', '\\'), newFile.FileName),

                        });
                        continue;
                    }

                    string oldFilePath = Path.Combine(Environment.CurrentDirectory, oldBasePath, oldFile.Path.TrimStart('/', '\\'), oldFile.FileName);
                    string newFilePath = Path.Combine(Environment.CurrentDirectory, newBasePath, newFile.Path.TrimStart('/', '\\'), newFile.FileName);

                    if (!File.Exists(oldFilePath))
                    {
                        Logger.Warning($"旧版本文件不存在，提供完整文件: {newFile.FileName}\n");
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
                        string patchFilePath = Path.Combine(outputPath, "files", patchFileName);

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
                else if (oldFile.FileName != newFile.FileName || oldFile.Path != newFile.Path)
                {
                    Logger.Warning($"文件未变化，但是需mv: {Path.Combine(oldFile.Path.TrimStart('/', '\\'), oldFile.FileName)}->{Path.Combine(newFile.Path.TrimStart('/', '\\'), newFile.FileName)}{Environment.NewLine}");
                    Debug.Assert(false, "暂无支持，TO DO");
                    operations.Add(new PatchOperation
                    {
                        Type = OperationTypeEnum.Move,
                        // 移动 TO DO
                        File = newFile
                    });
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

        public static void GeneratePatchPackage(
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
                        File.Copy(op.SourcePath, destPath, true);
                        op.RelativePath = $"files/{op.File.UUID:N}";
                        break;

                    case OperationTypeEnum.Patch:
                        string patchDest = Path.Combine(filesDir, $"{op.File.UUID:N}.hdiff");
                        //File.Copy(op.PatchPath, patchDest, true);
                        op.RelativePath = $"files/{op.File.UUID:N}.hdiff";
                        break;

                    case OperationTypeEnum.Move:
                        Debug.Assert(false, "暂未支持 TO DO");
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
}
