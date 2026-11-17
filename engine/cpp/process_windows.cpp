#include "process_internal.hpp"

#include <windows.h>

#include <algorithm>
#include <array>
#include <bit>
#include <memory>
#include <utility>

namespace
{
class handle
{
  public:
    handle() = default;
    explicit handle(HANDLE value) : value(value) {}
    ~handle()
    {
        reset();
    }
    handle(const handle &) = delete;
    handle &operator=(const handle &) = delete;
    HANDLE get() const
    {
        return value;
    }
    explicit operator bool() const
    {
        return value != nullptr && value != INVALID_HANDLE_VALUE;
    }
    void reset(HANDLE replacement = nullptr)
    {
        if (*this)
        {
            CloseHandle(value);
        }
        value = replacement;
    }

  private:
    HANDLE value = nullptr;
};

std::wstring wide(const std::string &text)
{
    if (text.empty())
    {
        return {};
    }
    const auto length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(),
                                            static_cast<int>(text.size()), nullptr, 0);
    if (length == 0)
    {
        luma::fail_os("MultiByteToWideChar", GetLastError());
    }
    std::wstring result(length, L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()),
                            result.data(), length) == 0)
    {
        luma::fail_os("MultiByteToWideChar", GetLastError());
    }
    return result;
}

/* CRT argv escaping: quotes double the immediately preceding backslashes;
 * trailing backslashes are doubled before the closing quote. Always quote,
 * including empty strings, and pass lpApplicationName separately. */
void append_argument(std::wstring &line, const std::wstring &argument)
{
    if (!line.empty())
    {
        line.push_back(L' ');
    }
    line.push_back(L'"');
    size_t slashes = 0;
    for (wchar_t character : argument)
    {
        if (character == L'\\')
        {
            ++slashes;
        }
        else
        {
            line.append(slashes * (character == L'"' ? 2 : 1), L'\\');
            if (character == L'"')
            {
                line.push_back(L'\\');
            }
            line.push_back(character);
            slashes = 0;
        }
    }
    line.append(slashes * 2, L'\\');
    line.push_back(L'"');
    if (line.size() >= 32767)
    {
        throw luma::process_failure(LR_RESOURCE_LIMIT,
                                    "Windows command line exceeds 32766 UTF-16 code units.");
    }
}

struct pipe_pair
{
    handle read;
    handle write;
    pipe_pair()
    {
        SECURITY_ATTRIBUTES attributes{sizeof(attributes), nullptr, TRUE};
        HANDLE reader = nullptr, writer = nullptr;
        if (!CreatePipe(&reader, &writer, &attributes, 0))
        {
            luma::fail_os("CreatePipe", GetLastError());
        }
        read.reset(reader);
        write.reset(writer);
        if (!SetHandleInformation(read.get(), HANDLE_FLAG_INHERIT, 0))
        {
            luma::fail_os("SetHandleInformation", GetLastError());
        }
    }
};

class attributes
{
  public:
    attributes()
    {
        SIZE_T bytes = 0;
        InitializeProcThreadAttributeList(nullptr, 2, 0, &bytes);
        storage = std::make_unique_for_overwrite<uint8_t[]>(bytes);
        list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.get());
        if (!InitializeProcThreadAttributeList(list, 2, 0, &bytes))
        {
            list = nullptr;
            luma::fail_os("InitializeProcThreadAttributeList", GetLastError());
        }
    }
    ~attributes()
    {
        if (list != nullptr)
        {
            DeleteProcThreadAttributeList(list);
        }
    }
    LPPROC_THREAD_ATTRIBUTE_LIST get() const
    {
        return list;
    }
    void set(DWORD_PTR kind, void *values, SIZE_T bytes)
    {
        if (!UpdateProcThreadAttribute(list, 0, kind, values, bytes, nullptr, nullptr))
        {
            luma::fail_os("UpdateProcThreadAttribute", GetLastError());
        }
    }

  private:
    std::unique_ptr<uint8_t[]> storage;
    LPPROC_THREAD_ATTRIBUTE_LIST list = nullptr;
};

class child_group
{
  public:
    child_group()
    {
        job.reset(CreateJobObjectW(nullptr, nullptr));
        if (!job)
        {
            luma::fail_os("CreateJobObjectW", GetLastError());
        }
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job.get(), JobObjectExtendedLimitInformation, &limits, sizeof(limits)))
        {
            luma::fail_os("SetInformationJobObject", GetLastError());
        }
    }
    ~child_group()
    {
        if (process)
        {
            TerminateJobObject(job.get(), 1);
            WaitForSingleObject(process.get(), INFINITE);
        }
    }
    void stop_and_join()
    {
        if (!TerminateJobObject(job.get(), 1))
        {
            luma::fail_os("TerminateJobObject", GetLastError());
        }
        if (WaitForSingleObject(process.get(), INFINITE) != WAIT_OBJECT_0)
        {
            luma::fail_os("WaitForSingleObject", GetLastError());
        }
        const auto started = std::chrono::steady_clock::now();
        while (true)
        {
            JOBOBJECT_BASIC_ACCOUNTING_INFORMATION information{};
            if (!QueryInformationJobObject(job.get(), JobObjectBasicAccountingInformation, &information,
                                           sizeof(information), nullptr))
            {
                luma::fail_os("QueryInformationJobObject", GetLastError());
            }
            if (information.ActiveProcesses == 0)
            {
                return;
            }
            if (std::chrono::steady_clock::now() - started > std::chrono::seconds(5))
            {
                throw luma::process_failure(LR_IO_ERROR, "Cannot confirm process job cleanup.");
            }
            Sleep(1);
        }
    }
    handle job;
    handle process;
};

