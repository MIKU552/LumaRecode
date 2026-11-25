#include "probe_json.hpp"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <list>
#include <unordered_map>

namespace
{
union allocation_header
{
    size_t size;
    std::max_align_t alignment;
};
void require(bool success)
{
    if (!success)
        throw luma::probe_failure(LR_RESOURCE_LIMIT, "Probe JSON memory budget exhausted.");
}
} // namespace

namespace luma
{
probe_json::probe_json(size_t limit) : limit(limit), allocator{allocate, resize, deallocate, this}
{
    document = yyjson_mut_doc_new(&allocator);
    require(document != nullptr);
}
probe_json::~probe_json()
{
    yyjson_mut_doc_free(document);
}
void *probe_json::allocate(void *context, size_t size)
{
    auto &owner = *static_cast<probe_json *>(context);
    if (size > std::numeric_limits<size_t>::max() - sizeof(allocation_header) ||
        size + sizeof(allocation_header) > owner.limit - owner.used)
    {
        owner.allocation_failed = true;
        return nullptr;
    }
    const auto total = size + sizeof(allocation_header);
    auto *header = static_cast<allocation_header *>(std::malloc(total));
    if (header == nullptr)
    {
        owner.allocation_failed = true;
        return nullptr;
    }
    header->size = total;
    owner.used += total;
    owner.peak = std::max(owner.peak, owner.used);
    return header + 1;
}
void *probe_json::resize(void *context, void *pointer, size_t, size_t size)
{
    if (pointer == nullptr)
        return allocate(context, size);
    if (size == 0)
    {
        deallocate(context, pointer);
        return nullptr;
    }
    auto &owner = *static_cast<probe_json *>(context);
    auto *old = static_cast<allocation_header *>(pointer) - 1;
    if (size > std::numeric_limits<size_t>::max() - sizeof(allocation_header) ||
        size + sizeof(allocation_header) > owner.limit - (owner.used - old->size))
    {
        owner.allocation_failed = true;
        return nullptr;
    }
    const auto previous = old->size, total = size + sizeof(allocation_header);
    auto *header = static_cast<allocation_header *>(std::realloc(old, total));
    if (header == nullptr)
    {
        owner.allocation_failed = true;
        return nullptr;
    }
    header->size = total;
    owner.used = owner.used - previous + total;
    owner.peak = std::max(owner.peak, owner.used);
    return header + 1;
}
void probe_json::deallocate(void *context, void *pointer)
{
    if (pointer == nullptr)
        return;
    auto &owner = *static_cast<probe_json *>(context);
    auto *header = static_cast<allocation_header *>(pointer) - 1;
    owner.used -= header->size;
    std::free(header);
}
void *probe_json::do_allocate(size_t bytes, size_t alignment)
{
    if (alignment > alignof(std::max_align_t))
        throw std::bad_alloc();
    void *result = allocate(this, bytes);
    if (result == nullptr)
        throw std::bad_alloc();
    return result;
}
void probe_json::do_deallocate(void *pointer, size_t, size_t)
{
    deallocate(this, pointer);
}
yyjson_mut_val *probe_json::object()
{
    auto *result = yyjson_mut_obj(document);
    require(result != nullptr);
    return result;
}
yyjson_mut_val *probe_json::array()
{
    auto *result = yyjson_mut_arr(document);
    require(result != nullptr);
    return result;
}
yyjson_mut_val *probe_json::string(std::string_view value)
{
    auto *result = yyjson_mut_strncpy(document, value.empty() ? "" : value.data(), value.size());
    require(result != nullptr);
    return result;
}
void probe_json::put(yyjson_mut_val *object, std::string_view key, yyjson_mut_val *value)
{
    auto *name = string(key);
    require(value != nullptr && (yyjson_mut_obj_getn(object, key.data(), key.size()) != nullptr
                                     ? yyjson_mut_obj_replace(object, name, value)
                                     : yyjson_mut_obj_add(object, name, value)));
}
void probe_json::append(yyjson_mut_val *array, yyjson_mut_val *value)
{
    require(value != nullptr && yyjson_mut_arr_append(array, value));
}
yyjson_mut_val *probe_json::copy(yyjson_mut_val *value)
{
    auto *result = yyjson_mut_val_mut_copy(document, value);
    require(result != nullptr);
    return result;
}
yyjson_mut_val *probe_json::normalize(yyjson_val *value, unsigned depth)
{
    if (depth > 64)
        throw probe_failure(LR_RESOURCE_LIMIT, "Probe JSON nesting limit exceeded.");
    if (yyjson_is_obj(value))
    {
        auto *result = object();
        struct entry
        {
            std::string_view name;
            yyjson_mut_val *value;
            bool repeated;
        };
        std::pmr::list<entry> ordered(this);
        std::pmr::unordered_map<std::string_view, decltype(ordered)::iterator> seen(this);
        yyjson_obj_iter iterator = yyjson_obj_iter_with(value);
        while (auto *key = yyjson_obj_iter_next(&iterator))
        {
            const std::string_view name(yyjson_get_str(key), yyjson_get_len(key));
            auto *item = normalize(yyjson_obj_iter_get_val(key), depth + 1);
            const auto [position, added] = seen.try_emplace(name, ordered.end());
            if (added)
            {
                ordered.push_back({name, item, false});
                position->second = std::prev(ordered.end());
            }
            else
            {
                auto &previous = *position->second;
                if (!previous.repeated)
                {
                    auto *duplicates = array();
                    append(duplicates, previous.value);
                    previous.value = duplicates;
                    previous.repeated = true;
                    ordered.splice(ordered.end(), ordered, position->second);
                }
                append(previous.value, item);
            }
        }
        for (const auto &entry : ordered)
            require(yyjson_mut_obj_add(result, string(entry.name), entry.value));
        return result;
    }
    if (yyjson_is_arr(value))
    {
        auto *result = array();
        yyjson_arr_iter iterator = yyjson_arr_iter_with(value);
        while (auto *item = yyjson_arr_iter_next(&iterator))
            append(result, normalize(item, depth + 1));
        return result;
    }
    auto *result = yyjson_val_mut_copy(document, value);
    require(result != nullptr);
    return result;
}
yyjson_mut_val *probe_json::parse(std::span<const uint8_t> bytes)
{
    if (bytes.empty() || bytes.size() > 64 * 1024 * 1024)
        throw probe_failure(bytes.empty() ? LR_INVALID_DATA : LR_RESOURCE_LIMIT, "Invalid probe JSON size.");
    yyjson_read_err error{};
    // No INSITU flag: yyjson owns its input copy and never mutates capture data.
    std::unique_ptr<yyjson_doc, decltype(&yyjson_doc_free)> source(
        yyjson_read_opts(reinterpret_cast<char *>(const_cast<uint8_t *>(bytes.data())), bytes.size(), 0,
                         &allocator, &error),
        yyjson_doc_free);
    if (source == nullptr)
        throw probe_failure(allocation_failed || error.code == YYJSON_READ_ERROR_DEPTH ? LR_RESOURCE_LIMIT
                                                                                       : LR_INVALID_DATA,
                            "Invalid or over-budget ffprobe JSON.");
    return normalize(yyjson_doc_get_root(source.get()), 0);
}
std::string probe_json::write(yyjson_mut_val *value)
{
    size_t size = 0;
    yyjson_write_err error{};
    char *bytes = yyjson_mut_val_write_opts(value, 0, &allocator, &size, &error);
    if (bytes == nullptr)
        throw probe_failure(LR_RESOURCE_LIMIT, "Cannot serialize probe JSON within budget.");
    const auto free = [&](char *data) { deallocate(this, data); };
    std::unique_ptr<char, decltype(free)> owned(bytes, free);
    if (size > 64 * 1024 * 1024)
        throw probe_failure(LR_RESOURCE_LIMIT, "Probe result exceeds the output budget.");
    return {bytes, size};
}
std::string probe_json::text(yyjson_mut_val *value)
{
    if (value == nullptr || yyjson_mut_is_null(value))
        return {};
    if (yyjson_mut_is_str(value))
        return std::string(string_value(value));
    return write(value);
}
bool unspecified(std::string_view value)
{
    return value.empty() || value == "unknown" || value == "unspecified" || value == "reserved";
}
double probe_number(std::string_view value, double fallback)
{
    if (const auto slash = value.find('/'); slash != std::string_view::npos)
    {
        if (value.find('/', slash + 1) != std::string_view::npos)
            return fallback;
        const auto ratio =
            probe_number(value.substr(0, slash), NAN) / probe_number(value.substr(slash + 1), NAN);
        return std::isfinite(ratio) ? ratio : fallback;
    }
    while (!value.empty() &&
           (value.front() == ' ' || value.front() == '\t' || value.front() == '\r' || value.front() == '\n'))
        value.remove_prefix(1);
    while (!value.empty() &&
           (value.back() == ' ' || value.back() == '\t' || value.back() == '\r' || value.back() == '\n'))
        value.remove_suffix(1);
    if (!value.empty() && value.front() == '+')
    {
        value.remove_prefix(1);
        if (!value.empty() && (value.front() == '+' || value.front() == '-'))
            return fallback;
    }
    if (value.empty())
        return fallback;
    double result = 0;
    const auto converted = std::from_chars(value.data(), value.data() + value.size(), result);
    // .NET's numeric reader rounds finite underflow to signed zero. from_chars
    // reports both underflow and overflow as out_of_range; decimal magnitude
    // distinguishes them without locale state or unbounded exponent parsing.
    if (converted.ec == std::errc::result_out_of_range && converted.ptr == value.data() + value.size())
    {
        const bool negative = value.front() == '-';
        if (negative)
            value.remove_prefix(1);
        const auto exponent_position = value.find_first_of("eE");
        const auto mantissa = value.substr(0, exponent_position);
        int64_t exponent = 0;
        if (exponent_position != std::string_view::npos)
        {
            auto digits = value.substr(exponent_position + 1);
            const bool minus = !digits.empty() && digits.front() == '-';
            if (!digits.empty() && (digits.front() == '+' || minus))
                digits.remove_prefix(1);
            const auto bound = static_cast<int64_t>(value.size()) + 1000;
            for (char digit : digits)
                exponent = std::min(bound, exponent * 10 + digit - '0');
            if (minus)
                exponent = -exponent;
        }
        const auto dot = mantissa.find('.');
        const auto integral = static_cast<int64_t>(dot == std::string_view::npos ? mantissa.size() : dot);
        int64_t leading = 0;
        for (char digit : mantissa)
        {
            if (digit == '.')
                continue;
            if (digit != '0')
                break;
            ++leading;
        }
        if (integral - leading - 1 + exponent < 0)
            return negative ? -0.0 : 0.0;
    }
    return converted.ec == std::errc{} && converted.ptr == value.data() + value.size() &&
                   std::isfinite(result)
               ? result
               : fallback;
}
} // namespace luma
