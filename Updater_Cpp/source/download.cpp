#include "download.h"
#include <windows.h>
#include <winhttp.h>
#include <filesystem>

#pragma comment(lib, "winhttp.lib")

namespace fs = std::filesystem;

bool DownloadUrl(const std::wstring& url, const std::wstring& dest,
                 uint32_t timeoutMs, const DownloadProgress& progress)
{
    URL_COMPONENTS uc = { sizeof(uc) };
    uc.dwHostNameLength = 1;
    uc.dwUrlPathLength   = 1;
    if (!WinHttpCrackUrl(url.c_str(), static_cast<DWORD>(url.size()), 0, &uc))
        return false;

    std::wstring host(uc.lpszHostName, uc.dwHostNameLength);
    std::wstring path(uc.lpszUrlPath, uc.dwUrlPathLength);
    if (uc.dwExtraInfoLength)
        path += std::wstring(uc.lpszExtraInfo, uc.dwExtraInfoLength);

    HINTERNET hSession = WinHttpOpen(L"UpdaterCpp/1.0",
                                      WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
                                      nullptr, nullptr, 0);
    if (!hSession) return false;

    HINTERNET hConnect = WinHttpConnect(hSession, host.c_str(), uc.nPort, 0);
    if (!hConnect) { WinHttpCloseHandle(hSession); return false; }

    DWORD flags = WINHTTP_FLAG_REFRESH;
    if (uc.nScheme == INTERNET_SCHEME_HTTPS)
        flags |= WINHTTP_FLAG_SECURE;

    HINTERNET hRequest = WinHttpOpenRequest(hConnect, L"GET", path.c_str(),
                                             nullptr, nullptr, nullptr, flags);
    if (!hRequest) {
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);
        return false;
    }

    WinHttpSetTimeouts(hRequest, timeoutMs, timeoutMs, timeoutMs, timeoutMs);

    bool success = false;

    if (WinHttpSendRequest(hRequest, WINHTTP_NO_ADDITIONAL_HEADERS, 0,
                            WINHTTP_NO_REQUEST_DATA, 0, 0, 0) &&
        WinHttpReceiveResponse(hRequest, nullptr))
    {
        DWORD statusCode = 0;
        DWORD sz = sizeof(statusCode);
        WinHttpQueryHeaders(hRequest, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                            nullptr, &statusCode, &sz, nullptr);

        if (statusCode == 200)
        {
            // 获取 Content-Length
            uint64_t totalSize = 0;
            {
                WCHAR contentLen[32] = {};
                DWORD lenSz = sizeof(contentLen);
                if (WinHttpQueryHeaders(hRequest, WINHTTP_QUERY_CONTENT_LENGTH,
                                         nullptr, contentLen, &lenSz, nullptr))
                    totalSize = _wtoi64(contentLen);
            }

            fs::create_directories(fs::path(dest).parent_path());
            HANDLE hFile = CreateFileW(dest.c_str(), GENERIC_WRITE, 0, nullptr,
                                        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (hFile != INVALID_HANDLE_VALUE)
            {
                success = true;
                BYTE buf[65536];
                DWORD read = 0;
                uint64_t totalRead = 0;

                while (WinHttpReadData(hRequest, buf, sizeof(buf), &read))
                {
                    if (read == 0) break;

                    bool cancel = false;
                    if (progress)
                    {
                        totalRead += read;
                        progress(totalRead, totalSize, cancel);
                    }
                    if (cancel) { success = false; break; }

                    DWORD w = 0;
                    if (!WriteFile(hFile, buf, read, &w, nullptr) || w != read)
                    { success = false; break; }
                }

                CloseHandle(hFile);
                if (!success) fs::remove(dest);
            }
        }
    }

    WinHttpCloseHandle(hRequest);
    WinHttpCloseHandle(hConnect);
    WinHttpCloseHandle(hSession);
    return success;
}
