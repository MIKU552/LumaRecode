#include "process_internal.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <limits>
#include <memory>
#include <mutex>

namespace
{
using clock_type = std::chrono::steady_clock;

lr_status report(lr_error *error, lr_status status, const char *message) noexcept
{
    if (error != nullptr && error->struct_size == sizeof(*error))
    {
        error->code = status;
        const auto count = std::min(std::strlen(message), sizeof(error->message) - 1);
        std::memcpy(error->message, message, count);
        error->message[count] = 0;
    }
    return status;
}

bool valid_error(const lr_error *error)
{
    return error == nullptr || error->struct_size == sizeof(*error);
}

std::string copy_string(lr_utf8_view view, uint64_t &remaining)
{
    if (view.size > remaining)
    {
        throw luma::process_failure(LR_RESOURCE_LIMIT, "Process strings exceed the one-MiB budget.");
    }
    if (lr_utf8_validate(view.data, view.size) != LR_OK ||
        (view.size != 0 && std::memchr(view.data, 0, static_cast<size_t>(view.size)) != nullptr))
    {
        throw luma::process_failure(LR_INVALID_ARGUMENT,
                                    "Process strings require UTF-8 without embedded NUL.");
    }
    remaining -= view.size;
    return view.size == 0 ? std::string() : std::string(reinterpret_cast<const char *>(view.data), view.size);
}

bool absolute_path(const std::string &value)
{
    return std::filesystem::path(std::u8string(value.begin(), value.end())).is_absolute();
}

/* Allocate once per invocation. Tail capture is a ring: continuous diagnostics
 * never cause repeated reallocations or move the entire retained log. Only
 * initialized bytes are visible through copy(), including in partial failures. */
class capture_buffer
{
  public:
    capture_buffer(uint32_t mode, uint64_t capacity)
        : mode(mode), capacity(mode == LR_CAPTURE_DISCARD ? 0 : static_cast<size_t>(capacity)),
          storage(this->capacity == 0 ? nullptr : std::make_unique_for_overwrite<uint8_t[]>(this->capacity))
    {
    }
    bool append(std::span<const uint8_t> bytes)
    {
        seen += std::min<uint64_t>(bytes.size(), UINT64_MAX - seen);
        if (mode == LR_CAPTURE_DISCARD || bytes.empty())
        {
            return true;
        }
        if (mode == LR_CAPTURE_FULL)
        {
            const auto count = std::min(bytes.size(), capacity - size);
            if (count != 0)
            {
                std::memcpy(storage.get() + size, bytes.data(), count);
                size += count;
            }
            return count == bytes.size();
        }
        if (capacity == 0)
        {
            return true;
        }
        if (bytes.size() >= capacity)
        {
            std::memcpy(storage.get(), bytes.data() + bytes.size() - capacity, capacity);
            begin = 0;
            size = capacity;
            return true;
        }
        const auto end = (begin + size) % capacity;
        const auto first = std::min(bytes.size(), capacity - end);
        std::memcpy(storage.get() + end, bytes.data(), first);
        std::memcpy(storage.get(), bytes.data() + first, bytes.size() - first);
        const auto overwritten = bytes.size() - std::min(bytes.size(), capacity - size);
        begin = (begin + overwritten) % capacity;
        size = std::min(capacity, size + bytes.size());
        return true;
    }
    void copy(uint8_t *destination) const
    {
        if (size == 0)
        {
            return;
        }
        const auto first = std::min(size, capacity - begin);
        std::memcpy(destination, storage.get() + begin, first);
        std::memcpy(destination + first, storage.get(), size - first);
    }
    uint64_t seen = 0;
    size_t size = 0;

  private:
    uint32_t mode;
    size_t capacity;
    std::unique_ptr<uint8_t[]> storage;
    size_t begin = 0;
};
} // namespace

