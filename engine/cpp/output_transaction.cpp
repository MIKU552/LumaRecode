#include "output_internal.hpp"

#include <algorithm>
#include <cstring>
#include <mutex>
#include <span>

namespace
{
lr_status report(lr_error *error, lr_status status, const char *message) noexcept
{
    if (error != nullptr && error->struct_size == sizeof(*error))
    {
        error->code = status;
        const auto size = std::min(std::strlen(message), sizeof(error->message) - 1);
        std::memcpy(error->message, message, size);
        error->message[size] = 0;
    }
    return status;
}
bool valid_error(const lr_error *error)
{
    return error == nullptr || error->struct_size == sizeof(*error);
}
std::filesystem::path parse_path(lr_utf8_view view)
{
    if (view.size == 0 || view.size > 1024 * 1024 || lr_utf8_validate(view.data, view.size) != LR_OK ||
        std::memchr(view.data, 0, static_cast<size_t>(view.size)) != nullptr)
    {
        throw luma::file_failure(LR_INVALID_ARGUMENT, "Expected a bounded UTF-8 file path without NUL.");
    }
    const std::span bytes(view.data, static_cast<size_t>(view.size));
    std::filesystem::path path(std::u8string(bytes.begin(), bytes.end()));
    if (!path.is_absolute())
    {
        throw luma::file_failure(LR_INVALID_ARGUMENT, "File paths must be absolute.");
    }
    for (const auto &component : path)
    {
        if (component == "." || component == "..")
        {
            throw luma::file_failure(LR_INVALID_ARGUMENT, "File paths must not contain dot components.");
        }
    }
    return path;
}

template <class Function> lr_status boundary(lr_error *error, Function &&function) noexcept
{
    if (!valid_error(error))
    {
        return LR_INVALID_ARGUMENT;
    }
    try
    {
        function();
        return report(error, LR_OK, "");
    }
    catch (const luma::file_failure &failure)
    {
        return report(error, failure.status, failure.what());
    }
    catch (const std::bad_alloc &)
    {
        return report(error, LR_RESOURCE_LIMIT, "Cannot allocate output transaction resources.");
    }
    catch (...)
    {
        return report(error, LR_INTERNAL_ERROR, "Output transaction failed unexpectedly.");
    }
}
} // namespace

struct lr_output_transaction
{
    std::unique_ptr<luma::output_storage> storage;
    std::mutex mutex;
    lr_output_snapshot snapshot{};
};

extern "C" lr_status lr_files_same(lr_utf8_view left, lr_utf8_view right, uint32_t *result, lr_error *error)
{
    return boundary(error,
                    [&]
                    {
                        if (result == nullptr)
                        {
                            throw luma::file_failure(LR_INVALID_ARGUMENT, "Missing identity result.");
                        }
                        const auto first = parse_path(left), second = parse_path(right);
                        const bool same = luma::files_same(first, second);
                        *result = same ? 1 : 0;
                    });
}

extern "C" lr_status lr_output_create(const lr_output_options *options, lr_output_transaction **result,
                                      lr_error *error)
{
    return boundary(
        error,
        [&]
        {
            if (options == nullptr || result == nullptr || options->struct_size != sizeof(*options) ||
                options->abi_version != LR_ABI_VERSION || options->overwrite > 1 || options->reserved != 0)
            {
                throw luma::file_failure(LR_INVALID_ARGUMENT, "Invalid output transaction options.");
            }
            const auto source = parse_path(options->source), destination = parse_path(options->destination);
            if (destination.filename().empty() || destination.filename() == "." ||
                destination.filename() == "..")
            {
                throw luma::file_failure(LR_INVALID_ARGUMENT, "The destination needs a file name.");
            }
            auto transaction = std::make_unique<lr_output_transaction>();
            lr_output_snapshot_init(&transaction->snapshot, sizeof(transaction->snapshot));
            transaction->storage = luma::create_output_storage(source, destination, options->overwrite != 0);
            transaction->snapshot.source_size = transaction->storage->source_size();
            transaction->snapshot.flags = LR_OUTPUT_WORKSPACE_EXISTS;
            *result = transaction.release();
        });
}

