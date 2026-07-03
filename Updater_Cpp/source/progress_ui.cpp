#include "progress_ui.h"
#include <CommCtrl.h>
#include <algorithm>
#include <thread>
#include <windows.h>

#pragma comment(lib, "Comctl32.lib")

static int GetWindowDpi(HWND hwnd)
{
    if (auto user32 = GetModuleHandleW(L"user32"))
    {
        using GetDpiForWindow_t = UINT(*)(HWND);
        if (auto func = reinterpret_cast<GetDpiForWindow_t>(GetProcAddress(user32, "GetDpiForWindow")))
            return func(hwnd);
    }
    HDC hdc = GetDC(hwnd);
    int dpi = GetDeviceCaps(hdc, LOGPIXELSY);
    ReleaseDC(hwnd, hdc);
    return dpi;
}

static HWND g_hwnd = nullptr;
static HWND g_progress = nullptr;
static HWND g_label = nullptr;
static HWND g_hDetail = nullptr;
static bool g_ui_thread_running = false;
static std::thread g_ui_thread;

volatile bool g_user_cancelled;
bool g_close_by_code = false;

// 96dpi base dimensions
static constexpr int BASE_CX = 520;
static constexpr int BASE_CY = 150;
static constexpr int BASE_PAD = 16;
static constexpr int BASE_LABEL_Y = 14;
static constexpr int BASE_LABEL_H = 22;
static constexpr int BASE_PB_Y = 48;
static constexpr int BASE_PB_H = 28;
static constexpr int BASE_INFO_Y = 88;
static constexpr int BASE_INFO_H = 22;
static constexpr int BASE_FONT = 12;

static int g_dpi = 96;

static int Scale(int v) { return MulDiv(v, g_dpi, 96); }

static bool IsWin8OrLater()
{
    auto mod = GetModuleHandleW(L"ntdll.dll");
    if (!mod) return true;
    using RtlGetVersionFn = LONG(NTAPI*)(PRTL_OSVERSIONINFOW);
    auto fn = reinterpret_cast<RtlGetVersionFn>(GetProcAddress(mod, "RtlGetVersion"));
    if (!fn) return true;
    RTL_OSVERSIONINFOW vi = {};
    vi.dwOSVersionInfoSize = sizeof(vi);
    if (fn(&vi) != 0) return true;
    return vi.dwMajorVersion > 6 || (vi.dwMajorVersion == 6 && vi.dwMinorVersion >= 2);
}

static HFONT CreateSegoeUIFont(int pt)
{
    LOGFONTW lf = {};
    lf.lfHeight = -MulDiv(pt, g_dpi, 72);
    lf.lfWeight = FW_NORMAL;
    lf.lfQuality = CLEARTYPE_QUALITY;
    wcscpy_s(lf.lfFaceName, IsWin8OrLater() ? L"Microsoft YaHei UI" : L"Microsoft YaHei");
    return CreateFontIndirectW(&lf);
}

static LRESULT CALLBACK ProgressWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    switch (msg)
    {
    case WM_CREATE:
    {
        auto create = reinterpret_cast<LPCREATESTRUCTW>(lParam);
        auto labelText = static_cast<const wchar_t*>(create->lpCreateParams);

        g_dpi = GetWindowDpi(hwnd);

        auto hFont = CreateSegoeUIFont(BASE_FONT);

        g_label = CreateWindowExW(0, L"STATIC", labelText,
            WS_CHILD | WS_VISIBLE | SS_LEFT | SS_CENTERIMAGE | SS_ENDELLIPSIS,
            Scale(BASE_PAD), Scale(BASE_LABEL_Y), Scale(BASE_CX - BASE_PAD * 2), Scale(BASE_LABEL_H),
            hwnd, reinterpret_cast<HMENU>(100), GetModuleHandleW(nullptr), nullptr);
        SendMessageW(g_label, WM_SETFONT, reinterpret_cast<WPARAM>(hFont), TRUE);

        g_progress = CreateWindowExW(0, PROGRESS_CLASSW, nullptr,
            WS_CHILD | WS_VISIBLE | PBS_SMOOTH,
            Scale(BASE_PAD), Scale(BASE_PB_Y), Scale(BASE_CX - BASE_PAD * 2), Scale(BASE_PB_H),
            hwnd, reinterpret_cast<HMENU>(101), GetModuleHandleW(nullptr), nullptr);
        SendMessageW(g_progress, PBM_SETRANGE, 0, MAKELPARAM(0, 100));
        SendMessageW(g_progress, PBM_SETPOS, 0, 0);
        SendMessageW(g_progress, PBM_SETBARCOLOR, 0, RGB(0, 120, 215));

        auto hInfoFont = CreateSegoeUIFont(BASE_FONT - 2);
        g_hDetail = CreateWindowExW(0, L"STATIC", L"",
            WS_CHILD | WS_VISIBLE | SS_RIGHT | SS_ENDELLIPSIS,
            Scale(BASE_PAD), Scale(BASE_INFO_Y), Scale(BASE_CX - BASE_PAD * 2), Scale(BASE_INFO_H),
            hwnd, reinterpret_cast<HMENU>(102), GetModuleHandleW(nullptr), nullptr);
        SendMessageW(g_hDetail, WM_SETFONT, reinterpret_cast<WPARAM>(hInfoFont), TRUE);

        break;
    }
    case WM_CTLCOLORSTATIC:
    {
        HDC hdcStatic = reinterpret_cast<HDC>(wParam);
        SetBkColor(hdcStatic, GetSysColor(COLOR_WINDOW));
        SetBkMode(hdcStatic, OPAQUE);
        return reinterpret_cast<LRESULT>(GetSysColorBrush(COLOR_WINDOW));
    }
    case WM_DPICHANGED:
    {
        g_dpi = HIWORD(wParam);
        auto rect = reinterpret_cast<RECT*>(lParam);
        SetWindowPos(hwnd, nullptr, rect->left, rect->top,
                     rect->right - rect->left, rect->bottom - rect->top,
                     SWP_NOZORDER | SWP_NOACTIVATE);
        RECT cr; GetClientRect(hwnd, &cr);
        SendMessageW(hwnd, WM_SIZE, 0, MAKELPARAM(cr.right, cr.bottom));
        break;
    }
    case WM_SIZE:
    {
        if (g_label && g_progress)
        {
            int cx = LOWORD(lParam);
            int cy = HIWORD(lParam);
            if (cx > 0 && cy > 0)
            {
                SetWindowPos(g_label, nullptr, Scale(BASE_PAD), Scale(BASE_LABEL_Y),
                    cx - Scale(BASE_PAD) * 2, Scale(BASE_LABEL_H), SWP_NOZORDER);
                SetWindowPos(g_progress, nullptr, Scale(BASE_PAD), Scale(BASE_PB_Y),
                    cx - Scale(BASE_PAD) * 2, Scale(BASE_PB_H), SWP_NOZORDER);
                if (g_hDetail)
                    SetWindowPos(g_hDetail, nullptr, Scale(BASE_PAD), Scale(BASE_INFO_Y),
                        cx - Scale(BASE_PAD) * 2, Scale(BASE_INFO_H), SWP_NOZORDER);
            }
        }
        break;
    }
    case WM_DESTROY:
        g_progress = nullptr;
        g_label = nullptr;
        g_hDetail = nullptr;
        g_hwnd = nullptr;

		if (!g_close_by_code)
            g_user_cancelled = true;

        PostQuitMessage(0);
        break;
    default:
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
    return 0;
}

