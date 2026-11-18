#include "lumarecode/media.h"

#include <algorithm>
#include <cstring>
#include <new>
#include <stdexcept>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace
{
struct failure
{
    lr_status status;
    const char *message;
};
lr_status report(lr_error *error, lr_status status, const char *message)
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
std::string text(lr_utf8_view view, uint64_t &total)
{
    if (view.size > 65536 || view.size > 1048576 - total)
        throw failure{LR_RESOURCE_LIMIT, "Camera tags exceed the inspection budget."};
    if (lr_utf8_validate(view.data, view.size) != LR_OK)
        throw failure{LR_INVALID_ARGUMENT, "Camera tags must contain valid UTF-8."};
    total += view.size;
    return view.size == 0
               ? std::string{}
               : std::string(reinterpret_cast<const char *>(view.data), static_cast<size_t>(view.size));
}
std::string lower(std::string value)
{
    for (auto &byte : value)
        if (byte >= 'A' && byte <= 'Z')
            byte = static_cast<char>(byte + ('a' - 'A'));
    return value;
}
bool enabled(std::string_view value)
{
    return value == "1" || value == "true" || value == "yes";
}
std::string compact(std::string_view value)
{
    std::string result;
    result.reserve(value.size());
    for (char byte : value)
        if ((byte >= 'a' && byte <= 'z') || (byte >= '0' && byte <= '9'))
            result += byte;
    return result;
}
struct tag
{
    std::string key, value;
    uint32_t index;
};
lr_classification classify(const lr_classify_input &input)
{
    uint64_t total = 0;
    const auto codec = text(input.codec, total), transfer = text(input.transfer, total),
               schema = text(input.dji_schema, total);
    std::vector<tag> tags;
    std::unordered_map<std::string, size_t> positions;
    tags.reserve(input.tag_count);
    positions.reserve(input.tag_count);
    for (uint32_t index = 0; index < input.tag_count; ++index)
    {
        auto key = lower(text(input.tags[index].key, total)),
             value = lower(text(input.tags[index].value, total));
        const auto [position, added] = positions.try_emplace(key, tags.size());
        if (added)
            tags.push_back({std::move(key), std::move(value), index});
        else
        {
            // Metadata sources can differ only in key case. Match the merged
            // tag map: last value wins, first insertion keeps its priority.
            tags[position->second].value = std::move(value);
            tags[position->second].index = index;
        }
    }
    const auto get = [&](const char *key) -> const tag *
    {
        const auto found = positions.find(key);
        return found == positions.end() ? nullptr : &tags[found->second];
    };
    const auto result = [](uint32_t kind, uint32_t profile, uint32_t evidence, uint32_t index = UINT32_MAX)
    { return lr_classification{sizeof(lr_classification), LR_ABI_VERSION, kind, profile, evidence, index}; };
    if ((input.flags & LR_CLASSIFY_HAS_DOLBY_VISION) != 0)
        return result(input.dolby_vision_compatibility == 4 ? LR_MEDIA_HLG : LR_MEDIA_PQ, LR_PROFILE_NONE,
                      LR_EVIDENCE_DOLBY_VISION);
    if (codec == "prores_raw")
        return result(LR_MEDIA_RAW, LR_PROFILE_NONE, LR_EVIDENCE_RAW);
    for (const auto *key : {"com.apple.proapps.logcolorspace", "com.apple.proapps.color.logcolorspace"})
    {
        const auto *value = get(key);
        if (value != nullptr && value->value == "com.apple.apple-wide-gamut.apple-log")
            return result(LR_MEDIA_LOG, LR_PROFILE_APPLE_LOG2, LR_EVIDENCE_APPLE_LOG2, value->index);
        if (value != nullptr && value->value == "com.apple.log.apple-log")
            return result(LR_MEDIA_LOG, LR_PROFILE_APPLE_LOG, LR_EVIDENCE_APPLE_LOG, value->index);
    }
    if (const auto *value = get("com.xiaomi.record_log"); value != nullptr && enabled(value->value))
        return result(LR_MEDIA_LOG, LR_PROFILE_MI_LOG, LR_EVIDENCE_MI_LOG, value->index);
    if (const auto *value = get("com.samsung.android.logvideo");
        value != nullptr && !value->value.empty() && value->value != "0" && value->value != "false" &&
        value->value != "no")
        return result(LR_MEDIA_LOG, LR_PROFILE_SAMSUNG_LOG, LR_EVIDENCE_SAMSUNG_LOG, value->index);
    if ((input.flags & LR_CLASSIFY_HAS_DJI) != 0)
    {
        if (input.dji_mode == 19)
            return schema == "dvtm_PP-101.proto"
                       ? result(LR_MEDIA_LOG, LR_PROFILE_DJI_POCKET3, LR_EVIDENCE_DJI_DLOG_M)
                       : result(LR_MEDIA_LOG, LR_PROFILE_NONE, LR_EVIDENCE_DJI_OTHER_DLOG_M);
        if (input.dji_mode == 1 || input.dji_mode == 2 || input.dji_mode == 22)
            return result(LR_MEDIA_LOG, LR_PROFILE_NONE, LR_EVIDENCE_DJI_LOG);
        if (input.dji_mode == 9)
            return result(LR_MEDIA_HLG, LR_PROFILE_NONE, LR_EVIDENCE_DJI_HLG);
        if (input.dji_mode == 0)
            return result(LR_MEDIA_SDR, LR_PROFILE_NONE, LR_EVIDENCE_DJI_SDR);
    }
    const bool pocket = std::any_of(tags.begin(), tags.end(),
                                    [](const tag &entry) { return entry.value.contains("pocket"); });
    for (const auto &entry : tags)
    {
        if (entry.key.contains("colorgamma") || entry.key.contains("color_mode") ||
            entry.key.contains("colormode") || entry.key.contains("log_profile") ||
            entry.key.contains("gamma"))
        {
            const auto value = compact(entry.value);
            if (value == "dlogm" || value == "djidlogm")
                return result(LR_MEDIA_LOG, pocket ? LR_PROFILE_DJI_POCKET3 : LR_PROFILE_NONE,
                              LR_EVIDENCE_CAMERA_TAG, entry.index);
            if (value == "milog" || value == "xiaomilog" || value == "samsunglog")
                return result(LR_MEDIA_LOG,
                              value == "samsunglog" ? LR_PROFILE_SAMSUNG_LOG : LR_PROFILE_MI_LOG,
                              LR_EVIDENCE_CAMERA_TAG, entry.index);
            if (value.contains("log"))
                return result(LR_MEDIA_LOG, LR_PROFILE_NONE, LR_EVIDENCE_LOG_TAG, entry.index);
        }
    }
    for (const auto &entry : tags)
        if (entry.key.contains("log") && (enabled(entry.value) || entry.value == "log"))
            return result(LR_MEDIA_LOG, LR_PROFILE_NONE, LR_EVIDENCE_LOG_SWITCH, entry.index);
    if (transfer == "arib-std-b67")
        return result(LR_MEDIA_HLG, LR_PROFILE_NONE, LR_EVIDENCE_TRANSFER_HLG);
    if (transfer == "smpte2084")
        return result(LR_MEDIA_PQ, LR_PROFILE_NONE, LR_EVIDENCE_TRANSFER_PQ);
    for (const auto *sdr : {"bt709", "smpte170m", "bt470m", "bt470bg", "gamma22", "gamma28", "iec61966-2-1",
                            "bt2020-10", "bt2020-12"})
        if (transfer == sdr)
            return result(LR_MEDIA_SDR, LR_PROFILE_NONE, LR_EVIDENCE_TRANSFER_SDR);
    return result(LR_MEDIA_UNKNOWN, LR_PROFILE_NONE, LR_EVIDENCE_UNKNOWN_TRANSFER);
}
} // namespace