bool drain(handle &pipe, uint32_t stream, luma::process_sink &sink, bool stopping = false)
{
    bool consumed = false;
    std::array<uint8_t, 8192> bytes;
    // Fairness bounds both cancellation latency and stderr starvation when a
    // child writes stdout continuously. The outer loop repeats without sleeping.
    for (unsigned i = 0; pipe && i < 16; ++i)
    {
        if (!stopping)
        {
            sink.check_stop();
        }
        DWORD available = 0;
        if (!PeekNamedPipe(pipe.get(), nullptr, 0, nullptr, &available, nullptr))
        {
            const auto code = GetLastError();
            if (code != ERROR_BROKEN_PIPE)
            {
                luma::fail_os("PeekNamedPipe", code);
            }
            pipe.reset();
            break;
        }
        if (available == 0)
        {
            break;
        }
        DWORD count = 0;
        if (!ReadFile(pipe.get(), bytes.data(), std::min<DWORD>(available, static_cast<DWORD>(bytes.size())),
                      &count, nullptr))
        {
            luma::fail_os("ReadFile", GetLastError());
        }
        sink.consume(stream, std::span(bytes.data(), count), stopping);
        consumed = true;
    }
    return consumed;
}
} // namespace

void luma::execute_child(const process_request &request, process_sink &sink)
{
    const auto executable = wide(request.executable);
    const auto directory = wide(request.directory);
    std::wstring command;
    append_argument(command, executable);
    for (const auto &argument : request.arguments)
    {
        append_argument(command, wide(argument));
    }
    pipe_pair output, errors;
    SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
    handle input(CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &security,
                             OPEN_EXISTING, 0, nullptr));
    if (!input)
    {
        fail_os("CreateFileW(NUL)", GetLastError());
    }
    child_group child;
    attributes inherited;
    std::array<HANDLE, 3> handles{input.get(), output.write.get(), errors.write.get()};
    inherited.set(PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles.data(), sizeof(handles));
    HANDLE job = child.job.get();
    // Creation-time association closes the launch/assignment race. Windows 11
    // also nests this job under an IDE or Actions runner's existing job.
    inherited.set(PROC_THREAD_ATTRIBUTE_JOB_LIST, &job, sizeof(job));
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = input.get();
    startup.StartupInfo.hStdOutput = output.write.get();
    startup.StartupInfo.hStdError = errors.write.get();
    startup.lpAttributeList = inherited.get();
    PROCESS_INFORMATION information{};
    sink.check_stop();
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
                        CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, nullptr,
                        directory.empty() ? nullptr : directory.c_str(), &startup.StartupInfo, &information))
    {
        fail_os("CreateProcessW", GetLastError());
    }
    child.process.reset(information.hProcess);
    sink.started();
    handle thread(information.hThread);
    output.write.reset();
    errors.write.reset();
    try
    {
        while (true)
        {
            sink.check_stop();
            const bool error_data = drain(errors.read, LR_STREAM_STDERR, sink);
            const bool output_data = drain(output.read, LR_STREAM_STDOUT, sink);
            const auto state = WaitForSingleObject(child.process.get(), error_data || output_data ? 0 : 5);
            if (state == WAIT_OBJECT_0)
            {
                break;
            }
            if (state == WAIT_FAILED)
            {
                fail_os("WaitForSingleObject", GetLastError());
            }
        }
        DWORD exit = 0;
        if (!GetExitCodeProcess(child.process.get(), &exit))
        {
            fail_os("GetExitCodeProcess", GetLastError());
        }
        sink.exited(std::bit_cast<int32_t>(static_cast<uint32_t>(exit)));
        child.stop_and_join();
        while (errors.read || output.read)
        {
            drain(errors.read, LR_STREAM_STDERR, sink);
            drain(output.read, LR_STREAM_STDOUT, sink);
        }
        sink.cleaned();
    }
    catch (...)
    {
        auto failure = std::current_exception();
        try
        {
            child.stop_and_join();
            const auto cleanup = std::chrono::steady_clock::now();
            while (errors.read || output.read)
            {
                drain(errors.read, LR_STREAM_STDERR, sink, true);
                drain(output.read, LR_STREAM_STDOUT, sink, true);
                if (std::chrono::steady_clock::now() - cleanup > std::chrono::seconds(5))
                {
                    throw luma::process_failure(LR_IO_ERROR, "Child pipes remained open after job cleanup.");
                }
            }
            sink.cleaned();
        }
        catch (...)
        {
        }
        std::rethrow_exception(failure);
    }
}
