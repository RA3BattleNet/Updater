#include <cstdio>
#include <format>
#include <filesystem>
#include <string>
#include <vector>
#include "manifest.h"
#include "hash.h"
#include "download.h"
#include "patch.h"
#include "engine.h"
#include "progress_ui.h"
#include "language.h"
#include <windows.h>

namespace fs = std::filesystem;

static void PrintUsage()
{
    printf(R"(UpdaterCpp — 增量更新客户端测试驱动 (Step 6 — CLI 测试驱动)

用法:
  UpdaterCpp test-manifest <manifest.xml>
       解析 manifest.xml 并打印文件列表

  UpdaterCpp test-patches <patches.json>
       解析 patches.json 并打印补丁条目

  UpdaterCpp test-hash <file>
       计算文件的 MD5

  UpdaterCpp test-download <url> [dest]
       下载文件，可选指定保存路径（默认缓存到 temp）
       先启动测试服务器: python file_server.py --port 23456 --dir server/patch

  UpdaterCpp test-patch <oldFile> <diffFile> [outputFile]
       应用 HDiffPatch 补丁（默认输出 oldFile.patched）
       先确保 tools/hpatchz.exe 存在

  UpdaterCpp test-engine --local-rootPath <dir> --local-manifestPath <file>
                        --newest-manifestPath <url|file> --newest-patchesPath <url|file>
                        --newest-downloadUrl <url> [--relaunch <exe>]
       全流程更新测试（对标 PatchIndexApplyerEngine.RunAsync）
       示例: UpdaterCpp test-engine
               --local-rootPath C:\Corona
               --local-manifestPath temp\local.xml
               --newest-manifestPath http://server/manifest.xml
               --newest-patchesPath http://server/patches.json
               --newest-downloadUrl http://server/download/
               --relaunch C:\Corona\CoronaLauncher.exe

  UpdaterCpp help
       显示此帮助
)");
}

static int TestManifest(const std::wstring& path)
{
    if (!fs::exists(path))
    {
        printf("文件不存在: %ws\n", path.c_str());
        return 1;
    }

    UpdateManifest m;
    if (!m.LoadFromXml(path))
    {
        printf("manifest 解析失败: %ws\n", path.c_str());
        return 1;
    }

    printf("版本: %ws\n", m.version.c_str());
    printf("UUID: %ws\n", m.uuid.c_str());
    if (m.genTime > 0) printf("GenTime: %lld\n", m.genTime);
    if (!m.commit.empty()) printf("Commit: %ws\n", m.commit.c_str());
    printf("文件数: %zu\n\n", m.files.size());

    for (auto& f : m.files)
        printf("  [%ws] %ws  MD5:%ws  Mode:%ws  Type:%d  KindOf:%ws\n",
               f.uuid.c_str(), f.FullPath().c_str(),
               f.md5.c_str(), FileModeToStr(f.mode).c_str(),
               static_cast<int>(f.type), f.kindOf.c_str());

    auto saved = (fs::path(path).parent_path() / L"manifest.regen.xml").wstring();
    m.SaveToXml(saved);
    printf("\n已重新保存到: %ws\n", saved.c_str());
    return 0;
}

static int TestPatches(const std::wstring& path)
{
    if (!fs::exists(path))
    {
        printf("文件不存在: %ws\n", path.c_str());
        return 1;
    }

    PatchIndex pi;
    if (!pi.LoadFromFile(path))
    {
        printf("patches.json 解析失败: %ws\n", path.c_str());
        return 1;
    }

    printf("补丁数: %zu\n\n", pi.Count());

    // FindPatch 演示：用 temp 数据中的示例值
    if (pi.Count() > 0)
    {
        auto& first = pi.GetPatch(0);
        printf("查找示例: UUID=%ws  OldMD5=%ws  NewMD5=%ws\n",
               first.uuid.c_str(), first.oldMd5.c_str(), first.newMd5.c_str());
        auto found = pi.FindPatch(first.uuid, first.oldMd5, first.newMd5);
        printf("结果: %ws\n\n", found.c_str());
    }

    for (size_t i = 0; i < pi.Count(); i++)
    {
        auto& p = pi.GetPatch(i);
        printf("  [%zu] UUID=%ws  PatchName=%ws\n",
               i, p.uuid.c_str(), p.patchName.c_str());
    }

    return 0;
}

