using Ra3.BattleNet.Updater.Client.PatchIndexApplyer.Models;
using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace Ra3.BattleNet.Updater.Client.PatchIndexApplyer
{
    internal class Program
    {
        internal class CommandLineOptions
        {
            public string? localRootPath { get; set; }
            public string? localManifestPath { get; set; }
            public string? NewestManifestPath { get; set; }
            public string? NewestPatchesPath { get; set; }
            public string? NewestDownloadPath { get; set; }
            public string? Relaunch { get; set; }

            public void ShowUsage()
            {
                Console.Write("使用方式:\n");
                Console.Write("--local-rootPath <本地待更新文件夹路径>\n" +
                    "--local-manifestPath <本地manifest文件路径>\n" +
                    "--newest-manifestPath <最新的manifest文件路径或URL>\n" +
                    "--newest-patchesPath <最新的patches文件路径或URL>\n" +
                    "--newest-downloadPath <最新的download文件路径或URL>\n" +
                    "--relaunch <更新完成后启动的exe路径>\n" +
                    "--debug  输出更多日志\n");
            }
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
                            case "--relaunch":
                                options.Relaunch = args[++i];
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
                        options.ShowUsage();
                        Environment.Exit(-1);
                    }
                    return options;
                }
                catch (Exception ex)
                {
                    Logger.Fail($"参数无法解析{Environment.NewLine}Msg:{ex.Message}");
                    options.ShowUsage();
                    Environment.Exit(-2);
                }
                return options;
            }
        }

        private static readonly string _cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UpdaterCache");
        private static CommandLineOptions _options = new CommandLineOptions();

        static async Task Main(string[] args)
        {
            if (args.Contains("--help"))
            {
                _options.ShowUsage();
                Environment.Exit(-1);
            }
#if DEBUG
            Logger.IsDebug = true;
#else
            Logger.IsDebug = false;
#endif
            Logger.IsDebug = args.Contains("--debug");
            Logger.Info("Client Updater Starting\n");
            Logger.Debug($"args: {string.Join(" ", args)}{Environment.NewLine}");

            _options = CommandLineParser.Parse(args);

            ForceCleanCacheDirectory(); // 清理cache文件夹并存留警告文件

            // 先获取最新manifest并与本地进行比对，相同则认为已是最新，无需更新
            // 若不同则会尝试读取patches以找到所需文件的patch信息
            // 若有记录则下载patch并补丁，如果无记录则直接下载文件

            Debug.Assert(_options.localRootPath != null);
            Debug.Assert(Path.Exists(_options.localRootPath));
            Debug.Assert(_options.localManifestPath != null);

            Debug.Assert(_options.NewestManifestPath != null);
            Debug.Assert(_options.NewestPatchesPath != null);
            Debug.Assert(_options.NewestDownloadPath != null);
            try
            {
                //获取最新manifest
                ManifestModel newestManifest = new ManifestModel(_options.NewestManifestPath);
                if (newestManifest == null)
                {
                    Logger.Fail("最新Manifest获取失败\n");
                    Environment.Exit(-3);
                    return;
                }

                Guid nuuid = newestManifest.Tags.UUID;
                Logger.Success("最新Manifest已读取\n");

                //获取本地manifest
                ManifestModel localManifest;
                if (File.Exists(_options.localManifestPath))
                {
                    localManifest = new ManifestModel(_options.localManifestPath);
                }
                else
                {
                    Logger.Info("本地Manifest未找到，视为首次运行\n");
                    localManifest = new ManifestModel(new Version("1.0.0"));
                }
                Logger.Success("本地Manifest已读取\n");

                Guid luuid = localManifest.Tags.UUID;

                // 比对生成UUID
                if (luuid == nuuid)
                {
                    Logger.Info("已是最新版本\n");
                    RelaunchAndExit();
                    return;
                }
                Logger.Info($"发现新版本：{nuuid}（当前版本：{luuid}），开始尝试增量更新\n");

                string patchesStr = await LoadJsonAsync(_options.NewestPatchesPath);
                if (string.IsNullOrEmpty(patchesStr))
                {
                    Logger.Fail("无法获取patches.json文件内容\n");
                    Environment.Exit(-5);
                    return;
                }
                PatchIndex patchesContent = new PatchIndex(patchesStr);
                if (patchesContent == null)
                {
                    Logger.Fail("无法解析patches.json文件内容\n");
                    Environment.Exit(-6);
                    return;
                }

                Logger.Info("成功获取patches.json文件，准备应用更新");
                bool NewestFlag = true;
                // 遍历最新manifest中的所有文件
                foreach (var newFile in newestManifest.Manifest.Files)
                {
                    var localFile = localManifest.Manifest.Files.FirstOrDefault(f => f.UUID == newFile.UUID);

                    // 计算本地文件的MD5（如果存在）
                    string localMd5 = null;
                    string localFilePath = System.IO.Path.Combine(_options.localRootPath, localFile?.Path.TrimStart('/', '\\') ?? "", localFile?.FileName ?? "");

                    if (localFile == null)
                    {
                        // 新文件，直接下载完整文件
                        Logger.Info($"本地不存在新文件 {newFile.FileName}，将直接下载完整文件\n");
                        string DownloadedNewestFilePath = Path.Combine(_cachePath, newFile.MD5);
                        string newestTargetFilePath = System.IO.Path.Combine(_options.localRootPath, newFile.Path.TrimStart('/', '\\'), newFile.FileName.TrimStart('/', '\\'));

                        try
                        {
                            // 下载完整文件
                            await DownloadFileAsync(DownloadType.files, newFile.MD5);

                            // 验证文件MD5
                            string downloadedMd5;
                            using (var md5 = System.Security.Cryptography.MD5.Create())
                            {
                                using (var stream = File.OpenRead(DownloadedNewestFilePath))
                                {
                                    var hash = md5.ComputeHash(stream);
                                    downloadedMd5 = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                                }
                            }

                            if (downloadedMd5 == newFile.MD5)
                            {
                                // 确保目录存在
                                Directory.CreateDirectory(Path.GetDirectoryName(newestTargetFilePath));

                                // 移动文件到目标位置
                                File.Copy(DownloadedNewestFilePath, newestTargetFilePath, true);
                                Logger.Info($"文件 {newFile.FileName} 完整下载成功\n");
                            }
                            else
                            {
                                Logger.Fail($"文件 {newFile.FileName} 下载后MD5验证失败\n");
                                NewestFlag = false;
                            }

                            //File.Delete(DownloadedNewestFilePath);
                        }
                        catch (Exception ex)
                        {
                            Logger.Fail($"文件 {newFile.FileName} 下载失败: {ex.Message}\n");
                            NewestFlag = false;
                        }
                    }
                    else
                    {
                        if (localFile != null && File.Exists(localFilePath))
                        {
                            using (var md5 = System.Security.Cryptography.MD5.Create())
                            {
                                using (var stream = File.OpenRead(localFilePath))
                                {
                                    var hash = md5.ComputeHash(stream);
                                    localMd5 = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                                }
                            }
                        }

                        // 已经最新
                        if (localMd5 == newFile.MD5)
                        {
                            Logger.Debug($"文件 {newFile.FileName} 已是最新，跳过更新\n");
                            continue;
                        }
                        Logger.Info($"文件 {newFile.FileName} 正在更新\n");

                        // 尝试查找补丁
                        Guid? applicablePatchGuidFlag = new Guid();
                        if (localFile != null && !string.IsNullOrEmpty(localMd5))
                        {
                            // 使用文件UUID、旧MD5和新MD5查找补丁文件（有且最多只会找到一个）
                            applicablePatchGuidFlag = patchesContent.FindPatch(localFile.UUID, localMd5, newFile.MD5);
                        }

                        // 确保目标目录存在

                        // 旧文件目录路径
                        string localTargetDir = System.IO.Path.Combine(_options.localRootPath, localFile.Path.TrimStart('/', '\\'));
                        Logger.Debug($"旧文件目录路径: {localTargetDir}\n");
                        if (!Directory.Exists(localTargetDir))
                        {
                            Directory.CreateDirectory(localTargetDir);
                        }
                        // 旧文件路径
                        string localTargetFilePath = System.IO.Path.Combine(localTargetDir, localFile.FileName.TrimStart('/', '\\'));
                        Logger.Debug($"旧文件路径: {localTargetFilePath}\n");

                        // 新文件目录路径
                        string newestTargetDir = System.IO.Path.Combine(_options.localRootPath, newFile.Path.TrimStart('/', '\\'));
                        Logger.Debug($"新文件目录路径: {newestTargetDir}\n");
                        if (!Directory.Exists(newestTargetDir))
                        {
                            Directory.CreateDirectory(newestTargetDir);
                        }
                        // 新文件路径
                        string newestTargetFilePath = System.IO.Path.Combine(newestTargetDir, newFile.FileName.TrimStart('/', '\\'));
                        Logger.Debug($"新文件路径: {newestTargetFilePath}\n");

                        Debug.Assert(!String.IsNullOrEmpty(localTargetDir));
                        Debug.Assert(!String.IsNullOrEmpty(localTargetFilePath));
                        Debug.Assert(!String.IsNullOrEmpty(newestTargetDir));
                        Debug.Assert(!String.IsNullOrEmpty(newestTargetFilePath));

                        if (applicablePatchGuidFlag != null)
                        {
                            Guid PatchNameGuid = applicablePatchGuidFlag.Value;
                            // 尝试使用补丁更新
                            Logger.Note($"找到适用补丁，尝试使用增量更新，下载文件：{PatchNameGuid.ToString("N")}\n");

                            try
                            {
                                if (!await DownloadFileAsync(DownloadType.patches, PatchNameGuid.ToString("N")))
                                {
                                    Logger.Fail($"下载失败，目标信息：{PatchNameGuid}");
                                    throw new Exception("补丁下载失败");
                                }
                                string DiffFilePath = Path.Combine(_cachePath, PatchNameGuid.ToString("N"));
                                // 应用补丁
                                bool success = PatchApplyer.ApplyPatch(localTargetFilePath, DiffFilePath, newestTargetFilePath);

                                if (success)
                                {
                                    // 验证新文件的MD5
                                    string newFileMd5;
                                    using (var md5 = System.Security.Cryptography.MD5.Create())
                                    {
                                        using (var stream = File.OpenRead(newestTargetFilePath))
                                        {
                                            var hash = md5.ComputeHash(stream);
                                            newFileMd5 = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                                        }
                                    }

                                    if (newFileMd5 == newFile.MD5)
                                    {
                                        Logger.Info($"文件 {newFile.FileName} 增量更新成功\n");
                                    }
                                    else
                                    {
                                        Logger.Warning($"文件 {newFile.FileName} 补丁应用后MD5验证失败，将尝试完整下载\n");
                                        Debug.Assert(false, "此代码不应遇到，此种情况证明发生了意料之外的逻辑故障");
                                        applicablePatchGuidFlag = null; // 触发完整下载
                                    }
                                }
                                else
                                {
                                    Logger.Warning($"文件 {newFile.FileName} 补丁应用失败，将尝试完整下载\n");
                                    applicablePatchGuidFlag = null; // 触发完整下载
                                }

                                // 清理临时文件
                                if (localTargetFilePath != newestTargetFilePath)
                                    File.Delete(localTargetFilePath);
                                //File.Delete(DiffFilePath);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning($"文件 {newFile.FileName} 补丁更新过程出错: {ex.Message}，将尝试完整下载\n");
                                applicablePatchGuidFlag = null; // 触发完整下载
                            }
                        }

                        // 如果没有适用补丁，下载完整文件
                        if (applicablePatchGuidFlag == null)
                        {
                            Logger.Info($"未找到适用补丁，将下载完整文件: {newFile.FileName}\n");
                            string DownloadedNewestFilePath = Path.Combine(_cachePath, newFile.MD5);

                            try
                            {
                                // 下载完整文件
                                await DownloadFileAsync(DownloadType.files, newFile.MD5);

                                // 验证文件MD5
                                string downloadedMd5;
                                using (var md5 = System.Security.Cryptography.MD5.Create())
                                {
                                    using (var stream = File.OpenRead(DownloadedNewestFilePath))
                                    {
                                        var hash = md5.ComputeHash(stream);
                                        downloadedMd5 = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                                    }
                                }

                                if (downloadedMd5 == newFile.MD5)
                                {
                                    // 确保目录存在
                                    Directory.CreateDirectory(newestTargetDir);

                                    // 移动文件到目标位置
                                    File.Copy(DownloadedNewestFilePath, newestTargetFilePath, true);
                                    Logger.Info($"文件 {newFile.FileName} 完整下载成功\n");
                                }
                                else
                                {
                                    Logger.Fail($"文件 {newFile.FileName} 下载后MD5验证失败\n");
                                    NewestFlag = false;
                                }

                                // 清理临时文件
                                if (localTargetFilePath != newestTargetFilePath)
                                    File.Delete(localTargetFilePath);
                                //File.Delete(DownloadedNewestFilePath);
                            }
                            catch (Exception ex)
                            {
                                Logger.Fail($"文件 {newFile.FileName} 下载失败: {ex.Message}\n");
                                NewestFlag = false;
                            }
                        }
                    }

                    // 更新本地manifest中的文件信息
                    if (localFile != null)
                    {
                        localManifest.Manifest.Files.Remove(localFile);
                    }
                    localManifest.Manifest.Files.Add(newFile);
                }

                if (NewestFlag)
                {
                    // 保存更新后的本地manifest
                    try
                    {
                        newestManifest.SaveToXml(_options.localManifestPath);
                        //File.Copy(_options.NewestManifestPath, _options.localManifestPath, true);
                        Logger.Success("本地manifest已最新\n");
                    }
                    catch (Exception ex)
                    {
                        Logger.Fail($"保存本地manifest失败: {ex.Message}\n");
                    }
                    localManifest.Tags.UUID = newestManifest.Tags.UUID;
                    Logger.Success("所有文件均已成功更新到最新版本\n");
                    Logger.Success("更新完成\n");
                }
                else
                {
                    try
                    {
                        localManifest.SaveToXml(_options.localManifestPath);
                        //File.Copy(_options.NewestManifestPath, _options.localManifestPath, true);
                        Logger.Info("manifest已部分更新\n");
                    }
                    catch (Exception ex)
                    {
                        Logger.Fail($"保存本地manifest失败: {ex.Message}\n");
                    }
                    Logger.Warning("更新未完成\n");

                }

                Logger.Success("更新程序执行完毕\n");
                RelaunchAndExit();
            }
            catch (Exception ex)
            {
                Logger.Fail($"更新过程发生错误：{ex.Message}\n");
                RelaunchAndExit();
            }
        }

        private static void RelaunchAndExit()
        {
            if (!string.IsNullOrEmpty(_options.Relaunch))
            {
                Logger.Info($"拉起: {_options.Relaunch}\n");
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _options.Relaunch,
                        WorkingDirectory = _options.localRootPath!,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Logger.Fail($"拉起失败: {ex.Message}\n");
                }
            }
            Environment.Exit(0);
        }
        internal enum DownloadType
        {
            patches,
            files
        }

        public static async Task<bool> DownloadFileAsync(DownloadType downloadType, String FileName)
        {
            using (HttpClient client = new HttpClient())
            {
                try
                {
                    // 构建下载URL，确保URL的正确格式
                    string baseUrl = _options.NewestDownloadPath.TrimEnd('/');
                    string FullDownloadUrl = $"{baseUrl}/{downloadType.ToString()}/{FileName}";
                    using (var response = await client.GetAsync(FullDownloadUrl))
                    {
                        response.EnsureSuccessStatusCode();
                        string DiffFilePath = Path.Combine(_cachePath, FileName);
                        using (var fs = new FileStream(DiffFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await response.Content.CopyToAsync(fs);
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Fail($"{downloadType.ToString()}方式下载文件失败: {FileName}, 错误: {ex.Message}");
                    return false;
                }
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


        public static void ForceCleanCacheDirectory()
        {
            if (Directory.Exists(_cachePath))
            {
                try
                {
                    Directory.Delete(_cachePath, true);
                }
                catch (Exception ex)
                {
                    Logger.Fail($"删除缓存目录时出错: {ex.Message}");
                    return;
                }
            }

            // 重新创建目录
            Directory.CreateDirectory(_cachePath);
            string warningFilePath = Path.Combine(_cachePath, "请勿将任何重要文件放在此处.txt");
            string warningContent = @"此目录为客户端增量更新程序缓存文件存放处
====================================
此目录由程序自动管理，包含临时下载的更新文件和补丁。
请不要手动修改或删除此目录中的文件，程序会自动清理过期的缓存文件。

如果需要手动清理，可以安全删除此目录中的所有内容。
";
            File.WriteAllText(warningFilePath, warningContent, Encoding.UTF8);
            Logger.Info("已清理缓存目录");
        }
    }

}