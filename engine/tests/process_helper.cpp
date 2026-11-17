#include <array>
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

#include "probe_fixture.hpp"

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#include <windows.h>
#else
#include <unistd.h>
#endif

namespace
{
std::string utf8_path(const std::filesystem::path &path)
{
    const auto bytes = path.u8string();
    return std::string(bytes.begin(), bytes.end());
}

int helper(const std::vector<std::string> &arguments)
{
    const auto &mode = arguments.at(1);
    if (mode == "-v")
        return probe_fixture(arguments);
    if (mode == "echo")
    {
        for (size_t i = 2; i < arguments.size(); ++i)
        {
            std::printf("%zu:", arguments[i].size());
            std::fwrite(arguments[i].data(), 1, arguments[i].size(), stdout);
            std::putchar('\n');
        }
    }
    else if (mode == "binary")
    {
        const auto size = std::stoul(arguments.at(2));
        // Fill stderr before stdout so a sequential reader would deadlock.
        const std::string diagnostics(150000, 'e');
        std::fwrite(diagnostics.data(), 1, diagnostics.size(), stderr);
        std::fputs("diagnostic-tail", stderr);
        std::array<uint8_t, 4096> bytes;
        size_t offset = 0;
        while (offset < size)
        {
            const auto count = std::min<size_t>(bytes.size(), size - offset);
            for (size_t i = 0; i < count; ++i)
            {
                bytes[i] = static_cast<uint8_t>((offset + i) % 251);
            }
            std::fwrite(bytes.data(), 1, count, stdout);
            offset += count;
        }
    }
    else if (mode == "fail")
    {
        std::fputs("failure-reason", stderr);
        return 17;
    }
    else if (mode == "cwd")
    {
        const auto path = utf8_path(std::filesystem::current_path());
        std::fwrite(path.data(), 1, path.size(), stdout);
    }
    else if (mode == "stdin")
    {
        std::fputs(std::getchar() == EOF ? "eof" : "unexpected-input", stdout);
    }
    else if (mode == "heartbeat")
    {
        std::fputs("child-ready", stdout);
        std::fflush(stdout);
        const auto path =
            std::filesystem::path(std::u8string(arguments.at(2).begin(), arguments.at(2).end()));
        while (true)
        {
            std::ofstream file(path, std::ios::binary | std::ios::app);
            file << '.';
            file.close();
            std::this_thread::sleep_for(std::chrono::milliseconds(25));
        }
    }
    else if (mode == "tree" || mode == "orphan")
    {
        const auto path =
            std::filesystem::path(std::u8string(arguments.at(2).begin(), arguments.at(2).end()));
#ifdef _WIN32
        const auto executable =
            std::filesystem::path(std::u8string(arguments[0].begin(), arguments[0].end())).wstring();
        std::wstring command = L"\"" + executable + L"\" heartbeat \"" + path.wstring() + L"\"";
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        startup.dwFlags = STARTF_USESTDHANDLES;
        startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
        startup.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
        startup.hStdError = GetStdHandle(STD_ERROR_HANDLE);
        PROCESS_INFORMATION child{};
        if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW,
                            nullptr, nullptr, &startup, &child))
        {
            return 91;
        }
        CloseHandle(child.hThread);
        CloseHandle(child.hProcess);
#else
        const auto child = fork();
        if (child == -1)
        {
            return 91;
        }
        if (child == 0)
        {
            execl(arguments[0].c_str(), arguments[0].c_str(), "heartbeat", arguments[2].c_str(),
                  static_cast<char *>(nullptr));
            _exit(92);
        }
#endif
        // Wait for evidence that the descendant is alive and writing before
        // reporting readiness or deliberately exiting without joining it.
        const auto started = std::chrono::steady_clock::now();
        while (!std::filesystem::exists(path) || std::filesystem::file_size(path) == 0)
        {
            if (std::chrono::steady_clock::now() - started > std::chrono::seconds(2))
            {
                return 93;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
        }
        std::fputs("tree-ready", stdout);
        std::fflush(stdout);
        if (mode == "orphan")
        {
            return 0;
        }
        while (true)
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
        }
    }
    else if (mode == "wait")
    {
        std::fputs("ready", stdout);
        std::fflush(stdout);
        while (true)
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
        }
    }
    else
    {
        return 90;
    }
    return 0;
}
} // namespace

#ifdef _WIN32
int wmain(int count, wchar_t **values)
{
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stderr), _O_BINARY);
    std::vector<std::string> arguments;
    for (int i = 0; i < count; ++i)
    {
        const auto length =
            WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, values[i], -1, nullptr, 0, nullptr, nullptr);
        if (length <= 0)
        {
            return 94;
        }
        std::string text(static_cast<size_t>(length), '\0');
        WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, values[i], -1, text.data(), length, nullptr,
                            nullptr);
        text.pop_back();
        arguments.push_back(std::move(text));
    }
    return helper(arguments);
}
#else
int main(int count, char **values)
{
    return helper(std::vector<std::string>(values, values + count));
}
#endif
