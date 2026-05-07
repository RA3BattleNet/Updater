using Ra3.BattleNet.Updater.Client.PatchIndexApplyer.Models;
using Ra3.BattleNet.Updater.Share.Log;
using Ra3.BattleNet.Updater.Share.Models;
using Ra3.BattleNet.Updater.Share.Utilities;
using System.Diagnostics;
using System.Text;

namespace Ra3.BattleNet.Updater.Client.PatchIndexApplyer;

public class PatchIndexApplyerEngine
{
    public record Options(
        string LocalRootPath,
        string LocalManifestPath,
        string NewestManifestPath,
        string NewestPatchesPath,
        string NewestDownloadPath
    );

    public record ProgressReport(int Current, int Total, string FileName, string Status);

    private readonly Options _options;
    private readonly IProgress<ProgressReport>? _progress;
    private readonly string _cachePath;

    public PatchIndexApplyerEngine(Options options, IProgress<ProgressReport>? progress = null)
    {
        _options = options;
        _progress = progress;
        _cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UpdaterCache");
    }

    /// <summary>运行更新流程，返回 true 表示更新成功或已最新。</summary>
    public async Task<bool> RunAsync()
    {
        ForceCleanCacheDirectory();

        ManifestModel newestManifest;
        try
        {
            newestManifest = new ManifestModel(_options.NewestManifestPath);
        }
        catch
        {
            Logger.Fail("最新Manifest获取失败\n");
            return false;
        }

        ManifestModel localManifest;
        if (File.Exists(_options.LocalManifestPath))
        {
            localManifest = new ManifestModel(_options.LocalManifestPath);
        }
        else
        {
            Logger.Info("本地Manifest未找到，视为首次运行\n");
            localManifest = new ManifestModel(new Version("1.0.0"));
        }

        if (localManifest.Tags.UUID == newestManifest.Tags.UUID)
        {
            Logger.Info("已是最新版本\n");
            return true;
        }

        Logger.Info($"发现新版本：{newestManifest.Tags.UUID:N}，开始增量更新\n");

        var patchesJson = await LoadJsonAsync(_options.NewestPatchesPath);
        if (string.IsNullOrEmpty(patchesJson))
        {
            Logger.Fail("无法获取patches.json\n");
            return false;
        }

        var patchIndex = new PatchIndex(patchesJson);
        var files = newestManifest.Manifest.Files;
        var total = files.Count;
        var allSuccess = true;

        for (var i = 0; i < files.Count; i++)
        {
            var newFile = files[i];

            if (newFile.Mode == FileModeEnum.Skip) continue;

            var fileDir = newFile.Path.TrimStart('/', '\\');
            if (fileDir == "CoronaData" ||
                fileDir.StartsWith("CoronaData/") ||
                fileDir.StartsWith("CoronaData\\"))
                continue;

            if (fileDir == "dotnet" ||
                fileDir.StartsWith("dotnet/") ||
                fileDir.StartsWith("dotnet\\"))
                continue;

            Report(i, total, newFile.FileName, "检查中");

            var localFile = localManifest.Manifest.Files.FirstOrDefault(f => f.UUID == newFile.UUID);
            string? localFilePath = null;
            if (localFile != null)
            {
                localFilePath = Path.Combine(_options.LocalRootPath,
                    localFile.Path.TrimStart('/', '\\'), localFile.FileName);
            }

            if (localFile == null || localFilePath == null || !File.Exists(localFilePath))
            {
                Report(i, total, newFile.FileName, "下载");
                var ok = await DownloadAndCopyAsync(_cachePath, newFile.MD5, newFile, localFile);
                if (!ok) allSuccess = false;
            }
            else
            {
                var localMd5 = ComputeMD5(localFilePath);
                if (localMd5 == newFile.MD5)
                {
                    Report(i, total, newFile.FileName, "跳过");
                    continue;
                }

                var patchGuid = patchIndex.FindPatch(newFile.UUID, localMd5, newFile.MD5);
                if (patchGuid != null)
                {
                    Report(i, total, newFile.FileName, "增量补丁");
                    var ok = await ApplyPatchAsync(patchGuid.Value, localFilePath, newFile, localFile);
                    if (ok) continue;
                }

                Report(i, total, newFile.FileName, "下载");
                var downloadOk = await DownloadAndCopyAsync(_cachePath, newFile.MD5, newFile, localFile);
                if (!downloadOk) allSuccess = false;
            }
        }

        if (allSuccess)
        {
            newestManifest.SaveToXml(_options.LocalManifestPath);
            Logger.Success($"更新完成，已更新本地 Manifest，版本 {newestManifest.Tags.UUID:N}\n");
        }
        else
        {
            Logger.Fail("部分文件更新失败，未更新本地 Manifest\n");
        }

        Report(total, total, "", allSuccess ? "完成" : "失败");
        return allSuccess;
    }

