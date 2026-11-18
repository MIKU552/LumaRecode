#include "lumarecode/process.h"

#include <array>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <memory>
#include <random>
#include <string>
#include <thread>
#include <vector>

namespace
{
using clock_type = std::chrono::steady_clock;
using process_handle = std::unique_ptr<lr_process, decltype(&lr_process_release)>;
int checks = 0, failures = 0;
#define CHECK(expression)                                                                                    \
    do                                                                                                       \
    {                                                                                                        \
        ++checks;                                                                                            \
        if (!(expression))                                                                                   \
        {                                                                                                    \
            ++failures;                                                                                      \
            std::fprintf(stderr, "Process check failed at line %d: %s\n", __LINE__, #expression);            \
        }                                                                                                    \
    } while (0)

std::string utf8_path(const std::filesystem::path &path)
{
    const auto value = path.u8string();
    return std::string(value.begin(), value.end());
}
lr_utf8_view view(const std::string &text)
{
    return {reinterpret_cast<const uint8_t *>(text.data()), text.size()};
}

lr_status run(lr_process *process)
{
    lr_error error;
    lr_error_init(&error, sizeof(error));
    const auto status = lr_process_run(process, &error);
    if (status == LR_IO_ERROR || status == LR_INTERNAL_ERROR)
    {
        std::fprintf(stderr, "Native process execution: %s (%s)\n", lr_status_name(status), error.message);
    }
    return status;
}

process_handle create(const std::filesystem::path &executable, std::vector<std::string> arguments,
                      uint32_t mode = LR_CAPTURE_FULL, uint64_t capacity = 8 * 1024 * 1024,
                      uint64_t timeout = 3000, const std::filesystem::path &directory = {})
{
    const auto path = utf8_path(executable), cwd = utf8_path(directory);
    lr_process_options options;
    lr_process_options_init(&options, sizeof(options));
    options.executable = view(path);
    options.working_directory = view(cwd);
    std::vector<lr_utf8_view> views;
    for (const auto &argument : arguments)
    {
        views.push_back(view(argument));
    }
    options.arguments = views.data();
    options.argument_count = static_cast<uint32_t>(views.size());
    options.stdout_mode = mode;
    options.stdout_capacity = capacity;
    options.timeout_ms = timeout;
    lr_process *process = nullptr;
    lr_error error;
    lr_error_init(&error, sizeof(error));
    CHECK(lr_process_create(&options, &process, &error) == LR_OK);
    if (process == nullptr)
    {
        throw std::runtime_error(error.message);
    }
    // All path, argument and view storage is destroyed before run().
    return process_handle(process, &lr_process_release);
}

lr_process_snapshot snapshot(lr_process *process)
{
    lr_process_snapshot result;
    lr_process_snapshot_init(&result, sizeof(result));
    CHECK(lr_process_get_snapshot(process, &result) == LR_OK);
    CHECK(result.state != LR_PROCESS_FINISHED || result.cleanup_state == LR_CLEANUP_CONFIRMED);
    return result;
}

std::vector<uint8_t> output(lr_process *process, uint32_t stream = LR_STREAM_STDOUT)
{
    uint64_t required = 0;
    const auto status = lr_process_copy_output(process, stream, nullptr, 0, &required);
    CHECK(status == (required == 0 ? LR_OK : LR_BUFFER_TOO_SMALL));
    std::vector<uint8_t> bytes(static_cast<size_t>(required));
    CHECK(lr_process_copy_output(process, stream, bytes.data(), bytes.size(), &required) == LR_OK);
    return bytes;
}

std::string text(lr_process *process, uint32_t stream = LR_STREAM_STDOUT)
{
    const auto bytes = output(process, stream);
    return std::string(bytes.begin(), bytes.end());
}

void check_capture(const std::filesystem::path &helper)
{
    for (size_t size : {0U, 1U, 31U, 8192U, 8193U, 200000U, 1048576U})
    {
        auto process = create(helper, {"binary", std::to_string(size)}, LR_CAPTURE_FULL, size);
        CHECK(run(process.get()) == LR_OK);
        auto state = snapshot(process.get());
        CHECK(state.state == LR_PROCESS_FINISHED && state.exit_code == 0);
        CHECK(state.stdout_seen == size && state.stdout_retained == size && state.stderr_retained == 8192);
        const auto bytes = output(process.get());
        bool equal = bytes.size() == size;
        for (size_t i = 0; equal && i < size; ++i)
        {
            equal = bytes[i] == i % 251;
        }
        CHECK(equal);
        CHECK(text(process.get(), LR_STREAM_STDERR).ends_with("diagnostic-tail"));
    }
    auto tail = create(helper, {"binary", "100000"}, LR_CAPTURE_TAIL, 31);
    CHECK(run(tail.get()) == LR_OK);
    const auto bytes = output(tail.get());
    bool equal = bytes.size() == 31;
    for (size_t i = 0; equal && i < bytes.size(); ++i)
    {
        equal = bytes[i] == (100000 - 31 + i) % 251;
    }
    CHECK(equal);
    std::array<uint8_t, 4> sentinel{1, 2, 3, 4};
    const auto previous = sentinel;
    uint64_t required = 0;
    CHECK(lr_process_copy_output(tail.get(), LR_STREAM_STDOUT, sentinel.data(), sentinel.size(), &required) ==
          LR_BUFFER_TOO_SMALL);
    CHECK(sentinel == previous && required == 31);
    auto discarded = create(helper, {"binary", "100000"}, LR_CAPTURE_DISCARD, 0);
    CHECK(run(discarded.get()) == LR_OK);
    CHECK(snapshot(discarded.get()).stdout_seen == 100000 && output(discarded.get()).empty());
    auto limited = create(helper, {"binary", "32"}, LR_CAPTURE_FULL, 31);
    CHECK(run(limited.get()) == LR_RESOURCE_LIMIT);
    CHECK(snapshot(limited.get()).state == LR_PROCESS_FINISHED && output(limited.get()).size() == 31);
}

void check_arguments(const std::filesystem::path &helper, const std::filesystem::path &root)
{
    const auto directory = root / std::filesystem::path(u8"中文 日本語 space");
    std::filesystem::create_directory(directory);
    const auto copied = directory / helper.filename();
    std::filesystem::copy_file(helper, copied);
    std::vector<std::string> arguments{"echo",
                                       "",
                                       "space word",
                                       "quotes\"quoted\"",
                                       "C:\\path\\",
                                       "a\\\"b",
                                       "&|<>$()",
                                       "line\nfeed",
                                       utf8_path(std::filesystem::path(u8"中文 日本語 🌈"))};
    std::string expected;
    for (size_t i = 1; i < arguments.size(); ++i)
    {
        expected += std::to_string(arguments[i].size()) + ":" + arguments[i] + "\n";
    }
    auto process = create(copied, arguments, LR_CAPTURE_FULL, 8192, 3000, directory);
    CHECK(run(process.get()) == LR_OK);
    CHECK(text(process.get()) == expected);
    CHECK(run(process.get()) == LR_INVALID_ARGUMENT);
    CHECK(snapshot(process.get()).status == LR_OK);
    auto cwd = create(helper, {"cwd"}, LR_CAPTURE_FULL, 8192, 3000, directory);
    CHECK(run(cwd.get()) == LR_OK);
    const auto actual_directory = text(cwd.get());
    CHECK(std::filesystem::equivalent(
        std::filesystem::path(std::u8string(actual_directory.begin(), actual_directory.end())), directory));
    auto input = create(helper, {"stdin"});
    CHECK(run(input.get()) == LR_OK && text(input.get()) == "eof");
    auto failed = create(helper, {"fail"});
    lr_error error;
    lr_error_init(&error, sizeof(error));
    CHECK(lr_process_run(failed.get(), &error) == LR_IO_ERROR);
    CHECK(snapshot(failed.get()).exit_code == 17 && text(failed.get(), LR_STREAM_STDERR) == "failure-reason");
    CHECK(error.code == LR_IO_ERROR && std::strstr(error.message, "17") != nullptr);
    auto negative = create(helper, {"fail-negative"});
    int32_t code = 0;
    CHECK(lr_process_get_exit_code(negative.get(), &code) == LR_NOT_FOUND);
    CHECK(lr_process_run(negative.get(), &error) == LR_IO_ERROR);
#ifdef _WIN32
    CHECK(lr_process_get_exit_code(negative.get(), &code) == LR_OK && code == -1);
#else
    CHECK(lr_process_get_exit_code(negative.get(), &code) == LR_OK && code == 255);
#endif
    auto missing = create(root / "missing-executable", {});
    CHECK(lr_process_run(missing.get(), &error) == LR_IO_ERROR);
    CHECK(snapshot(missing.get()).state == LR_PROCESS_FINISHED);
    CHECK(lr_process_get_exit_code(missing.get(), &code) == LR_NOT_FOUND);
}

void check_cancellation(const std::filesystem::path &helper, const std::filesystem::path &root)
{
    auto cancelled = create(helper, {"wait"}, LR_CAPTURE_FULL, 8192, 0);
    CHECK(lr_process_cancel(cancelled.get()) == LR_OK);
    CHECK(run(cancelled.get()) == LR_CANCELLED);
    CHECK(snapshot(cancelled.get()).stdout_seen == 0);
    auto timeout = create(helper, {"wait"}, LR_CAPTURE_FULL, 8192, 60);
    CHECK(run(timeout.get()) == LR_TIMED_OUT);
    CHECK(snapshot(timeout.get()).elapsed_ms < 2000);
    for (const auto &mode : {"tree", "orphan"})
    {
        const auto heartbeat = root / (std::string(mode) + ".heartbeat");
        auto process = create(helper, {mode, utf8_path(heartbeat)});
        lr_status status = LR_INTERNAL_ERROR;
        std::jthread worker([&] { status = run(process.get()); });
        const auto started = clock_type::now();
        while (snapshot(process.get()).stdout_retained < std::strlen("child-readytree-ready") &&
               snapshot(process.get()).state != LR_PROCESS_FINISHED &&
               clock_type::now() - started < std::chrono::seconds(3))
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
        }
        CHECK(std::filesystem::exists(heartbeat) && std::filesystem::file_size(heartbeat) > 0);
        if (std::strcmp(mode, "tree") == 0)
        {
            CHECK(lr_process_cancel(process.get()) == LR_OK);
        }
        worker.join();
        CHECK(status == (std::strcmp(mode, "tree") == 0 ? LR_CANCELLED : LR_OK));
        const auto size = std::filesystem::file_size(heartbeat);
        std::this_thread::sleep_for(std::chrono::milliseconds(150));
        CHECK(std::filesystem::file_size(heartbeat) == size);
        CHECK(snapshot(process.get()).state == LR_PROCESS_FINISHED);
    }
}

void check_validation(const std::filesystem::path &helper)
{
    lr_process_options options;
    lr_process_options_init(&options, sizeof(options));
    lr_process *sentinel = reinterpret_cast<lr_process *>(static_cast<uintptr_t>(1));
    CHECK(lr_process_create(&options, &sentinel, nullptr) == LR_INVALID_ARGUMENT);
    CHECK(sentinel == reinterpret_cast<lr_process *>(static_cast<uintptr_t>(1)));
    const auto executable = utf8_path(helper);
    options.executable = view(executable);
    options.argument_count = 1;
    CHECK(lr_process_create(&options, &sentinel, nullptr) == LR_INVALID_ARGUMENT);
    options.argument_count = 0;
    const std::string invalid("bad\0argument", 12);
    lr_utf8_view argument = view(invalid);
    options.arguments = &argument;
    options.argument_count = 1;
    CHECK(lr_process_create(&options, &sentinel, nullptr) == LR_INVALID_ARGUMENT);
    options.argument_count = 0;
    options.stdout_capacity = 64 * 1024 * 1024 + 1;
    CHECK(lr_process_create(&options, &sentinel, nullptr) == LR_INVALID_ARGUMENT);
    CHECK(run(nullptr) == LR_INVALID_ARGUMENT);
    CHECK(lr_process_cancel(nullptr) == LR_INVALID_ARGUMENT);
    lr_process_release(nullptr);
}

int run_checks(const std::filesystem::path &helper)
{
    const auto parent = std::filesystem::canonical(std::filesystem::temp_directory_path());
    const auto root = parent / ("LumaRecode-process-checks-" + std::to_string(std::random_device{}()));
    if (!std::filesystem::create_directory(root) || root.parent_path() != parent)
    {
        return 2;
    }
    try
    {
        check_validation(helper);
        check_capture(helper);
        check_arguments(helper, root);
        check_cancellation(helper, root);
        struct consumer_state
        {
            lr_process *process;
            uint64_t output = 0, errors = 0;
            bool valid = true;
        };
        auto streamed = create(helper, {"binary", "350000"}, LR_CAPTURE_DISCARD, 0);
        consumer_state consumer{streamed.get()};
        const auto consume = [](void *context, uint32_t stream, const uint8_t *bytes,
                                uint64_t size) -> lr_status
        {
            auto &state = *static_cast<consumer_state *>(context);
            lr_process_snapshot live;
            lr_process_snapshot_init(&live, sizeof(live));
            if (lr_process_get_snapshot(state.process, &live) != LR_OK || live.state != LR_PROCESS_RUNNING)
                state.valid = false;
            if (stream == LR_STREAM_STDOUT)
            {
                for (uint64_t i = 0; i < size; ++i)
                    if (bytes[i] != static_cast<uint8_t>((state.output + i) % 251))
                        state.valid = false;
                state.output += size;
            }
            else
                state.errors += size;
            return LR_OK;
        };
        CHECK(lr_process_run_streaming(streamed.get(), consume, &consumer, nullptr) == LR_OK);
        CHECK(consumer.valid && consumer.output == 350000 && consumer.errors > 150000);
        CHECK(snapshot(streamed.get()).stdout_retained == 0);
        auto stopped = create(helper, {"binary", "350000"}, LR_CAPTURE_DISCARD, 0);
        CHECK(lr_process_run_streaming(
                  stopped.get(), [](void *, uint32_t, const uint8_t *, uint64_t) -> lr_status
                  { return LR_CANCELLED; }, nullptr, nullptr) == LR_CANCELLED);
        CHECK(snapshot(stopped.get()).cleanup_state == LR_CLEANUP_CONFIRMED);
    }
    catch (const std::exception &error)
    {
        ++failures;
        std::fprintf(stderr, "Process checks aborted: %s\n", error.what());
    }
    std::error_code cleanup;
    std::filesystem::remove_all(root, cleanup);
    CHECK(!cleanup);
    std::printf("Native process contracts: %d checks, %d failures.\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
} // namespace

#ifdef _WIN32
int wmain(int count, wchar_t **arguments)
#else
int main(int count, char **arguments)
#endif
{
    return count == 2 ? run_checks(std::filesystem::absolute(arguments[1])) : 2;
}
