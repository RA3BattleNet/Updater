#include "engine.h"
#include "hash.h"
#include "download.h"
#include "patch.h"
#include <windows.h>
#include <filesystem>
#include <format>
#include <cstdio>

namespace fs = std::filesystem;

// ============================================================
//  Helper
// ============================================================
static std::wstring ModuleDir()
{
    wchar_t buf[MAX_PATH];
    DWORD len = GetModuleFileNameW(nullptr, buf, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) return {};
    return fs::path(buf).parent_path().wstring();
}

bool UpdateEngine::IsUrl(const std::wstring& s)
{
    return s.find(L"http://") == 0 || s.find(L"https://") == 0;
}

std::wstring UpdateEngine::GetCacheDir() const
{
    return m_cacheDir;
}

// ============================================================
//  构造 / 析构
// ============================================================
UpdateEngine::UpdateEngine(const EngineConfig& cfg, const ProgressCallback& progress)
    : m_cfg(cfg), m_progress(progress)
{
    m_cacheDir = (fs::path(ModuleDir()) / L"UpdaterCache").wstring();
}

// ============================================================
//  远程文件加载（支持 URL 和本地路径）
// ============================================================
bool UpdateEngine::LoadManifest(const std::wstring& source, UpdateManifest& m) const
{
    if (IsUrl(source))
    {
        auto tmp = (fs::path(m_cacheDir) / L"remote_manifest.xml").wstring();
        fs::create_directories(fs::path(m_cacheDir));
        if (!DownloadUrl(source, tmp, 10000))
        {
            printf("  manifest 下载失败\n");
            return false;
        }
        return m.LoadFromXml(tmp);
    }
    return m.LoadFromXml(source);
}

bool UpdateEngine::LoadPatches(const std::wstring& source, PatchIndex& pi) const
{
    if (IsUrl(source))
    {
        auto tmp = (fs::path(m_cacheDir) / L"patches.json").wstring();
        fs::create_directories(fs::path(m_cacheDir));
        if (!DownloadUrl(source, tmp, 10000))
        {
            printf("  patches.json 下载失败\n");
            return false;
        }
        return pi.LoadFromFile(tmp);
    }
    return pi.LoadFromFile(source);
}

// ============================================================
//  文件 / 补丁下载
// ============================================================
bool UpdateEngine::DownloadFullFile(const std::wstring& uuid, const std::wstring& dest) const
{
    auto url = m_cfg.newestDownloadUrl + L"files/" + uuid;
    return DownloadUrl(url, dest, 120000);
}

bool UpdateEngine::DownloadPatch(const std::wstring& patchName, const std::wstring& dest) const
{
    auto url = m_cfg.newestDownloadUrl + L"patches/" + patchName;
    return DownloadUrl(url, dest, 120000);
}

// ============================================================
//  缓存管理
// ============================================================
void UpdateEngine::ForceCleanCache()
{
    std::error_code ec;
    if (fs::exists(m_cacheDir))
        fs::remove_all(m_cacheDir, ec);
    fs::create_directories(m_cacheDir, ec);
}

// ============================================================
//  进度报告
// ============================================================
void UpdateEngine::Report(int current, int total,
                          const std::wstring& fileName,
                          const std::wstring& status) const
{
    if (m_progress)
        m_progress({current, total, fileName, status});
}

