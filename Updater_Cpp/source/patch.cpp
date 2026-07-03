#include "patch.h"
#include <windows.h>
#include <format>
#include <filesystem>
namespace fs = std::filesystem;

static std::wstring GetExeDir()
{
    wchar_t buf[MAX_PATH];
    DWORD len = GetModuleFileNameW(nullptr, buf, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) return {};
    fs::path p(buf);
    return p.parent_path().wstring();
}

std::wstring FindHpatchz(const std::wstring& exeDir)
{
    auto base = exeDir.empty() ? GetExeDir() : exeDir;
    if (base.empty()) return {};

    auto candidate = (fs::path(base) / L"tools" / L"hpatchz.exe").wstring();
    if (fs::exists(candidate)) return candidate;

    candidate = (fs::path(base) / L"hdiffpatch_bin" / L"win-x86" / L"hpatchz.exe").wstring();
    if (fs::exists(candidate)) return candidate;

    // fallback: try parent/tools/ (when running from build/)
    candidate = (fs::path(base).parent_path() / L"tools" / L"hpatchz.exe").wstring();
    if (fs::exists(candidate)) return candidate;

    return {};
}

bool ApplyPatch(const std::wstring& exeDir,
                const std::wstring& patchPath,
                const std::wstring& sourcePath,
                const std::wstring& destPath)
{
    auto hpatchz = FindHpatchz(exeDir);
    if (hpatchz.empty()) return false;

    auto cmd = std::format(L"\"{}\" -s -f \"{}\" \"{}\" \"{}\"",
                           hpatchz, sourcePath, patchPath, destPath);

    STARTUPINFOW si = {sizeof(si)};
    PROCESS_INFORMATION pi = {};
    if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE,
                         CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi))
        return false;

    WaitForSingleObject(pi.hProcess, 300000);
    DWORD ec = 0;
    GetExitCodeProcess(pi.hProcess, &ec);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return ec == 0;
}