static DWORD WINAPI UIThreadProc(LPVOID lpParam)
{
    auto title = static_cast<const wchar_t*>(lpParam);
    auto hinst = GetModuleHandleW(nullptr);

    WNDCLASSW wc = {};
    wc.lpfnWndProc = ProgressWndProc;
    wc.hInstance = hinst;
    wc.hIcon = LoadIconW(hinst, MAKEINTRESOURCEW(1));
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    wc.lpszClassName = L"AppHostProgressWindow";
    RegisterClassW(&wc);

    HDC hdc = GetDC(nullptr);
    g_dpi = GetDeviceCaps(hdc, LOGPIXELSY);
    ReleaseDC(nullptr, hdc);

    int cx = Scale(BASE_CX);
    int cy = Scale(BASE_CY);
    int x = (GetSystemMetrics(SM_CXSCREEN) - cx) / 2;
    int y = (GetSystemMetrics(SM_CYSCREEN) - cy) / 2;

    g_hwnd = CreateWindowExW(WS_EX_DLGMODALFRAME, L"AppHostProgressWindow", title,
        WS_CAPTION | WS_SYSMENU | WS_VISIBLE | WS_MINIMIZEBOX,
        x, y, cx, cy,
        nullptr, nullptr, hinst, nullptr);

    if (!g_hwnd) { g_ui_thread_running = false; return 1; }

    g_ui_thread_running = true;

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0))
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    g_ui_thread_running = false;
    return 0;
}

void ShowProgressWindow(const std::wstring& title, const std::wstring& message)
{
    if (g_ui_thread_running) return;

    INITCOMMONCONTROLSEX icex = {};
    icex.dwSize = sizeof(icex);
    icex.dwICC = ICC_PROGRESS_CLASS;
    InitCommonControlsEx(&icex);

    auto titleCopy = _wcsdup(title.c_str());
    auto msgCopy = _wcsdup(message.c_str());

	g_close_by_code = false;

    g_ui_thread = std::thread([titleCopy, msgCopy]() {
        UIThreadProc(msgCopy);
        free(titleCopy); free(msgCopy);
    });
    g_ui_thread.detach();

    for (int i = 0; i < 100 && !g_hwnd; i++)
        Sleep(10);
}

void UpdateProgress(int percent)
{
    if (g_progress && IsWindow(g_progress))
        SendMessageW(g_progress, PBM_SETPOS, std::clamp(percent, 0, 100), 0);
}

void SetProgressMessage(const std::wstring& message)
{
    if (g_label && IsWindow(g_label))
        SetWindowTextW(g_label, message.c_str());
}

void SetProgressDetail(const std::wstring& text)
{
    if (g_hDetail && IsWindow(g_hDetail))
        SetWindowTextW(g_hDetail, text.c_str());
}

void CloseProgressWindow()
{
    if (g_hwnd && IsWindow(g_hwnd))
    {
		g_close_by_code = true;
        PostMessageW(g_hwnd, WM_CLOSE, 0, 0);
    }

    for (int i = 0; i < 50 && g_ui_thread_running; i++)
        Sleep(10);
}
