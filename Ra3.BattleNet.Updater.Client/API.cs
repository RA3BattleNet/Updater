using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;
using System.Text.Json;

namespace Ra3.BattleNet.Updater.Client
{
    public class API
    {
        public static PatchManifest LoadPatchManifest(string patchPath)
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

        public static bool ApplyPatchOperations(
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

                        //case "delete":
                        //    Logger.Info($"删除文件: {operation.FilePath}\n");
                        //    if (File.Exists(fullTargetPath))
                        //    {
                        //        File.Delete(fullTargetPath);
                        //    }
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

}