// ============================================================
//  全量下载 + 复制（对标 DownloadAndCopyAsync）
// ============================================================
bool UpdateEngine::DownloadAndCopy(const ManifestFileEntry& newFile,
                                   const ManifestFileEntry* localFile)
{
    auto tmpPath = (fs::path(m_cacheDir) / newFile.uuid).wstring();

    if (!DownloadFullFile(newFile.uuid, tmpPath))
    {
        printf("  下载失败: %ws\n", newFile.fileName.c_str());
        return false;
    }

    auto dlMd5 = ComputeFileMd5(tmpPath);
    if (dlMd5.empty() || dlMd5 != newFile.md5)
    {
        printf("  MD5校验失败: %ws (期望 %ws, 得到 %ws)\n",
               newFile.fileName.c_str(), newFile.md5.c_str(), dlMd5.c_str());
        return false;
    }

    auto targetPath = (fs::path(m_cfg.localRootPath) / newFile.FullPath()).wstring();
    fs::create_directories(fs::path(targetPath).parent_path());

    std::error_code ec;
    fs::rename(tmpPath, targetPath, ec);
    if (ec)
    {
        fs::copy_file(tmpPath, targetPath, fs::copy_options::overwrite_existing, ec);
        if (ec)
        {
            printf("  写入失败: %ws\n", newFile.fileName.c_str());
            return false;
        }
    }

    // 清理旧路径文件（文件名变更时）
    if (localFile && localFile->FullPath() != newFile.FullPath())
    {
        auto oldPath = (fs::path(m_cfg.localRootPath) / localFile->FullPath()).wstring();
        fs::remove(oldPath, ec);
    }

    return true;
}

// ============================================================
//  增量补丁 + 应用（对标 ApplyPatchAsync）
// ============================================================
bool UpdateEngine::ApplyAndCopy(const std::wstring& patchGuid,
                                const std::wstring& localFilePath,
                                const ManifestFileEntry& newFile,
                                const ManifestFileEntry* localFile)
{
    auto patchPath = (fs::path(m_cacheDir) / (patchGuid + L".patch")).wstring();

    if (!DownloadPatch(patchGuid, patchPath))
    {
        printf("  补丁下载失败: %ws\n", patchGuid.c_str());
        return false;
    }

    auto tempOutput = (fs::path(m_cacheDir) / (patchGuid + L".out")).wstring();

    if (!ApplyPatch({}, patchPath, localFilePath, tempOutput))
    {
        printf("  补丁应用失败: %ws\n", newFile.fileName.c_str());
        return false;
    }

    auto outputMd5 = ComputeFileMd5(tempOutput);
    if (outputMd5.empty() || outputMd5 != newFile.md5)
    {
        printf("  补丁后MD5校验失败: %ws (期望 %ws, 得到 %ws)\n",
               newFile.fileName.c_str(), newFile.md5.c_str(), outputMd5.c_str());
        return false;
    }

    // 替换原文件（带重试逻辑）
    auto targetPath = (fs::path(m_cfg.localRootPath) / newFile.FullPath()).wstring();
    fs::create_directories(fs::path(targetPath).parent_path());

    bool replaced = false;
    for (int r = 0; r < 3; r++)
    {
        std::error_code ec;
        fs::rename(localFilePath, localFilePath + L".bak", ec);
        fs::rename(tempOutput, targetPath, ec);
        if (!ec) { replaced = true; break; }
        if (fs::exists(localFilePath + L".bak"))
            fs::rename(localFilePath + L".bak", localFilePath, ec);
        Sleep(500);
    }

    if (!replaced)
    {
        printf("  文件替换失败: %ws\n", newFile.fileName.c_str());
        return false;
    }

    // 清理旧路径文件（文件名变更时）
    if (localFile && localFile->FullPath() != newFile.FullPath())
    {
        auto oldPath = (fs::path(m_cfg.localRootPath) / localFile->FullPath()).wstring();
        std::error_code ec;
        fs::remove(oldPath, ec);
    }
    fs::remove(localFilePath + L".bak");

    return true;
}