static int TestHash(const std::wstring& path)
{
    if (!fs::exists(path))
    {
        printf("文件不存在: %ws\n", path.c_str());
        return 1;
    }

    auto md5 = ComputeFileMd5(path);
    if (md5.empty())
    {
        printf("MD5 计算失败: %ws\n", path.c_str());
        return 1;
    }

    printf("%ws  MD5:%ws\n", fs::path(path).filename().c_str(), md5.c_str());
    return 0;
}

static int TestDownload(const std::wstring& url, const std::wstring* dest)
{
    std::wstring destPath;
    if (dest && !dest->empty())
        destPath = *dest;
    else
    {
        auto name = fs::path(url).filename();
        if (name.empty()) name = L"downloaded.bin";
        auto tmp = fs::temp_directory_path() / L"UpdaterCpp";
        fs::create_directories(tmp);
        destPath = (tmp / name).wstring();
    }

    printf("下载中:\n  URL: %ws\n  保存到: %ws\n\n", url.c_str(), destPath.c_str());

    auto progress = [](uint64_t current, uint64_t total, bool&) {
        if (total > 0)
            printf("\r  %llu / %llu  (%.0f%%)  ",
                   current, total,
                   static_cast<double>(current) * 100.0 / total);
        else
            printf("\r  已下载 %llu 字节  ", current);
    };

    if (!DownloadUrl(url, destPath, 10000, progress))
    {
        printf("\n下载失败\n");
        return 1;
    }

    printf("\n下载完成\n");

    auto md5 = ComputeFileMd5(destPath);
    if (!md5.empty())
        printf("MD5: %ws\n", md5.c_str());

    return 0;
}

static int TestPatch(const std::wstring& oldFile, const std::wstring& diffFile, const std::wstring* outputArg)
{
    if (!fs::exists(oldFile))
    {
        printf("旧文件不存在: %ws\n", oldFile.c_str());
        return 1;
    }
    if (!fs::exists(diffFile))
    {
        printf("补丁文件不存在: %ws\n", diffFile.c_str());
        return 1;
    }

    std::wstring destFile;
    if (outputArg && !outputArg->empty())
        destFile = *outputArg;
    else
        destFile = oldFile + L".patched";

    printf("应用补丁:\n  旧文件: %ws\n  补丁:   %ws\n  新文件: %ws\n\n",
           oldFile.c_str(), diffFile.c_str(), destFile.c_str());

    if (!ApplyPatch({}, diffFile, oldFile, destFile))
    {
        printf("补丁应用失败\n");
        return 1;
    }

    printf("补丁应用成功\n");

    auto oldMd5 = ComputeFileMd5(oldFile);
    auto newMd5 = ComputeFileMd5(destFile);
    printf("旧文件 MD5: %ws\n", oldMd5.c_str());
    printf("新文件 MD5: %ws\n", newMd5.c_str());

    return 0;
}

