#ifndef LUMA_PROBE_JSON_HPP
#define LUMA_PROBE_JSON_HPP

#include "lumarecode/core.h"
#include "yyjson.h"

#include <cstddef>
#include <memory>
#include <memory_resource>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>

namespace luma
{
struct probe_failure : std::runtime_error
{
    probe_failure(lr_status status, const std::string &message) : std::runtime_error(message), status(status)
    {
    }
    lr_status status;
};

/* All yyjson trees, strings, normalization maps and writer scratch share one
 * budget. The allocator outlives documents and does not call user callbacks. */
class probe_json final : public std::pmr::memory_resource
{
  public:
    explicit probe_json(size_t limit = 128 * 1024 * 1024);
    ~probe_json();
    probe_json(const probe_json &) = delete;
    probe_json &operator=(const probe_json &) = delete;
    yyjson_mut_doc *doc() const
    {
        return document;
    }
    yyjson_mut_val *parse(std::span<const uint8_t> bytes);
    yyjson_mut_val *copy(yyjson_mut_val *value);
    yyjson_mut_val *object();
    yyjson_mut_val *array();
    yyjson_mut_val *string(std::string_view value);
    void put(yyjson_mut_val *object, std::string_view key, yyjson_mut_val *value);
    void append(yyjson_mut_val *array, yyjson_mut_val *value);
    std::string write(yyjson_mut_val *value);
    std::string text(yyjson_mut_val *value);
    size_t peak_bytes() const
    {
        return peak;
    }

  private:
    static void *allocate(void *context, size_t size);
    static void *resize(void *context, void *pointer, size_t old_size, size_t size);
    static void deallocate(void *context, void *pointer);
    yyjson_mut_val *normalize(yyjson_val *value, unsigned depth);
    void *do_allocate(size_t bytes, size_t alignment) override;
    void do_deallocate(void *pointer, size_t, size_t) override;
    bool do_is_equal(const std::pmr::memory_resource &other) const noexcept override
    {
        return this == &other;
    }
    size_t used = 0, peak = 0, limit;
    bool allocation_failed = false;
    yyjson_alc allocator;
    yyjson_mut_doc *document = nullptr;
};

inline yyjson_mut_val *member(yyjson_mut_val *object, const char *key)
{
    return yyjson_mut_obj_get(object, key);
}
inline std::string_view string_value(yyjson_mut_val *value)
{
    return yyjson_mut_is_str(value) ? std::string_view(yyjson_mut_get_str(value), yyjson_mut_get_len(value))
                                    : std::string_view{};
}
bool unspecified(std::string_view value);
double probe_number(std::string_view value, double fallback = 0);
} // namespace luma
#endif