// ============================================================
//  Run — 主更新流程（对标 RunAsync）
// ============================================================
bool UpdateEngine::Run()
{
    ForceCleanCache();

    // ---- 1. 加载远程 manifest ----
    UpdateManifest newestManifest;
    if (!LoadManifest(m_cfg.newestManifestPath, newestManifest))
    {
        Report(0, 0, L"", L"远程Manifest获取失败");
        return false;
    }

    bool hasLocal = false;
    UpdateManifest localManifest;

    // ---- 2. 加载本地 manifest ----
    if (fs::exists(m_cfg.localManifestPath))
    {
        hasLocal = localManifest.LoadFromXml(m_cfg.localManifestPath);
    }

    if (!hasLocal)
        printf("  本地Manifest不存在，视为首次运行\n");

    // ---- 3. UUID 比对 ----
    if (hasLocal && localManifest.uuid == newestManifest.uuid)
    {
        printf("  已是最新版本 (UUID: %ws)\n", localManifest.uuid.c_str());
        Report(0, 0, L"", L"已是最新版本");
        return true;
    }

    printf("  发现新版本: %ws\n", newestManifest.uuid.c_str());

    // ---- 4. 加载 patches.json ----
    PatchIndex patchIndex;
    if (!LoadPatches(m_cfg.newestPatchesPath, patchIndex))
    {
        Report(0, 0, L"", L"无法获取补丁索引");
        return false;
    }

    // ---- 5. 逐文件处理 ----
    auto& files = newestManifest.files;
    int total = static_cast<int>(files.size());
    bool allSuccess = true;

    for (int i = 0; i < total; i++)
    {
        auto& newFile = files[i];

        if (newFile.mode == FileMode::Skip)
        {
            Report(i, total, newFile.fileName, L"跳过");
            printf("  [%d/%d] %ws — Mode=Skip\n", i + 1, total, newFile.fileName.c_str());
            continue;
        }

        Report(i, total, newFile.fileName, L"检查中");

        ManifestFileEntry* localFile = nullptr;
        std::wstring localFilePath;

        if (hasLocal)
        {
            for (auto& lf : localManifest.files)
            {
                if (lf.uuid == newFile.uuid)
                {
                    localFile = &lf;
                    localFilePath = (fs::path(m_cfg.localRootPath) / lf.FullPath()).wstring();
                    break;
                }
            }
        }

        bool fileExists = (localFile != nullptr && !localFilePath.empty() &&
                           fs::exists(localFilePath));

        if (!fileExists)
        {
            printf("  [%d/%d] %ws — 新文件，全量下载\n", i + 1, total, newFile.fileName.c_str());
            Report(i, total, newFile.fileName, L"下载");
            if (!DownloadAndCopy(newFile, localFile))
                allSuccess = false;
        }
        else
        {
            auto localMd5 = (newFile.type == FileType::Text)
                ? ComputeNormalizedMd5(localFilePath)
                : ComputeFileMd5(localFilePath);
            if (!localMd5.empty() && localMd5 == newFile.md5)
            {
                printf("  [%d/%d] %ws — 已最新\n", i + 1, total, newFile.fileName.c_str());
                Report(i, total, newFile.fileName, L"跳过");
                continue;
            }

            bool applied = false;

            if (patchIndex.IsValid())
            {
                auto patchGuid = patchIndex.FindPatch(newFile.uuid, localFile->md5, newFile.md5);
                if (!patchGuid.empty())
                {
                    printf("  [%d/%d] %ws — 增量补丁\n", i + 1, total, newFile.fileName.c_str());
                    Report(i, total, newFile.fileName, L"增量补丁");
                    if (ApplyAndCopy(patchGuid, localFilePath, newFile, localFile))
                        applied = true;
                }
            }

            if (!applied)
            {
                printf("  [%d/%d] %ws — 全量下载（补丁不可用或失败）\n",
                       i + 1, total, newFile.fileName.c_str());
                Report(i, total, newFile.fileName, L"下载");
                if (!DownloadAndCopy(newFile, localFile))
                    allSuccess = false;
            }
            else
            {
                printf("  [%d/%d] %ws — ✓\n", i + 1, total, newFile.fileName.c_str());
            }
        }
    }

    // ---- 6. 保存本地 manifest ----
    if (allSuccess)
    {
        newestManifest.SaveToXml(m_cfg.localManifestPath);
        printf("\n更新完成，已保存本地 Manifest (UUID: %ws)\n",
               newestManifest.uuid.c_str());
        Report(total, total, L"", L"完成");
    }
    else
    {
        printf("\n部分文件更新失败，未更新本地 Manifest\n");
        Report(total, total, L"", L"失败");
    }

    return allSuccess;
}
