#pragma once
#include <windows.h>
#include <string>

void ShowProgressWindow(const std::wstring& title, const std::wstring& message);
void UpdateProgress(int percent);
void SetProgressMessage(const std::wstring& message);
void SetProgressDetail(const std::wstring& text);
void CloseProgressWindow();
