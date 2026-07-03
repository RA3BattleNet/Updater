#include "hash.h"
#include <windows.h>
#include <wincrypt.h>
#include <cstdio>

#pragma comment(lib, "crypt32.lib")

std::wstring ComputeMd5(const void* data, size_t size)
{
    HCRYPTPROV hProv = 0;
    HCRYPTHASH hHash = 0;
    BYTE hash[16] = {};
    DWORD hashLen = 16;

    if (!CryptAcquireContextW(&hProv, nullptr, nullptr, PROV_RSA_FULL, CRYPT_VERIFYCONTEXT))
        return {};
    if (!CryptCreateHash(hProv, CALG_MD5, 0, 0, &hHash))
    {
        CryptReleaseContext(hProv, 0);
        return {};
    }

    CryptHashData(hHash, static_cast<const BYTE*>(data), static_cast<DWORD>(size), 0);

    bool ok = CryptGetHashParam(hHash, HP_HASHVAL, hash, &hashLen, 0);

    CryptDestroyHash(hHash);
    CryptReleaseContext(hProv, 0);

    if (!ok) return {};

    wchar_t hex[33] = {};
    for (DWORD i = 0; i < hashLen; i++)
        swprintf_s(hex + i * 2, 3, L"%02x", hash[i]);
    return std::wstring(hex);
}

size_t NormalizeCrlf(void* buf, size_t size)
{
    auto* src = static_cast<uint8_t*>(buf);
    auto* dst = src;
    size_t out = 0;
    for (size_t i = 0; i < size; i++)
    {
        if (src[i] == '\r' && i + 1 < size && src[i + 1] == '\n')
            continue;
        dst[out++] = src[i];
    }
    return out;
}

std::wstring ComputeNormalizedMd5(const std::wstring& filePath)
{
    HANDLE hFile = CreateFileW(filePath.c_str(), GENERIC_READ, FILE_SHARE_READ,
                               nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hFile == INVALID_HANDLE_VALUE)
        return {};

    LARGE_INTEGER li;
    if (!GetFileSizeEx(hFile, &li) || li.QuadPart > 256 * 1024 * 1024)
    {
        CloseHandle(hFile);
        return {};
    }

    size_t size = static_cast<size_t>(li.QuadPart);
    auto* buf = static_cast<uint8_t*>(HeapAlloc(GetProcessHeap(), 0, size));
    if (!buf) { CloseHandle(hFile); return {}; }

    DWORD bytesRead = 0;
    bool readOk = ReadFile(hFile, buf, static_cast<DWORD>(size), &bytesRead, nullptr);
    CloseHandle(hFile);

    if (!readOk || bytesRead != size)
    {
        HeapFree(GetProcessHeap(), 0, buf);
        return {};
    }

    size_t normalizedSize = NormalizeCrlf(buf, size);
    auto result = ComputeMd5(buf, normalizedSize);
    HeapFree(GetProcessHeap(), 0, buf);
    return result;
}

std::wstring ComputeFileMd5(const std::wstring& filePath)
{
    HANDLE hFile = CreateFileW(filePath.c_str(), GENERIC_READ, FILE_SHARE_READ,
                               nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hFile == INVALID_HANDLE_VALUE)
        return {};

    HCRYPTPROV hProv = 0;
    HCRYPTHASH hHash = 0;
    BYTE hash[16] = {};
    DWORD hashLen = 16;
    bool ok = false;

    if (CryptAcquireContextW(&hProv, nullptr, nullptr, PROV_RSA_FULL, CRYPT_VERIFYCONTEXT) &&
        CryptCreateHash(hProv, CALG_MD5, 0, 0, &hHash))
    {
        BYTE buf[65536];
        DWORD bytesRead = 0;
        while (ReadFile(hFile, buf, sizeof(buf), &bytesRead, nullptr) && bytesRead > 0)
            CryptHashData(hHash, buf, bytesRead, 0);

        ok = CryptGetHashParam(hHash, HP_HASHVAL, hash, &hashLen, 0);
    }

    if (hHash) CryptDestroyHash(hHash);
    if (hProv) CryptReleaseContext(hProv, 0);
    CloseHandle(hFile);

    if (!ok) return {};

    wchar_t hex[33] = {};
    for (DWORD i = 0; i < hashLen; i++)
        swprintf_s(hex + i * 2, 3, L"%02x", hash[i]);
    return std::wstring(hex);
}
