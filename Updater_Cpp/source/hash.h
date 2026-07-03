#pragma once
#include <string>
#include <cstdint>

// 计算文件的 MD5 哈希值（32 位小写十六进制）
// 返回空字符串表示失败
std::wstring ComputeFileMd5(const std::wstring& filePath);

// 计算文件的 MD5，自动将 CRLF 统一为 LF 后再哈希
// 用于 FileType::Text 类型文件：服务端 XmlGenerator 在生成 manifest 时
// 会将文本文件统一为 LF 后计算 MD5，客户端须同步此行为
std::wstring ComputeNormalizedMd5(const std::wstring& filePath);

// 计算内存缓冲区的 MD5 哈希值
std::wstring ComputeMd5(const void* data, size_t size);

// 将缓冲区中的 CRLF (\r\n) 统一替换为 LF (\n)，
// 返回归一化后的字节数（≤ 原始大小），dest 可与 src 重叠
size_t NormalizeCrlf(void* buf, size_t size);