    private async Task<bool> DownloadAndCopyAsync(string cacheDir, string md5, ManifestFile newFile, ManifestFile? localFile)
    {
        var tmpPath = Path.Combine(cacheDir, md5);
        try
        {
            await DownloadFileAsync(DownloadType.files, md5);

            var downloadedMd5 = ComputeMD5(tmpPath);
            if (downloadedMd5 != newFile.MD5)
            {
                Logger.Fail($"文件 {newFile.FileName} MD5校验失败\n");
                return false;
            }

            var targetDir = Path.Combine(_options.LocalRootPath, newFile.Path.TrimStart('/', '\\'));
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, newFile.FileName);
            File.Copy(tmpPath, targetPath, true);
            Logger.Info($"文件 {newFile.FileName} 下载成功\n");

            if (localFile != null && localFile.FileName != newFile.FileName &&
                File.Exists(Path.Combine(_options.LocalRootPath, localFile.Path.TrimStart('/', '\\'), localFile.FileName)))
            {
                File.Delete(Path.Combine(_options.LocalRootPath, localFile.Path.TrimStart('/', '\\'), localFile.FileName));
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Fail($"文件 {newFile.FileName} 下载失败: {ex.Message}\n");
            return false;
        }
    }

    private async Task<bool> ApplyPatchAsync(Guid patchGuid, string localFilePath, ManifestFile newFile, ManifestFile localFile)
    {
        try
        {
            var patchName = patchGuid.ToString("N");
            if (!await DownloadFileAsync(DownloadType.patches, patchName))
            {
                Logger.Warning($"补丁下载失败: {patchName}\n");
                return false;
            }

            var diffPath = Path.Combine(_cachePath, patchName);
            var targetPath = Path.Combine(_options.LocalRootPath,
                newFile.Path.TrimStart('/', '\\'), newFile.FileName);
            var targetDir = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(targetDir);

            if (!PatchApplyer.ApplyPatch(localFilePath, diffPath, targetPath))
            {
                Logger.Warning($"补丁应用失败\n");
                return false;
            }

            if (ComputeMD5(targetPath) != newFile.MD5)
            {
                Logger.Warning($"补丁后MD5校验失败\n");
                return false;
            }

            Logger.Info($"文件 {newFile.FileName} 增量更新成功\n");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"补丁过程异常: {ex.Message}\n");
            return false;
        }
    }


    internal enum DownloadType { patches, files }

    private async Task<bool> DownloadFileAsync(DownloadType type, string fileName)
    {
        using var http = new HttpClient();
        try
        {
            var baseUrl = _options.NewestDownloadPath.TrimEnd('/');
            var url = $"{baseUrl}/{type}/{fileName}";
            var response = await http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var dest = Path.Combine(_cachePath, fileName);
            using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fs);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Fail($"下载 {type}/{fileName} 失败: {ex.Message}");
            return false;
        }
    }

    private static async Task<string> LoadJsonAsync(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var http = new HttpClient();
            return await http.GetStringAsync(uri);
        }
        if (!File.Exists(path))
            throw new FileNotFoundException($"文件未找到: {path}");
        return await File.ReadAllTextAsync(path);
    }

    private static string ComputeMD5(string path)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private void ForceCleanCacheDirectory()
    {
        if (Directory.Exists(_cachePath))
        {
            try { Directory.Delete(_cachePath, true); }
            catch (Exception ex) { Logger.Fail($"删除缓存目录出错: {ex.Message}"); return; }
        }
        Directory.CreateDirectory(_cachePath);
        File.WriteAllText(Path.Combine(_cachePath, "请勿将任何重要文件放在此处.txt"),
            "此目录为客户端增量更新程序缓存文件存放处\n" +
            "====================================\n" +
            "此目录由程序自动管理，包含临时下载的更新文件和补丁。\n" +
            "请不要手动修改或删除此目录中的文件，程序会自动清理过期的缓存文件。\n\n" +
            "如果需要手动清理，可以安全删除此目录中的所有内容。\n",
            Encoding.UTF8);
    }

    private void Report(int current, int total, string fileName, string status)
    {
        _progress?.Report(new ProgressReport(current, total, fileName, status));
    }
}