struct lr_process final : luma::process_sink
{
    lr_process(luma::process_request request, const lr_process_options &options)
        : request(std::move(request)), timeout_ms(options.timeout_ms),
          output(options.stdout_mode, options.stdout_capacity),
          errors(LR_CAPTURE_TAIL, options.stderr_capacity)
    {
        lr_process_snapshot_init(&snapshot, sizeof(snapshot));
    }
    void check_stop() override
    {
        if (cancelled.load(std::memory_order_relaxed))
        {
            throw luma::process_failure(LR_CANCELLED, "External process cancelled.");
        }
        if (timeout_ms != 0 && elapsed() >= timeout_ms)
        {
            throw luma::process_failure(LR_TIMED_OUT, "External process timed out.");
        }
    }
    void started() noexcept override
    {
        child_started = true;
    }
    void cleaned() noexcept override
    {
        cleanup_confirmed = true;
    }
    void consume(uint32_t stream, std::span<const uint8_t> bytes, bool cleaning) override
    {
        bool accepted;
        {
            std::lock_guard lock(mutex);
            accepted = (stream == LR_STREAM_STDOUT ? output : errors).append(bytes);
        }
        if (!accepted && !cleaning)
        {
            throw luma::process_failure(LR_RESOURCE_LIMIT,
                                        "External process output exceeds its byte budget.");
        }
        if (!cleaning && callback != nullptr)
        {
            const auto status = callback(callback_context, stream, bytes.data(), bytes.size());
            if (status != LR_OK)
                throw luma::process_failure(status, "Process output consumer stopped the invocation.");
        }
    }
    void exited(int32_t code) override
    {
        std::lock_guard lock(mutex);
        snapshot.exit_code = code;
    }
    uint64_t elapsed() const
    {
        return static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::milliseconds>(clock_type::now() - started_at).count());
    }
    luma::process_request request;
    uint64_t timeout_ms;
    capture_buffer output;
    capture_buffer errors;
    std::mutex mutex;
    std::atomic_bool cancelled = false;
    bool child_started = false;
    bool cleanup_confirmed = false;
    clock_type::time_point started_at;
    lr_process_snapshot snapshot;
    lr_process_output_callback callback = nullptr;
    void *callback_context = nullptr;
};

extern "C" lr_status lr_process_create(const lr_process_options *options, lr_process **result,
                                       lr_error *error)
{
    if (options == nullptr || result == nullptr || !valid_error(error) ||
        options->struct_size != sizeof(*options) || options->abi_version != LR_ABI_VERSION)
    {
        return report(error, LR_INVALID_ARGUMENT, "Invalid process options or ABI version.");
    }
    try
    {
        if (options->argument_count > 1024 ||
            (options->argument_count != 0 && options->arguments == nullptr) ||
            options->stdout_mode > LR_CAPTURE_DISCARD || options->stdout_capacity > 64 * 1024 * 1024 ||
            options->stderr_capacity > 1024 * 1024 || options->timeout_ms > INT64_MAX)
        {
            return report(error, LR_INVALID_ARGUMENT, "Invalid process arguments or capture budgets.");
        }
        uint64_t remaining = 1024 * 1024;
        luma::process_request request;
        request.executable = copy_string(options->executable, remaining);
        request.directory = copy_string(options->working_directory, remaining);
        if (!absolute_path(request.executable) ||
            (!request.directory.empty() && !absolute_path(request.directory)))
        {
            return report(error, LR_INVALID_ARGUMENT, "Process paths must be absolute.");
        }
        request.arguments.reserve(options->argument_count);
        for (uint32_t i = 0; i < options->argument_count; ++i)
        {
            request.arguments.push_back(copy_string(options->arguments[i], remaining));
        }
        *result = new lr_process(std::move(request), *options);
        return report(error, LR_OK, "");
    }
    catch (const luma::process_failure &failure)
    {
        return report(error, failure.status, failure.what());
    }
    catch (const std::bad_alloc &)
    {
        return report(error, LR_RESOURCE_LIMIT, "Cannot allocate process capture buffers.");
    }
    catch (...)
    {
        return report(error, LR_INTERNAL_ERROR, "Cannot create process context.");
    }
}

