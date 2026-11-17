#include "output_internal.hpp"

#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <array>
#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <random>

namespace
{
class descriptor
{
  public:
    ~descriptor()
    {
        reset();
    }
    descriptor() = default;
    descriptor(const descriptor &) = delete;
    descriptor &operator=(const descriptor &) = delete;
    int get() const
    {
        return value;
    }
    void reset(int replacement = -1)
    {
        if (value != -1)
            close(value);
        value = replacement;
    }

  private:
    int value = -1;
};

bool identity(const struct stat &a, const struct stat &b)
{
    return a.st_dev == b.st_dev && a.st_ino == b.st_ino;
}
bool revision(const struct stat &a, const struct stat &b)
{
#ifdef __APPLE__
    const auto am = a.st_mtimespec, bm = b.st_mtimespec, ac = a.st_ctimespec, bc = b.st_ctimespec;
#else
    const auto am = a.st_mtim, bm = b.st_mtim, ac = a.st_ctim, bc = b.st_ctim;
#endif
    return identity(a, b) && a.st_size == b.st_size && am.tv_sec == bm.tv_sec && am.tv_nsec == bm.tv_nsec &&
           ac.tv_sec == bc.tv_sec && ac.tv_nsec == bc.tv_nsec;
}
struct stat info(int fd)
{
    struct stat result{};
    if (fstat(fd, &result) == -1)
        luma::file_os_error("fstat", errno);
    return result;
}
void open_regular(descriptor &file, const std::filesystem::path &path)
{
    const auto fd = open(path.c_str(), O_RDONLY | O_NONBLOCK | O_CLOEXEC);
    if (fd == -1)
    {
        if (errno == ENOENT)
            throw luma::file_failure(LR_NOT_FOUND, "File does not exist.");
        luma::file_os_error("open(source)", errno);
    }
    file.reset(fd);
    if (!S_ISREG(info(fd).st_mode))
        throw luma::file_failure(LR_INVALID_ARGUMENT, "Expected a regular file.");
}
std::string random_name()
{
    std::array<unsigned char, 16> bytes;
#ifdef __APPLE__
    arc4random_buf(bytes.data(), bytes.size());
#else
    std::random_device random;
    for (auto &byte : bytes)
        byte = static_cast<unsigned char>(random());
#endif
    constexpr char hex[] = "0123456789abcdef";
    std::string name = ".lumarecode-";
    for (auto byte : bytes)
    {
        name += hex[byte >> 4];
        name += hex[byte & 15];
    }
    return name;
}

class storage final : public luma::output_storage
{
  public:
    storage(std::filesystem::path source, std::filesystem::path destination, bool overwrite)
        : source_path(std::move(source)), destination(std::move(destination)), overwrite(overwrite)
    {
    }

