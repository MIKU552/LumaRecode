#include "output_internal.hpp"

#include <windows.h>

#include <bcrypt.h>
#include <sddl.h>

#include <array>
#include <cstring>
#include <vector>

namespace
{
class file_handle
{
  public:
    ~file_handle()
    {
        reset();
    }
    file_handle() = default;
    file_handle(const file_handle &) = delete;
    file_handle &operator=(const file_handle &) = delete;
    HANDLE get() const
    {
        return value;
    }
    HANDLE take()
    {
        const auto old = value;
        value = INVALID_HANDLE_VALUE;
        return old;
    }
    void reset(HANDLE replacement = INVALID_HANDLE_VALUE)
    {
        if (value != INVALID_HANDLE_VALUE)
            CloseHandle(value);
        value = replacement;
    }

  private:
    HANDLE value = INVALID_HANDLE_VALUE;
};

struct file_info
{
    FILE_ID_INFO id{};
    FILE_BASIC_INFO basic{};
    FILE_STANDARD_INFO standard{};
};
file_info info(HANDLE file)
{
    file_info result;
    if (!GetFileInformationByHandleEx(file, FileIdInfo, &result.id, sizeof(result.id)) ||
        !GetFileInformationByHandleEx(file, FileBasicInfo, &result.basic, sizeof(result.basic)) ||
        !GetFileInformationByHandleEx(file, FileStandardInfo, &result.standard, sizeof(result.standard)))
        luma::file_os_error("GetFileInformationByHandleEx", GetLastError());
    return result;
}
bool identity(const file_info &a, const file_info &b)
{
    return a.id.VolumeSerialNumber == b.id.VolumeSerialNumber &&
           std::memcmp(&a.id.FileId, &b.id.FileId, sizeof(a.id.FileId)) == 0;
}
bool revision(const file_info &a, const file_info &b)
{
    return identity(a, b) && a.standard.EndOfFile.QuadPart == b.standard.EndOfFile.QuadPart &&
           a.basic.LastWriteTime.QuadPart == b.basic.LastWriteTime.QuadPart &&
           a.basic.ChangeTime.QuadPart == b.basic.ChangeTime.QuadPart;
}
bool regular(HANDLE file, const file_info &value)
{
    return GetFileType(file) == FILE_TYPE_DISK && !value.standard.Directory;
}
bool missing(DWORD code)
{
    return code == ERROR_FILE_NOT_FOUND || code == ERROR_PATH_NOT_FOUND;
}
void open_regular(file_handle &file, const std::filesystem::path &path)
{
    // Share reads only: the source remains available to the encoder while
    // concurrent writers, renames and deletion are denied for this transaction.
    file.reset(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr));
    if (file.get() == INVALID_HANDLE_VALUE)
    {
        const auto code = GetLastError();
        if (missing(code))
            throw luma::file_failure(LR_NOT_FOUND, "File does not exist.");
        luma::file_os_error("open source", code);
    }
    if (!regular(file.get(), info(file.get())))
        throw luma::file_failure(LR_INVALID_ARGUMENT, "Expected a regular file.");
}
std::filesystem::path final_path(HANDLE file)
{
    const auto size = GetFinalPathNameByHandleW(file, nullptr, 0, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (size == 0)
        luma::file_os_error("GetFinalPathNameByHandleW", GetLastError());
    std::vector<wchar_t> buffer(size);
    const auto copied =
        GetFinalPathNameByHandleW(file, buffer.data(), size, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (copied == 0 || copied >= size)
        luma::file_os_error("GetFinalPathNameByHandleW", GetLastError());
    return std::wstring(buffer.data(), copied);
}
std::wstring random_name()
{
    std::array<unsigned char, 16> bytes;
    if (BCryptGenRandom(nullptr, bytes.data(), static_cast<ULONG>(bytes.size()),
                        BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0)
        throw luma::file_failure(LR_IO_ERROR, "Cannot generate a workspace name.");
    constexpr wchar_t hex[] = L"0123456789abcdef";
    std::wstring name = L".lumarecode-";
    for (auto byte : bytes)
    {
        name += hex[byte >> 4];
        name += hex[byte & 15];
    }
    return name;
}
class private_directory_security
{
  public:
    private_directory_security()
    {
        // A protected inheritable DACL limits the workspace to its owner,
        // administrators and SYSTEM. It does not inherit a shared parent's ACL.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;OW)", SDDL_REVISION_1, &value, nullptr))
            luma::file_os_error("workspace security descriptor", GetLastError());
    }
    ~private_directory_security()
    {
        LocalFree(value);
    }
    SECURITY_ATTRIBUTES attributes() const
    {
        return {sizeof(SECURITY_ATTRIBUTES), value, FALSE};
    }

  private:
    PSECURITY_DESCRIPTOR value = nullptr;
};

class storage final : public luma::output_storage
{
  public:
    storage(std::filesystem::path source, std::filesystem::path destination, bool overwrite)
        : source_path(std::move(source)), destination(std::move(destination)), overwrite(overwrite)
    {
    }
    void initialize()
    {
        const auto name = destination.filename().native();
        if (name.find(L':') != std::wstring::npos || name.back() == L'.' || name.back() == L' ')
            throw luma::file_failure(LR_INVALID_ARGUMENT, "Invalid Windows destination name.");
        open_regular(source, source_path);
        source_initial = info(source.get());
        parent.reset(CreateFileW(destination.parent_path().c_str(),
                                 FILE_LIST_DIRECTORY | FILE_TRAVERSE | FILE_READ_ATTRIBUTES | FILE_ADD_FILE,
                                 FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                                 FILE_FLAG_BACKUP_SEMANTICS, nullptr));
        if (parent.get() == INVALID_HANDLE_VALUE)
            luma::file_os_error("open output parent", GetLastError());
        parent_initial = info(parent.get());
        if (!parent_initial.standard.Directory)
            throw luma::file_failure(LR_INVALID_ARGUMENT, "Output parent must be a directory.");
        parent_path = final_path(parent.get());
        validate_destination();
        leaf = L"result" + destination.extension().native();
        private_directory_security security;
        auto attributes = security.attributes();
        for (unsigned attempt = 0; attempt < 16; ++attempt)
        {
            work_path = parent_path / random_name();
            if (CreateDirectoryW(work_path.c_str(), &attributes))
            {
                directory_exists = true;
                work.reset(CreateFileW(work_path.c_str(), FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | DELETE,
                                       FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                                       FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
                if (work.get() == INVALID_HANDLE_VALUE)
                    luma::file_os_error("open workspace", GetLastError());
                work_initial = info(work.get());
                if (!work_initial.standard.Directory ||
                    (work_initial.basic.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT))
                    throw luma::file_failure(LR_IO_ERROR, "Workspace is not an ordinary directory.");
                validate_bindings();
                return;
            }
            const auto code = GetLastError();
            if (code != ERROR_ALREADY_EXISTS)
                luma::file_os_error("create workspace", code);
        }
        throw luma::file_failure(LR_IO_ERROR, "Cannot reserve a unique workspace.");
    }
    uint64_t source_size() const override
    {
        return static_cast<uint64_t>(source_initial.standard.EndOfFile.QuadPart);
    }
    std::string path(uint32_t kind) const override
    {
        if (kind == LR_OUTPUT_PATH_DESTINATION)
            return luma::path_utf8(destination);
        return luma::path_utf8(kind == LR_OUTPUT_PATH_STAGING ? work_path / leaf : work_path);
    }
    uint64_t seal() override
    {
        validate_source();
        validate_bindings();
        file_handle candidate;
        candidate.reset(CreateFileW((work_path / leaf).c_str(), GENERIC_READ | GENERIC_WRITE | DELETE,
                                    FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT,
                                    nullptr));
        if (candidate.get() == INVALID_HANDLE_VALUE)
            luma::file_os_error("open staging", GetLastError());
        const auto attributes = info(candidate.get());
        if (!regular(candidate.get(), attributes) || attributes.standard.EndOfFile.QuadPart <= 0 ||
            attributes.standard.NumberOfLinks != 1 ||
            (attributes.basic.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) ||
            identity(attributes, source_initial))
            throw luma::file_failure(LR_INVALID_DATA,
                                     "Staging must be a nonempty, single-link regular output.");
        if (!FlushFileBuffers(candidate.get()))
            luma::file_os_error("flush staging", GetLastError());
        staged_initial = info(candidate.get());
        staged.reset(candidate.take());
        return static_cast<uint64_t>(staged_initial.standard.EndOfFile.QuadPart);
    }
    void publish() override
    {
        validate_source();
        validate_bindings();
        file_handle current;
        current.reset(CreateFileW((work_path / leaf).c_str(), FILE_READ_ATTRIBUTES,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                                  OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (current.get() == INVALID_HANDLE_VALUE || !revision(staged_initial, info(current.get())) ||
            !revision(staged_initial, info(staged.get())))
            throw luma::file_failure(LR_INVALID_DATA, "Sealed staging output changed.");
        current.reset();
        validate_destination();
        // The Win32 wrapper rejects a relative RootDirectory rename on some
        // Windows builds. Resolve the full name from the pinned directory;
        // retain its no-delete sharing and recheck its requested path binding.
        const auto name = (final_path(parent.get()) / destination.filename()).native();
        const auto bytes = name.size() * sizeof(wchar_t);
        std::vector<unsigned char> buffer(offsetof(FILE_RENAME_INFO, FileName) + bytes + sizeof(wchar_t));
        auto *rename = reinterpret_cast<FILE_RENAME_INFO *>(buffer.data());
        rename->ReplaceIfExists = overwrite ? TRUE : FALSE;
        rename->RootDirectory = nullptr;
        rename->FileNameLength = static_cast<DWORD>(bytes);
        std::memcpy(rename->FileName, name.c_str(), bytes + sizeof(wchar_t));
        if (!SetFileInformationByHandle(staged.get(), FileRenameInfo, rename,
                                        static_cast<DWORD>(buffer.size())))
        {
            const auto code = GetLastError();
            if (code == ERROR_FILE_EXISTS || code == ERROR_ALREADY_EXISTS)
                throw luma::file_failure(LR_ALREADY_EXISTS, "Destination already exists.");
            luma::file_os_error("publish output", code);
        }
        // The staged handle now refers to the destination. Cleanup must close
        // it without setting a delete disposition on the published file.
    }
    void cleanup() override
    {
        if (!directory_exists)
            return;
        validate_bindings();
        staged.reset();
        if (!DeleteFileW((work_path / leaf).c_str()))
        {
            const auto code = GetLastError();
            if (!missing(code))
                luma::file_os_error("delete staging", code);
        }
        FILE_DISPOSITION_INFO disposition{TRUE};
        if (!SetFileInformationByHandle(work.get(), FileDispositionInfo, &disposition, sizeof(disposition)))
            luma::file_os_error("remove empty workspace", GetLastError());
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
        file_handle current;
        open_regular(current, source_path);
        if (!revision(source_initial, info(source.get())) || !revision(source_initial, info(current.get())))
            throw luma::file_failure(LR_SOURCE_CHANGED, "Source file identity or revision changed.");
    }
    void validate_bindings()
    {
        file_handle current;
        current.reset(CreateFileW(destination.parent_path().c_str(), FILE_READ_ATTRIBUTES,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                                  OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr));
        if (current.get() == INVALID_HANDLE_VALUE || !identity(parent_initial, info(current.get())))
            throw luma::file_failure(LR_IO_ERROR, "Output parent changed.");
        if (work.get() == INVALID_HANDLE_VALUE)
            throw luma::file_failure(LR_IO_ERROR, "Workspace is not pinned.");
        current.reset(CreateFileW(
            work_path.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (current.get() == INVALID_HANDLE_VALUE || !identity(work_initial, info(current.get())))
            throw luma::file_failure(LR_IO_ERROR, "Workspace directory binding changed.");
    }
    void validate_destination()
    {
        file_handle current;
        current.reset(CreateFileW((parent_path / destination.filename()).c_str(), FILE_READ_ATTRIBUTES,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                                  OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                                  nullptr));
        if (current.get() == INVALID_HANDLE_VALUE)
        {
            const auto code = GetLastError();
            if (missing(code))
                return;
            luma::file_os_error("inspect destination", code);
        }
        const auto value = info(current.get());
        if (!regular(current.get(), value) || (value.basic.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) ||
            identity(value, source_initial))
            throw luma::file_failure(LR_INVALID_ARGUMENT,
                                     "Destination aliases the source or is not a regular file.");
        if (!overwrite)
            throw luma::file_failure(LR_ALREADY_EXISTS, "Destination already exists.");
    }
    std::filesystem::path source_path, destination, parent_path, work_path;
    std::wstring leaf;
    file_handle source, parent, work, staged;
    file_info source_initial{}, parent_initial{}, work_initial{}, staged_initial{};
    bool overwrite;
    bool directory_exists = false;
};
} // namespace

namespace luma
{
std::unique_ptr<output_storage> create_output_storage(const std::filesystem::path &source,
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
bool files_same(const std::filesystem::path &left, const std::filesystem::path &right)
{
    file_handle first, second;
    open_regular(first, left);
    open_regular(second, right);
    return identity(info(first.get()), info(second.get()));
}
std::unique_ptr<source_lease> open_source_lease(const std::filesystem::path &path)
{
    class lease final : public source_lease
    {
      public:
        explicit lease(const std::filesystem::path &path) : path(path)
        {
            open_regular(file, path);
            original = info(file.get());
        }
        void validate() const override
        {
            file_handle current;
            open_regular(current, path);
            if (!revision(original, info(file.get())) || !revision(original, info(current.get())))
                throw file_failure(LR_SOURCE_CHANGED, "Source changed during inspection.");
        }

      private:
        std::filesystem::path path;
        file_handle file;
        file_info original;
    };
    return std::make_unique<lease>(path);
}
} // namespace luma