extern "C" lr_status lr_process_run_streaming(lr_process *process, lr_process_output_callback callback,
                                              void *context, lr_error *error)
{
    if (process == nullptr || !valid_error(error))
    {
        return report(error, LR_INVALID_ARGUMENT, "Invalid process handle or error record.");
    }
    try
    {
        {
            std::lock_guard lock(process->mutex);
            if (process->snapshot.state != LR_PROCESS_CREATED)
            {
                return report(error, LR_INVALID_ARGUMENT, "A process context can run only once.");
            }
            process->started_at = clock_type::now();
            process->snapshot.state = LR_PROCESS_RUNNING;
            process->callback = callback;
            process->callback_context = context;
        }
        lr_status status = LR_OK;
        std::array<char, 768> message{};
        const auto set_message = [&](const char *text)
        {
            const auto count = std::min(std::strlen(text), message.size() - 1);
            std::memcpy(message.data(), text, count);
            message[count] = 0;
        };
        try
        {
            process->check_stop();
            luma::execute_child(process->request, *process);
            process->check_stop();
            if (process->snapshot.exit_code != 0)
            {
                status = LR_IO_ERROR;
                std::snprintf(message.data(), message.size(), "External process exited with code %d.",
                              process->snapshot.exit_code);
            }
        }
        catch (const luma::process_failure &failure)
        {
            status = failure.status;
            set_message(failure.what());
        }
        catch (const std::bad_alloc &)
        {
            status = LR_RESOURCE_LIMIT;
            set_message("External process exceeded available memory.");
        }
        catch (...)
        {
            status = LR_INTERNAL_ERROR;
            set_message("External process failed unexpectedly.");
        }
        std::lock_guard lock(process->mutex);
        const bool cleanup_confirmed = !process->child_started || process->cleanup_confirmed;
        if (!cleanup_confirmed)
        {
            status = LR_IO_ERROR;
            const auto original = message;
            std::snprintf(message.data(), message.size(), "%.600s%s",
                          original[0] == 0 ? "External process ended." : original.data(),
                          " Cleanup could not be confirmed; preserve task files.");
        }
        process->snapshot.state = LR_PROCESS_FINISHED;
        process->snapshot.status = status;
        process->snapshot.cleanup_state = cleanup_confirmed ? LR_CLEANUP_CONFIRMED : LR_CLEANUP_FAILED;
        process->snapshot.elapsed_ms = process->elapsed();
        return report(error, status, message.data());
    }
    catch (...)
    {
        return report(error, LR_INTERNAL_ERROR, "Cannot access process state.");
    }
}

extern "C" lr_status lr_process_run(lr_process *process, lr_error *error)
{
    return lr_process_run_streaming(process, nullptr, nullptr, error);
}

extern "C" lr_status lr_process_cancel(lr_process *process)
{
    if (process == nullptr)
    {
        return LR_INVALID_ARGUMENT;
    }
    process->cancelled.store(true, std::memory_order_relaxed);
    return LR_OK;
}

extern "C" lr_status lr_process_get_snapshot(lr_process *process, lr_process_snapshot *result)
{
    if (process == nullptr || result == nullptr || result->struct_size != sizeof(*result) ||
        result->abi_version != LR_ABI_VERSION)
    {
        return LR_INVALID_ARGUMENT;
    }
    try
    {
        std::lock_guard lock(process->mutex);
        *result = process->snapshot;
        if (result->state == LR_PROCESS_RUNNING)
        {
            result->elapsed_ms = process->elapsed();
        }
        result->stdout_seen = process->output.seen;
        result->stderr_seen = process->errors.seen;
        result->stdout_retained = process->output.size;
        result->stderr_retained = process->errors.size;
        return LR_OK;
    }
    catch (...)
    {
        return LR_INTERNAL_ERROR;
    }
}

extern "C" lr_status lr_process_copy_output(lr_process *process, uint32_t stream, uint8_t *destination,
                                            uint64_t capacity, uint64_t *required)
{
    if (process == nullptr || required == nullptr || (capacity != 0 && destination == nullptr) ||
        (stream != LR_STREAM_STDOUT && stream != LR_STREAM_STDERR))
    {
        return LR_INVALID_ARGUMENT;
    }
    try
    {
        std::lock_guard lock(process->mutex);
        const auto &buffer = stream == LR_STREAM_STDOUT ? process->output : process->errors;
        *required = buffer.size;
        if (capacity < buffer.size)
        {
            return LR_BUFFER_TOO_SMALL;
        }
        buffer.copy(destination);
        return LR_OK;
    }
    catch (...)
    {
        return LR_INTERNAL_ERROR;
    }
}

extern "C" void lr_process_release(lr_process *process)
{
    delete process;
}
