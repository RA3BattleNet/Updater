#pragma once
#include <string>

// 查找 hpatchz.exe 路径（搜索策略同 CoronaLauncher updater.cpp）
// exeDir — 可执行文件所在目录；为空时自动从 GetModuleFileNameW 获取
std::wstring FindHpatchz(const std::wstring& exeDir = {});

// 使用外部 hpatchz.exe 应用 HDiffPatch 补丁
// hpatchz -s -f <oldFile> <diffFile> <newFile>
// exeDir    — hpatchz.exe 搜索基准目录（为空则用当前模块目录）
// patchPath — .hdiff 补丁文件路径
// sourcePath— 旧文件路径
// destPath  — 新文件输出路径
// 返回 true 表示成功（hpatchz 退出码 0）
bool ApplyPatch(const std::wstring& exeDir,
                const std::wstring& patchPath,
                const std::wstring& sourcePath,
                const std::wstring& destPath);