extern "C" lr_status lr_output_get_snapshot(lr_output_transaction *transaction, lr_output_snapshot *result)
{
    return boundary(nullptr,
                    [&]
                    {
                        if (transaction == nullptr || result == nullptr ||
                            result->struct_size != sizeof(*result) || result->abi_version != LR_ABI_VERSION)
                        {
                            throw luma::file_failure(LR_INVALID_ARGUMENT, "Invalid output snapshot.");
                        }
                        std::lock_guard lock(transaction->mutex);
                        *result = transaction->snapshot;
                    });
}

extern "C" lr_status lr_output_copy_path(lr_output_transaction *transaction, uint32_t kind,
                                         uint8_t *destination, uint64_t capacity, uint64_t *required)
{
    return boundary(nullptr,
                    [&]
                    {
                        if (transaction == nullptr || required == nullptr ||
                            (capacity != 0 && destination == nullptr) || kind < LR_OUTPUT_PATH_WORKSPACE ||
                            kind > LR_OUTPUT_PATH_DESTINATION)
                        {
                            throw luma::file_failure(LR_INVALID_ARGUMENT, "Invalid path snapshot request.");
                        }
                        std::lock_guard lock(transaction->mutex);
                        const auto path = transaction->storage->path(kind);
                        *required = path.size();
                        if (capacity < path.size())
                        {
                            throw luma::file_failure(LR_BUFFER_TOO_SMALL, "Path buffer is too small.");
                        }
                        if (!path.empty())
                        {
                            std::memcpy(destination, path.data(), path.size());
                        }
                    });
}

extern "C" lr_status lr_output_seal(lr_output_transaction *transaction, uint32_t cleanup_confirmed,
                                    lr_error *error)
{
    return boundary(
        error,
        [&]
        {
            if (transaction == nullptr || cleanup_confirmed != 1)
            {
                throw luma::file_failure(LR_INVALID_ARGUMENT, "Sealing requires confirmed process cleanup.");
            }
            std::lock_guard lock(transaction->mutex);
            if (transaction->snapshot.state != LR_OUTPUT_CREATED)
            {
                throw luma::file_failure(LR_INVALID_ARGUMENT, "Only a new transaction can be sealed.");
            }
            transaction->snapshot.output_size = transaction->storage->seal();
            transaction->snapshot.state = LR_OUTPUT_SEALED;
        });
}

extern "C" lr_status lr_output_commit(lr_output_transaction *transaction, lr_error *error)
{
    return boundary(error,
                    [&]
                    {
                        if (transaction == nullptr)
                        {
                            throw luma::file_failure(LR_INVALID_ARGUMENT, "Invalid output transaction.");
                        }
                        std::lock_guard lock(transaction->mutex);
                        if (transaction->snapshot.state != LR_OUTPUT_SEALED)
                        {
                            throw luma::file_failure(LR_INVALID_ARGUMENT, "Commit requires a sealed output.");
                        }
                        transaction->storage->publish();
                        transaction->snapshot.state = LR_OUTPUT_COMMITTED;
                        // Publication is the point of no return. A retained empty/extra-file
                        // workspace is reflected in flags, never as a false encoding failure.
                        try
                        {
                            transaction->storage->cleanup();
                            transaction->snapshot.flags = 0;
                        }
                        catch (...)
                        {
                        }
                    });
}

extern "C" lr_status lr_output_cleanup(lr_output_transaction *transaction, uint32_t cleanup_confirmed,
                                       lr_error *error)
{
    return boundary(error,
                    [&]
                    {
                        if (transaction == nullptr || cleanup_confirmed > 1)
                        {
                            throw luma::file_failure(LR_INVALID_ARGUMENT, "Invalid output cleanup request.");
                        }
                        std::lock_guard lock(transaction->mutex);
                        auto &state = transaction->snapshot;
                        if ((state.flags & LR_OUTPUT_WORKSPACE_EXISTS) == 0)
                        {
                            return;
                        }
                        if (state.state != LR_OUTPUT_COMMITTED)
                        {
                            state.state = LR_OUTPUT_RETAINED;
                        }
                        if (cleanup_confirmed == 0)
                        {
                            throw luma::file_failure(LR_IO_ERROR,
                                                     "Process cleanup is unconfirmed; retain the workspace.");
                        }
                        transaction->storage->cleanup();
                        state.flags = 0;
                        if (state.state != LR_OUTPUT_COMMITTED)
                        {
                            state.state = LR_OUTPUT_ABORTED;
                        }
                    });
}

extern "C" void lr_output_release(lr_output_transaction *transaction)
{
    delete transaction;
}
