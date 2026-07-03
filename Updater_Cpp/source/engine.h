#pragma once
#include <string>
#include <functional>
#include "manifest.h"

// UpdateEngine 配置（对标 C# PatchIndexApplyerEngine.Options）
struct EngineConfig {
    std::wstring localRootPath;         // 本地安装根目录
    std::wstring localManifestPath;     // 本地 manifest.xml 路径
    std::wstring newestManifestPath;    // 远程 manifest.xml（URL 或本地路径）
    std::wstring newestPatchesPath;     // 远程 patches.json（URL 或本地路径）
    std::wstring newestDownloadUrl;     // 文件下载基 URL（末尾需 /
    std::wstring relaunchExe;           // 更新完成后启动的可执行文件（可选）
};

// 进度报告（对标 C# ProgressReport）
struct EngineProgress {
    int current = 0;
    int total = 0;
    std::wstring fileName;
    std::wstring status;
};

using ProgressCallback = std::function<void(const EngineProgress&)>;

// 核心更新引擎（对标 PatchIndexApplyerEngine.cs RunAsync）
class UpdateEngine {
    EngineConfig m_cfg;
    ProgressCallback m_progress;
    std::wstring m_cacheDir;

    std::wstring GetCacheDir() const;
    static bool IsUrl(const std::wstring& s);

    bool LoadManifest(const std::wstring& source, UpdateManifest& m) const;
    bool LoadPatches(const std::wstring& source, PatchIndex& pi) const;

    bool DownloadFullFile(const std::wstring& uuid, const std::wstring& dest) const;
    bool DownloadPatch(const std::wstring& patchName, const std::wstring& dest) const;

    bool DownloadAndCopy(const ManifestFileEntry& newFile,
                         const ManifestFileEntry* localFile);
    bool ApplyAndCopy(const std::wstring& patchGuid,
                      const std::wstring& localFilePath,
                      const ManifestFileEntry& newFile,
                      const ManifestFileEntry* localFile);

    void ForceCleanCache();
    void Report(int current, int total,
                const std::wstring& fileName,
                const std::wstring& status) const;

public:
    UpdateEngine(const EngineConfig& cfg, const ProgressCallback& progress = nullptr);

    // 运行更新流程，返回 true 表示更新成功或已最新
    bool Run();
};