static int TestEngine(int argc, wchar_t* argv[])
{
    if (argc < 2)
    {
        printf("用法: UpdaterCpp test-engine --local-rootPath <dir> --local-manifestPath <file> --newest-manifestPath <url|file> --newest-patchesPath <url|file> --newest-downloadUrl <url> [--relaunch <exe>]\n");
        return 1;
    }

    EngineConfig cfg;
    bool hasRoot = false, hasManifest = false, hasNewestManifest = false,
         hasPatches = false, hasDownloadUrl = false;

    for (int i = 2; i < argc - 1; i++)
    {
        std::wstring arg = argv[i];
        auto next = argv[i + 1];

        if (arg == L"--local-rootPath")       { cfg.localRootPath = next; hasRoot = true; i++; }
        else if (arg == L"--local-manifestPath") { cfg.localManifestPath = next; hasManifest = true; i++; }
        else if (arg == L"--newest-manifestPath") { cfg.newestManifestPath = next; hasNewestManifest = true; i++; }
        else if (arg == L"--newest-patchesPath")  { cfg.newestPatchesPath = next; hasPatches = true; i++; }
        else if (arg == L"--newest-downloadUrl")  { cfg.newestDownloadUrl = next; hasDownloadUrl = true; i++; }
        else if (arg == L"--relaunch")            { cfg.relaunchExe = next; i++; }
        else
        {
            printf("未知参数: %ws\n", arg.c_str());
            return 1;
        }
    }

    if (!hasRoot || !hasManifest || !hasNewestManifest || !hasPatches || !hasDownloadUrl)
    {
        printf("缺少必要参数\n");
        return 1;
    }

    if (cfg.newestDownloadUrl.back() != L'/')
        cfg.newestDownloadUrl += L'/';

    printf("配置:\n");
    printf("  LocalRoot:     %ws\n", cfg.localRootPath.c_str());
    printf("  LocalManifest: %ws\n", cfg.localManifestPath.c_str());
    printf("  RemoteManifest:%ws\n", cfg.newestManifestPath.c_str());
    printf("  RemotePatches: %ws\n", cfg.newestPatchesPath.c_str());
    printf("  DownloadUrl:   %ws\n", cfg.newestDownloadUrl.c_str());
    if (!cfg.relaunchExe.empty())
        printf("  Relaunch:      %ws\n", cfg.relaunchExe.c_str());
    printf("\n");

    ShowProgressWindow(L"UpdaterCpp", GetLang().checking_updates);

    UpdateEngine engine(cfg, [](const EngineProgress& p) {
        if (p.total > 0)
            UpdateProgress(p.current * 100 / p.total);

        std::wstring msg;
        if      (p.status == L"检查中")   msg = GetLang().checking_updates;
        else if (p.status == L"下载" ||
                 p.status == L"全量下载") msg = GetLang().downloading_update;
        else if (p.status == L"增量补丁") msg = GetLang().downloading_patch;
        else if (p.status == L"完成")     msg = GetLang().please_wait;

        if (!msg.empty())
            SetProgressMessage(msg);
        if (!p.fileName.empty())
            SetProgressDetail(p.fileName);
    });

    bool ok = engine.Run();
    CloseProgressWindow();

    if (ok && !cfg.relaunchExe.empty())
    {
        printf("\n启动: %ws\n", cfg.relaunchExe.c_str());
        ShellExecuteW(nullptr, L"open", cfg.relaunchExe.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    }

    printf("\n结果: %ws\n", ok ? L"成功" : L"失败");
    return ok ? 0 : 1;
}

int wmain(int argc, wchar_t* argv[])
{
    if (argc < 2)
    {
        PrintUsage();
        return 0;
    }

    std::wstring cmd = argv[1];

    if (cmd == L"test-manifest" && argc >= 3)
        return TestManifest(argv[2]);

    if (cmd == L"test-patches" && argc >= 3)
        return TestPatches(argv[2]);

    if (cmd == L"test-hash" && argc >= 3)
        return TestHash(argv[2]);

    if (cmd == L"test-download" && argc >= 3)
    {
        static std::wstring destArg;
        destArg = (argc >= 4) ? argv[3] : L"";
        return TestDownload(argv[2], &destArg);
    }

    if (cmd == L"test-patch" && argc >= 4)
    {
        static std::wstring outputArg;
        outputArg = (argc >= 5) ? argv[4] : L"";
        return TestPatch(argv[2], argv[3], &outputArg);
    }

    if (cmd == L"test-engine")
        return TestEngine(argc, argv);

    if (cmd == L"help" || cmd == L"--help" || cmd == L"-?")
    {
        PrintUsage();
        return 0;
    }

    printf("未知命令: %ws\n\n", cmd.c_str());
    PrintUsage();
    return 1;
}
