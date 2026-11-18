#ifndef LUMA_OUTPUT_INTERNAL_HPP
#define LUMA_OUTPUT_INTERNAL_HPP

#include "lumarecode/output.h"

#include <exception>
#include <filesystem>
#include <memory>
#include <string>

namespace luma
{
class file_failure final : public std::exception
{
  public:
    file_failure(lr_status status, std::string text) : status(status), text(std::move(text)) {}
    const char *what() const noexcept override
    {
        return text.c_str();
    }
    lr_status status;
    std::string text;
};

struct output_storage
{
    virtual ~output_storage() = default;
    virtual uint64_t source_size() const = 0;
    virtual std::string path(uint32_t kind) const = 0;
    virtual uint64_t seal() = 0;
    virtual void publish() = 0;
    virtual void cleanup() = 0;
};

struct source_lease
{
    virtual ~source_lease() = default;
    virtual void validate() const = 0;
};
std::unique_ptr<source_lease> open_source_lease(const std::filesystem::path &path);

std::unique_ptr<output_storage> create_output_storage(const std::filesystem::path &source,
                                                      const std::filesystem::path &destination,
                                                      bool overwrite);
bool files_same(const std::filesystem::path &left, const std::filesystem::path &right);

inline std::string path_utf8(const std::filesystem::path &path)
{
    const auto bytes = path.u8string();
    return {bytes.begin(), bytes.end()};
}
[[noreturn]] inline void file_os_error(const char *operation, unsigned long code)
{
    throw file_failure(LR_IO_ERROR, std::string(operation) + " failed (" + std::to_string(code) + ").");
}
} // namespace luma
#endif
