using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;

namespace Ra3.BattleNet.Updater.Server
{
    /// <summary>
    /// 离线补丁包（一次性 A→B 对比）的生成端。
    /// 产物与在线链路使用**同一套寻址约定**：
    ///   完整文件 files/{md5}，补丁 patches/{oldHash}_{newHash}.hdiff。
    /// 补丁包是一个自包含目录：patch-manifest.json + files/ + patches/。
    /// </summary>
    public static class API
    {
        public static List<PatchOperation> CalculatePatchOperations(
            ManifestModel oldManifest,
            ManifestModel newManifest,
            string oldBasePath,
            string newBasePath,
            string outputPath)
        {
            var operations = new List<PatchOperation>();

            var outRoot = Path.GetFullPath(outputPath);
            var filesDir = Path.Combine(outRoot, "files");
            var patchesDir = Path.Combine(outRoot, "patches");
            Directory.CreateDirectory(filesDir);
            Directory.CreateDirectory(patchesDir);
            Logger.Debug($"补丁包输出路径：{outRoot}{Environment.NewLine}");

            var oldRoot = Path.GetFullPath(oldBasePath);
            var newRoot = Path.GetFullPath(newBasePath);

            foreach (var newFile in newManifest.Manifest.Files)
            {
                if (newFile.Mode == FileModeEnum.Skip) continue;

                var newFull = LocalPath(newRoot, newFile);
                var oldFile = oldManifest.Manifest.Files.FirstOrDefault(f => f.UUID == newFile.UUID);

                // 新增文件
                if (oldFile is null)
                {
                    operations.Add(FullCopy(newFile, newFull));
                    Logger.Info($"检测到新文件: {newFile.FileName}{newFile.UUID}\n");
                    continue;
                }

                var oldRel = oldFile.RelativePath();
                var oldFull = LocalPath(oldRoot, oldFile);

                // 内容未变：仅在路径变化时需要移动
                if (string.Equals(oldFile.MD5, newFile.MD5, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(oldRel, newFile.RelativePath(), StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info($"文件未变化但需移动: {oldRel} -> {newFile.RelativePath()}\n");
                        operations.Add(new PatchOperation
                        {
                            Type = OperationTypeEnum.Move,
                            File = newFile,
                            OldFilePath = oldRel,
                            SourcePath = newFull,   // 本地缺失时用它兜底
                            TargetMD5 = newFile.MD5,
                        });
                    }

                    continue;
                }

                // 强制全量、或旧/新文件缺失
                if (newFile.Mode == FileModeEnum.Force || !File.Exists(oldFull) || !File.Exists(newFull))
                {
                    if (!File.Exists(newFull))
                        Logger.Fail($"新版本文件不存在，跳过: {newFull}\n");
                    else
                        Logger.Warning($"强制或缺少旧文件，使用完整文件: {newFile.FileName}\n");

                    if (File.Exists(newFull)) operations.Add(FullCopy(newFile, newFull));
                    continue;
                }

                // 生成补丁（内容对命名，天然去重）
                var patchName = $"{oldFile.MD5}_{newFile.MD5}.hdiff";
                var patchPath = Path.Combine(patchesDir, patchName);

                if (!File.Exists(patchPath) && !PatchGenerater.GeneratePatch(oldFull, newFull, patchPath))
                {
                    Logger.Fail($"补丁生成失败，改用完整文件: {newFile.FileName}\n");
                    if (File.Exists(patchPath)) File.Delete(patchPath);
                    operations.Add(FullCopy(newFile, newFull));
                    continue;
                }

                // **后判断**：补丁不比完整文件小就没有意义
                if (new FileInfo(patchPath).Length >= new FileInfo(newFull).Length)
                {
                    File.Delete(patchPath);
                    Logger.Warning($"补丁不小于完整文件，改用完整文件: {newFile.FileName}\n");
                    operations.Add(FullCopy(newFile, newFull));
                    continue;
                }

                operations.Add(new PatchOperation
                {
                    Type = OperationTypeEnum.Patch,
                    File = newFile,
                    PatchPath = patchPath,
                    PatchSize = new FileInfo(patchPath).Length,
                    SourceMD5 = oldFile.MD5,
                    TargetMD5 = newFile.MD5,
                    OldFilePath = oldRel,
                });
                Logger.Info($"生成补丁: {newFile.FileName} ({FormatSize(new FileInfo(patchPath).Length)})\n");
            }

            return operations;
        }

        private static PatchOperation FullCopy(ManifestFile file, string sourceFull) => new()
        {
            Type = OperationTypeEnum.ForceCopy,
            File = file,
            SourcePath = sourceFull,
            TargetMD5 = file.MD5,
        };

        public static void GeneratePatchPackage(
            List<PatchOperation> operations,
            ManifestModel newManifest,
            string outputPath)
        {
            var outRoot = Path.GetFullPath(outputPath);
            Logger.Info($"创建补丁包到: {outRoot}\n");
            Directory.CreateDirectory(outRoot);

            var filesDir = Path.Combine(outRoot, "files");
            Directory.CreateDirectory(filesDir);

            foreach (var op in operations)
            {
                switch (op.Type)
                {
                    case OperationTypeEnum.ForceCopy:
                    case OperationTypeEnum.Move:
                    {
                        // 内容寻址：同一内容只放一份
                        var dest = Path.Combine(filesDir, op.File.MD5);
                        if (!File.Exists(dest) && op.SourcePath is not null)
                            File.Copy(op.SourcePath, dest, overwrite: true);
                        op.RelativePath = $"files/{op.File.MD5}";
                        break;
                    }

                    case OperationTypeEnum.Patch:
                        // 补丁已按内容对写在 patches/ 下
                        op.RelativePath = $"patches/{Path.GetFileName(op.PatchPath!)}";
                        break;
                }
            }

            var patchManifest = new PatchManifest
            {
                BaseVersion = newManifest.Version.ToString(),
                TargetVersion = newManifest.Version.ToString(),
                Operations = operations.Select(op => new OperationInfo
                {
                    Type = op.Type.ToString().ToLowerInvariant(),
                    FilePath = op.File.RelativePath(),
                    OldFilePath = op.OldFilePath,
                    UUID = op.File.UUID.ToString("N"),
                    RelativePath = op.RelativePath,
                    Size = op.Type == OperationTypeEnum.Patch ? op.PatchSize : 0,
                    SourceMD5 = op.SourceMD5,
                    TargetMD5 = op.TargetMD5,
                }).ToList(),
            };

            var manifestPath = Path.Combine(outRoot, "patch-manifest.json");
            File.WriteAllText(manifestPath, System.Text.Json.JsonSerializer.Serialize(patchManifest,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                }));

            Logger.Success($"补丁清单已生成: {manifestPath}\n");
        }

        private static string LocalPath(string root, ManifestFile file) =>
            Path.Combine(root, file.RelativePath().Replace('/', Path.DirectorySeparatorChar));

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
