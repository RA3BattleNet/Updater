using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;
using System.Text.Json;

namespace Ra3.BattleNet.Updater.Client
{
    /// <summary>
    /// 离线补丁包的应用端。载荷寻址与在线链路一致：
    ///   完整文件 files/{md5}，补丁 patches/{old}_{new}.hdiff。
    /// 每种操作都带 OldFilePath，因此**改名 + 改内容**也能正确地在旧文件上打补丁、
    /// 再写到新路径（AGENT.md §9.2）。
    /// </summary>
    public class API
    {
        public static PatchManifest? LoadPatchManifest(string patchPath)
        {
            try
            {
                var manifestPath = Path.Combine(patchPath, "patch-manifest.json");
                Logger.Info($"加载补丁清单: {manifestPath}\n");
                return JsonSerializer.Deserialize<PatchManifest>(File.ReadAllText(manifestPath));
            }
            catch (Exception ex)
            {
                Logger.Fail($"补丁清单加载失败: {ex.Message}\n");
                return null;
            }
        }

        public static bool ApplyPatchOperations(PatchManifest patchManifest, string targetPath, string patchPath)
        {
            var targetRoot = Path.GetFullPath(targetPath);
            var packageRoot = Path.GetFullPath(patchPath);
            var allSuccess = true;

            foreach (var operation in patchManifest.Operations ?? [])
            {
                try
                {
                    var targetFull = Resolve(targetRoot, operation.FilePath);
                    var payload = operation.RelativePath is null ? null : Resolve(packageRoot, operation.RelativePath);
                    var oldFull = Resolve(targetRoot, operation.OldFilePath ?? operation.FilePath);

                    switch (operation.Type?.ToLowerInvariant())
                    {
                        case "forcecopy":
                        case "copy":
                            Logger.Info($"添加文件: {operation.FilePath}\n");
                            EnsureDir(targetFull);
                            File.Copy(payload!, targetFull, overwrite: true);
                            Verify(targetFull, operation.TargetMD5, operation.FilePath);
                            break;

                        case "patch":
                        {
                            Logger.Info($"应用补丁: {operation.FilePath}\n");
                            EnsureDir(targetFull);
                            var tempFile = Path.Combine(Path.GetTempPath(), "upd-" + Guid.NewGuid().ToString("N"));

                            if (!PatchApplyer.ApplyPatch(oldFull, payload!, tempFile))
                                throw new InvalidOperationException("补丁应用失败");

                            Verify(tempFile, operation.TargetMD5, operation.FilePath);
                            File.Move(tempFile, targetFull, overwrite: true);
                            break;
                        }

                        case "move":
                        {
                            Logger.Info($"移动文件: {operation.OldFilePath} -> {operation.FilePath}\n");
                            EnsureDir(targetFull);

                            if (File.Exists(oldFull) && !SamePath(oldFull, targetFull))
                            {
                                File.Move(oldFull, targetFull, overwrite: true);
                            }
                            else if (!File.Exists(targetFull))
                            {
                                // 本地旧文件不在：退化用包内载荷（内容未变，等价于完整文件）
                                File.Copy(payload!, targetFull, overwrite: true);
                            }

                            Verify(targetFull, operation.TargetMD5, operation.FilePath);
                            break;
                        }

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

        private static void Verify(string path, string? expectedMd5, string? label)
        {
            if (string.IsNullOrEmpty(expectedMd5)) return;

            var actual = Convert.ToHexStringLower(PublicMethod.GetMD5(path));
            if (!string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"MD5校验失败 ({label}) 预期 {expectedMd5} 实际 {actual}");
        }

        private static void EnsureDir(string fullPath)
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        private static string Resolve(string root, string? relative)
        {
            if (string.IsNullOrEmpty(relative)) throw new InvalidOperationException("缺少路径字段");
            return Path.GetFullPath(Path.Combine(root, relative.Replace('\\', '/').TrimStart('/')));
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