extern "C" lr_status lr_media_classify(const lr_classify_input *input, lr_classification *result,
                                       lr_error *error)
{
    if (input == nullptr || result == nullptr || input->struct_size != sizeof(*input) ||
        input->abi_version != LR_ABI_VERSION || result->struct_size != sizeof(*result) ||
        result->abi_version != LR_ABI_VERSION || (error != nullptr && error->struct_size != sizeof(*error)) ||
        input->flags > 3 || (input->tag_count != 0 && input->tags == nullptr))
        return report(error, LR_INVALID_ARGUMENT, "Invalid source classification arguments.");
    if (input->tag_count > 1024)
        return report(error, LR_RESOURCE_LIMIT, "Too many camera tags.");
    try
    {
        const auto value = classify(*input);
        *result = value;
        return report(error, LR_OK, "");
    }
    catch (const failure &value)
    {
        return report(error, value.status, value.message);
    }
    catch (const std::bad_alloc &)
    {
        return report(error, LR_RESOURCE_LIMIT, "Cannot allocate source classification state.");
    }
    catch (...)
    {
        return report(error, LR_INTERNAL_ERROR, "Source classification failed unexpectedly.");
    }
}

extern "C" int32_t luma_dji_parse_color(const uint8_t *, uint64_t, lr_dji_color *);
extern "C" int32_t luma_avc_is_monochrome(const uint8_t *, uint64_t, uint32_t *);

extern "C" lr_status lr_dji_parse_color(const uint8_t *packet, uint64_t size, lr_dji_color *result,
                                        lr_error *error)
{
    if ((packet == nullptr && size != 0) || result == nullptr || size > 1048576 ||
        result->struct_size != sizeof(*result) || result->abi_version != LR_ABI_VERSION ||
        (error != nullptr && error->struct_size != sizeof(*error)))
        return report(error, LR_INVALID_ARGUMENT, "Invalid DJI packet arguments.");
    const auto status = luma_dji_parse_color(packet, size, result);
    return report(error, status,
                  status == LR_OK          ? ""
                  : status == LR_NOT_FOUND ? "DJI schema is not recognized."
                                           : "Invalid DJI color packet.");
}
extern "C" lr_status lr_avc_is_monochrome(const uint8_t *extradata, uint64_t size, uint32_t *result,
                                          lr_error *error)
{
    if ((extradata == nullptr && size != 0) || result == nullptr || size > 1048576 ||
        (error != nullptr && error->struct_size != sizeof(*error)))
        return report(error, LR_INVALID_ARGUMENT, "Invalid AVC extradata arguments.");
    const auto status = luma_avc_is_monochrome(extradata, size, result);
    return report(error, status,
                  status == LR_OK          ? ""
                  : status == LR_NOT_FOUND ? "AVC SPS not found."
                                           : "Invalid AVC SPS.");
}
