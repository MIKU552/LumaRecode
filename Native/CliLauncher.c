/* SPDX-License-Identifier: MIT
 * Console-subsystem companion: no CRT, no encoding or argument rewriting.
 * Its only imported library is the Windows kernel32 system library.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

static BOOL WINAPI keep_waiting(DWORD type) {
    return type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT;
}
static void fail(const WCHAR *message, DWORD code) {
    DWORD written;
    HANDLE error = GetStdHandle(STD_ERROR_HANDLE);
    CHAR text[1024];
    int count = WideCharToMultiByte(CP_UTF8, 0, message, -1, text, sizeof(text), 0, 0);
    if (count > 1) WriteFile(error, text, (DWORD)count - 1, &written, 0);
    ExitProcess(code);
}
void FrameFlowEntry(void) {
    WCHAR *app = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 65536);
    WCHAR *command = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 65536);
    if (!app || !command) fail(L"FrameFlow: insufficient memory.\r\n", 3);
    DWORD length = GetModuleFileNameW(0, app, 32768);
    if (!length || length >= 32768) fail(L"FrameFlow: cannot resolve launcher path.\r\n", 3);
    DWORD end = length;
    while (end && app[end - 1] != L'\\') --end;
    if (end + 14 >= 32768) fail(L"FrameFlow: launcher path is too long.\r\n", 3);
    lstrcpyW(app + end, L"FrameFlow.exe");
    WCHAR *tail = GetCommandLineW();
    BOOL quoted = FALSE;
    while (*tail) {
        if (*tail == L'"') quoted = !quoted;
        else if (!quoted && (*tail == L' ' || *tail == L'\t')) break;
        ++tail;
    }
    while (*tail == L' ' || *tail == L'\t') ++tail;
    if (lstrlenW(app) + lstrlenW(tail) + 12 >= 32768)
        fail(L"FrameFlow: command line is too long.\r\n", 2);
    lstrcpyW(command, L"\""); lstrcatW(command, app);
    lstrcatW(command, L"\" --cli "); lstrcatW(command, tail);
    STARTUPINFOW startup = {0};
    PROCESS_INFORMATION child = {0};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    startup.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    SetConsoleCtrlHandler(keep_waiting, TRUE);
    if (!CreateProcessW(app, command, 0, 0, TRUE, 0, 0, 0, &startup, &child))
        fail(L"FrameFlow: place FrameFlow.com beside FrameFlow.exe.\r\n", 3);
    CloseHandle(child.hThread);
    WaitForSingleObject(child.hProcess, INFINITE);
    DWORD code = 1;
    GetExitCodeProcess(child.hProcess, &code);
    CloseHandle(child.hProcess);
    ExitProcess(code == 0xC000013A ? 130 : code);
}
