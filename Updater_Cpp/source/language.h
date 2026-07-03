#pragma once
#include <windows.h>
#include <string>

struct LangStrings
{
    std::wstring preparing_runtime;
    std::wstring downloading_runtime;
    std::wstring setup_failed_title;
    std::wstring setup_failed_main;
    std::wstring setup_failed_content;
    std::wstring setup_btn_solution;
    std::wstring launch_failed_msg;
    std::wstring error_title;
    std::wstring downloading_installer;
    std::wstring downloading_desktop_installer;
    std::wstring installing_runtime;
    std::wstring installing_desktop;
    std::wstring downloading_runtime_zip;
    std::wstring downloading_desktop_zip;
    std::wstring extracting_runtime;
    std::wstring extracting_desktop;
    std::wstring please_wait;
    std::wstring extracting;
    std::wstring checking_updates;
    std::wstring downloading_update;
    std::wstring downloading_patch;
    std::wstring applying_patch;
    std::wstring old_os_title;
    std::wstring old_os_content;
    std::wstring runtime_required_title;
    std::wstring runtime_required_msg;
    std::wstring runtime_btn_download;
    std::wstring runtime_btn_cancel;
};

inline const LangStrings& GetLang()
{
    static bool init = false;
    static LangStrings lang;

    if (!init)
    {
        init = true;

        wchar_t env[3] = {};
        bool useZh = false;
        if (GetEnvironmentVariableW(L"RA3_APPHOST_LANGUAGE", env, 3) > 0)
        {
            useZh = (env[0] == L'z' && env[1] == L'h');
        }
        else
        {
            useZh = (PRIMARYLANGID(GetSystemDefaultLangID()) == LANG_CHINESE);
        }

        if (useZh)
        {
            lang.preparing_runtime         = L"\u6B63\u5728\u51C6\u5907\u8FD0\u884C\u65F6\u73AF\u5883...";
            lang.downloading_runtime       = L"\u6B63\u5728\u4E0B\u8F7D\u8FD0\u884C\u65F6...";
            lang.setup_failed_title        = L"\u8FD0\u884C\u65F6\u5B89\u88C5\u5931\u8D25";
            lang.setup_failed_main          = L"\u542F\u52A8\u5931\u8D25\uFF1A\u9700\u8981 .NET \u8FD0\u884C\u65F6";
            lang.setup_failed_content       = L"\u65E5\u5195\u542F\u52A8\u5668\u9700\u8981 .NET \u8FD0\u884C\u65F6\u624D\u80FD\u6B63\u786E\u8FD0\u884C\uFF0C\u4F46\u81EA\u52A8\u4E0B\u8F7D\u5B89\u88C5\u9047\u5230\u4E86\u9519\u8BEF\u3002\n\n\u8BF7\u9009\u62E9\u4EE5\u4E0B\u65B9\u5F0F\u624B\u52A8\u89E3\u51B3\uFF1A";
            lang.setup_btn_solution         = L"\u67E5\u770B\u89E3\u51B3\u65B9\u6CD5";
            lang.launch_failed_msg         = L"\u542F\u52A8\u6258\u7BA1\u63A7\u4EF6\u5931\u8D25: {}\n\n\u8BF7\u91CD\u65B0\u5B89\u88C5\u542F\u52A8\u5668\u3002";
            lang.error_title               = L"\u9519\u8BEF";
            lang.downloading_installer     = L"\u6B63\u5728\u4E0B\u8F7D .NET \u8FD0\u884C\u65F6\u5B89\u88C5\u5305...";
            lang.downloading_desktop_installer = L"\u6B63\u5728\u4E0B\u8F7D\u684C\u9762\u8FD0\u884C\u65F6\u5B89\u88C5\u5305...";
            lang.installing_runtime        = L"\u6B63\u5728\u5B89\u88C5 .NET \u8FD0\u884C\u65F6...";
            lang.installing_desktop        = L"\u6B63\u5728\u5B89\u88C5\u684C\u9762\u8FD0\u884C\u65F6...";
            lang.downloading_runtime_zip   = L"\u6B63\u5728\u4E0B\u8F7D .NET \u8FD0\u884C\u65F6\u538B\u7F29\u5305...";
            lang.downloading_desktop_zip   = L"\u6B63\u5728\u4E0B\u8F7D\u684C\u9762\u8FD0\u884C\u65F6\u538B\u7F29\u5305...";
            lang.extracting_runtime        = L"\u6B63\u5728\u89E3\u538B\u8FD0\u884C\u65F6...";
            lang.extracting_desktop        = L"\u6B63\u5728\u89E3\u538B\u684C\u9762\u8FD0\u884C\u65F6...";
            lang.please_wait               = L"\u8BF7\u7A0D\u5019...";
            lang.extracting                = L"\u6B63\u5728\u89E3\u538B...";
            lang.checking_updates          = L"\u6B63\u5728\u68C0\u67E5\u66F4\u65B0...";
            lang.downloading_update        = L"\u6B63\u5728\u4E0B\u8F7D\u66F4\u65B0...";
            lang.downloading_patch         = L"\u6B63\u5728\u4E0B\u8F7D\u8865\u4E01...";
            lang.applying_patch            = L"\u6B63\u5728\u5E94\u7528\u8865\u4E01...";
            lang.old_os_title              = L"\u64CD\u4F5C\u7CFB\u7EDF\u4E0D\u53D7\u652F\u6301";
            lang.old_os_content            = L"CoronaLauncher \u8981\u6C42 Windows 8 \u6216\u66F4\u65B0\u7248\u672C\u3002\n\n\u5F53\u524D\u64CD\u4F5C\u7CFB\u7EDF\u592A\u65E7\uFF0C\u65E0\u6CD5\u52A0\u8F7D .NET \u8FD0\u884C\u65F6\u3002";
            lang.runtime_required_title    = L"\u9700\u8981 .NET \u8FD0\u884C\u65F6";
            lang.runtime_required_msg      = L"CoronaLauncher \u9700\u8981 .NET {} \u8FD0\u884C\u65F6\uFF0C\u662F\u5426\u7ACB\u5373\u4E0B\u8F7D\u5E76\u5B89\u88C5\uFF1F\n\n\u5982\u679C\u5DF2\u5B89\u88C5 .NET \u8FD0\u884C\u65F6\uFF0C\u8BF7\u786E\u4FDD\u5B89\u88C5\u7684\u662F\u6700\u65B0\u7248\u672C\u3002";
            lang.runtime_btn_download      = L"\u4E0B\u8F7D\u5B89\u88C5";
            lang.runtime_btn_cancel        = L"\u53D6\u6D88";
        }
        else
        {
            lang.preparing_runtime         = L"Preparing runtime environment...";
            lang.downloading_runtime       = L"Downloading runtime...";
            lang.setup_failed_title        = L"Runtime Setup Failed";
            lang.setup_failed_main          = L"Launch failed: .NET runtime required";
            lang.setup_failed_content       = L"CoronaLauncher requires .NET runtime to run correctly, but automatic setup encountered an error.\n\nPlease choose one of the following to resolve:";
            lang.setup_btn_solution         = L"View Solution";
            lang.launch_failed_msg         = L"Failed to start managed app: {}\n\nPlease reinstall the launcher.";
            lang.error_title               = L"Error";
            lang.downloading_installer     = L"Downloading .NET runtime installer...";
            lang.downloading_desktop_installer = L"Downloading desktop runtime installer...";
            lang.installing_runtime        = L"Installing .NET runtime...";
            lang.installing_desktop        = L"Installing desktop runtime...";
            lang.downloading_runtime_zip   = L"Downloading .NET runtime...";
            lang.downloading_desktop_zip   = L"Downloading desktop runtime...";
            lang.extracting_runtime        = L"Extracting runtime...";
            lang.extracting_desktop        = L"Extracting desktop runtime...";
            lang.please_wait               = L"Please wait...";
            lang.extracting                = L"Extracting...";
            lang.checking_updates          = L"Checking for updates...";
            lang.downloading_update        = L"Downloading update...";
            lang.downloading_patch         = L"Downloading patch...";
            lang.applying_patch            = L"Applying patch...";
            lang.old_os_title              = L"Unsupported Operating System";
            lang.old_os_content            = L"CoronaLauncher requires Windows 8 or later.\n\nYour operating system is too old to load the .NET runtime.";
            lang.runtime_required_title    = L".NET Runtime Required";
            lang.runtime_required_msg      = L"CoronaLauncher requires .NET {} runtime.\n\nWould you like to download and install it now?\n\nIf .NET is already installed, please make sure you have the latest version.";
            lang.runtime_btn_download      = L"Download && Install";
            lang.runtime_btn_cancel        = L"Cancel";
        }
    }

    return lang;
}
