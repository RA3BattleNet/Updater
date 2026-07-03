#pragma once
#include <string>
#include <cstdint>
#include <functional>

// 下载进度回调：参数为 (已下载字节数, 总字节数, 取消引用)
// total 为 0 表示服务器未返回 Content-Length
using DownloadProgress = std::function<void(uint64_t current, uint64_t total, bool& cancel)>;

// 使用 WinHTTP 下载文件
// url        — 完整 URL（http/https）
// dest       — 本地保存路径
// timeoutMs  — 每次 WinHTTP 操作的超时（毫秒）
// progress   — 可选进度回调
// 返回 true 表示成功，false 表示失败
bool DownloadUrl(const std::wstring& url, const std::wstring& dest,
                 uint32_t timeoutMs = 5000,
                 const DownloadProgress& progress = nullptr);