    void initialize()
    {
        open_regular(source, source_path);
        source_initial = info(source.get());
        std::error_code error;
        parent_path = std::filesystem::canonical(destination.parent_path(), error);
        if (error)
            luma::file_os_error("resolve output parent", static_cast<unsigned long>(error.value()));
        parent.reset(open(parent_path.c_str(), O_RDONLY | O_DIRECTORY | O_CLOEXEC));
        if (parent.get() == -1)
            luma::file_os_error("open(output parent)", errno);
        parent_initial = info(parent.get());
        validate_destination();
        leaf = "result" + destination.extension().string();
        for (unsigned attempt = 0; attempt < 16; ++attempt)
        {
            work_name = random_name();
            if (mkdirat(parent.get(), work_name.c_str(), 0700) == 0)
            {
                directory_exists = true;
                work.reset(
                    openat(parent.get(), work_name.c_str(), O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC));
                if (work.get() == -1)
                    luma::file_os_error("open(workspace)", errno);
                work_initial = info(work.get());
                validate_bindings();
                return;
            }
            if (errno != EEXIST)
                luma::file_os_error("mkdirat", errno);
        }
        throw luma::file_failure(LR_IO_ERROR, "Cannot reserve a unique workspace.");
    }
    uint64_t source_size() const override
    {
        return static_cast<uint64_t>(source_initial.st_size);
    }
    std::string path(uint32_t kind) const override
    {
        if (kind == LR_OUTPUT_PATH_DESTINATION)
            return luma::path_utf8(destination);
        auto result = parent_path / work_name;
        if (kind == LR_OUTPUT_PATH_STAGING)
            result /= leaf;
        return luma::path_utf8(result);
    }
    uint64_t seal() override
    {
        validate_source();
        validate_bindings();
        descriptor candidate;
        candidate.reset(openat(work.get(), leaf.c_str(), O_RDWR | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC));
        if (candidate.get() == -1)
            luma::file_os_error("open(staging)", errno);
        const auto attributes = info(candidate.get());
        if (!S_ISREG(attributes.st_mode) || attributes.st_size <= 0 || attributes.st_nlink != 1 ||
            identity(attributes, source_initial))
            throw luma::file_failure(LR_INVALID_DATA,
                                     "Staging must be a nonempty, single-link regular output.");
        if (fsync(candidate.get()) == -1)
            luma::file_os_error("fsync(staging)", errno);
        // Only transfer the descriptor after validation; failed seals can be
        // corrected by the caller without leaving a half-sealed native handle.
        staged.reset(dup(candidate.get()));
        if (staged.get() == -1)
            luma::file_os_error("dup(staging)", errno);
        if (fcntl(staged.get(), F_SETFD, FD_CLOEXEC) == -1)
            luma::file_os_error("fcntl(staging)", errno);
        staged_initial = info(staged.get());
        return static_cast<uint64_t>(staged_initial.st_size);
    }
    void publish() override
    {
        validate_source();
        validate_bindings();
        struct stat entry{};
        if (!revision(staged_initial, info(staged.get())) ||
            fstatat(work.get(), leaf.c_str(), &entry, AT_SYMLINK_NOFOLLOW) == -1 || !S_ISREG(entry.st_mode) ||
            entry.st_nlink != 1 || !revision(staged_initial, entry))
            throw luma::file_failure(LR_INVALID_DATA, "Sealed staging output changed.");
        validate_destination();
#ifdef __APPLE__
        const auto flags = overwrite ? 0U : RENAME_EXCL;
        if (renameatx_np(work.get(), leaf.c_str(), parent.get(), destination.filename().c_str(), flags) == -1)
#else
        // The supported macOS target uses a single no-replace rename. The
        // POSIX fallback publishes with an exclusive link, then removes staging.
        if ((overwrite
                 ? renameat(work.get(), leaf.c_str(), parent.get(), destination.filename().c_str())
                 : linkat(work.get(), leaf.c_str(), parent.get(), destination.filename().c_str(), 0)) == -1)
#endif
        {
            if (errno == EEXIST)
                throw luma::file_failure(LR_ALREADY_EXISTS, "Destination already exists.");
            luma::file_os_error("publish output", errno);
        }
#ifndef __APPLE__
        if (!overwrite)
            unlinkat(work.get(), leaf.c_str(), 0);
#endif
        // No throwing work after the namespace operation: committed output must
        // remain successful if subsequent directory cleanup cannot complete.
    }
    void cleanup() override
    {
        if (!directory_exists)
            return;
        validate_bindings();
        staged.reset();
        if (unlinkat(work.get(), leaf.c_str(), 0) == -1 && errno != ENOENT)
            luma::file_os_error("unlink staging", errno);
        if (unlinkat(parent.get(), work_name.c_str(), AT_REMOVEDIR) == -1)
            luma::file_os_error("remove empty workspace", errno);
        directory_exists = false;
        work.reset();
    }
    void cleanup_failed_creation() noexcept
    {
        if (directory_exists)
        {
            try
            {
                cleanup();
            }
            catch (...)
            {
            }
        }
    }

  private:
    void validate_source()
    {
        struct stat current{};
        if (!revision(source_initial, info(source.get())) || stat(source_path.c_str(), &current) == -1 ||
            !revision(source_initial, current))
            throw luma::file_failure(LR_SOURCE_CHANGED, "Source file identity or revision changed.");
    }
    void validate_bindings()
    {
        struct stat current{};
        if (stat(destination.parent_path().c_str(), &current) == -1 || !identity(current, parent_initial))
            throw luma::file_failure(LR_IO_ERROR, "Output parent changed.");
        if (work.get() != -1 &&
            (fstatat(parent.get(), work_name.c_str(), &current, AT_SYMLINK_NOFOLLOW) == -1 ||
             !S_ISDIR(current.st_mode) || !identity(current, work_initial)))
            throw luma::file_failure(LR_IO_ERROR, "Workspace directory binding changed.");
    }
    void validate_destination()
    {
        struct stat current{};
        if (fstatat(parent.get(), destination.filename().c_str(), &current, AT_SYMLINK_NOFOLLOW) == -1)
        {
            if (errno == ENOENT)
                return;
            luma::file_os_error("stat(destination)", errno);
        }
        if (!S_ISREG(current.st_mode) || identity(current, source_initial))
            throw luma::file_failure(LR_INVALID_ARGUMENT,
                                     "Destination is an alias or is not a regular file.");
        if (!overwrite)
            throw luma::file_failure(LR_ALREADY_EXISTS, "Destination already exists.");
    }

    std::filesystem::path source_path, destination, parent_path;
    std::string work_name, leaf;
    descriptor source, parent, work, staged;
    struct stat source_initial{}, parent_initial{}, work_initial{}, staged_initial{};
    bool overwrite;
    bool directory_exists = false;
};
} // namespace

bool luma::files_same(const std::filesystem::path &left, const std::filesystem::path &right)
{
    descriptor first, second;
    open_regular(first, left);
    open_regular(second, right);
    return identity(info(first.get()), info(second.get()));
}
std::unique_ptr<luma::output_storage> luma::create_output_storage(const std::filesystem::path &source,
                                                                  const std::filesystem::path &destination,
                                                                  bool overwrite)
{
    auto result = std::make_unique<storage>(source, destination, overwrite);
    try
    {
        result->initialize();
    }
    catch (...)
    {
        result->cleanup_failed_creation();
        throw;
    }
    return result;
}
