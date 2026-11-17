#include "lumarecode/plan.h"
#include "media_model.hpp"
#include "plan_internal.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace
{
void require(bool condition, const char *message)
{
    if (!condition)
        throw luma::probe_failure(LR_INVALID_DATA, message);
}
using luma::json_value;
using luma::member;
json_value *first(luma::probe_json &json, const std::vector<json_value *> &side, const char *type,
                  bool contains = false)
{
    for (const auto entry : side)
    {
        const auto value = luma::field(json, entry, "side_data_type");
        if (contains ? value.find(type) != std::string::npos : value == type)
            return entry;
    }
    return nullptr;
}
bool equals(json_value *left, json_value *right)
{
    return left == nullptr || right == nullptr ? left == right : yyjson_mut_equals(left, right);
}
json_value *parse(luma::probe_json &json, lr_utf8_view input)
{
    if (input.size > 64 * 1024 * 1024)
        throw luma::probe_failure(LR_RESOURCE_LIMIT, "Verification JSON exceeds its byte budget.");
    auto value = json.parse({input.data, static_cast<size_t>(input.size)});
    require(yyjson_mut_is_obj(value), "Verification input must be a media object.");
    return value;
}
void verify(luma::probe_json &json, json_value *plan, json_value *source, json_value *output)
{
    const auto text = [&](auto object, const char *key) { return luma::field(json, object, key); };
    const auto integer = [&](auto object, const char *key) { return luma::int_field(json, object, key); };
    const auto options = member(plan, "options"), colors = member(plan, "colors");
    const auto before_video = member(source, "video"), video = member(output, "video");
    require(yyjson_mut_is_obj(before_video) && yyjson_mut_is_obj(video),
            "Verification requires source and output video metadata.");
    require(!luma::media_raw(json, source), "RAW output verification requires its development contract.");
    const auto sampling = text(options, "sampling"), codec = text(options, "codec"),
               output_kind = text(options, "output");
    const auto depth = integer(options, "bit_depth");
    auto pixel = sampling == "gray"
                     ? depth == 8 ? "gray" : "gray" + std::to_string(depth) + "le"
                     : "yuv" + sampling + "p" + (depth == 8 ? "" : std::to_string(depth) + "le");
    auto actual_pixel = text(video, "pix_fmt");
    if (actual_pixel.starts_with("yuvj"))
        actual_pixel.erase(3, 1);
    require(actual_pixel == pixel ||
                (sampling == "gray" && luma::flag_field(json, video, "_monochrome") &&
                 luma::source_format(json, output).bit_depth == static_cast<uint32_t>(depth)),
            "Output pixel format differs from the plan.");
    require(text(video, "codec_name") == (codec == "avc" ? "h264" : codec),
            "Output video codec differs from the plan.");
    const auto before_side = luma::side_data(source), after_side = luma::side_data(output);
    const auto before_audio = luma::audio_streams(json, source),
               after_audio = luma::audio_streams(json, output);
    const bool motion = luma::flag_field(json, plan, "motion_photo");
    if (motion)
    {
        const bool has_audio = !before_audio.empty();
        require(after_audio.size() == (has_audio ? 1U : 0U) &&
                    (!has_audio || (text(after_audio[0], "codec_name") == "aac" &&
                                    text(after_audio[0], "profile") == "LC" &&
                                    integer(after_audio[0], "sample_rate") == 48000 &&
                                    integer(after_audio[0], "channels") ==
                                        (integer(before_audio[0], "channels") == 1 ? 1 : 2))),
                "Motion Photo audio differs from the plan.");
    }
    auto width = integer(before_video, "width"), height = integer(before_video, "height");
    const auto matrix = first(json, before_side, "Display Matrix");
    const auto angle = std::fmod(std::abs(luma::probe_number(text(matrix, "rotation"))), 180.0);
    if (matrix != nullptr && std::abs(angle - 90) < 1)
        std::swap(width, height);
    if (motion)
    {
        width += width % 2;
        height += height % 2;
    }
    const auto actual_width = integer(video, "width"), actual_height = integer(video, "height");
    const auto motion_size = text(plan, "motion_size");
    const auto preview = integer(plan, "preview_size");
    require(width > 0 && height > 0 && actual_width > 0 && actual_height > 0,
            "Output video dimensions must be positive.");
    if (preview > 0 || (motion && motion_size != "source"))
    {
        int64_t bound_width = preview > 0       ? preview
                              : width >= height ? (motion_size == "1080" ? 1920 : 1280)
                                                : (motion_size == "1080" ? 1080 : 720);
        int64_t bound_height = preview > 0       ? preview
                               : width >= height ? (motion_size == "1080" ? 1080 : 720)
                                                 : (motion_size == "1080" ? 1920 : 1280);
        require(actual_width <= std::min(width, bound_width) &&
                    actual_height <= std::min(height, bound_height) &&
                    std::abs(static_cast<double>(actual_width) / actual_height -
                             static_cast<double>(width) / height) <= 2.0 / actual_height,
                "Scaled output dimensions or aspect ratio differ from the plan.");
    }
    else
        require(actual_width == width && actual_height == height,
                "Output dimensions or orientation differ from the plan.");
    size_t index, count;
    json_value *key, *value;
    yyjson_mut_obj_foreach(colors, index, count, key, value)
    {
        const auto name = json.text(key), expected = json.text(value);
        require(luma::unspecified(expected) || text(video, name.c_str()) == expected,
                "Output color tags differ from the plan.");
    }
    const auto before_dovi = first(json, before_side, "DOVI configuration record");
    const auto before_hdr = first(json, before_side, "HDR10+", true);
    const bool preserve = (before_dovi != nullptr || before_hdr != nullptr) &&
                          text(options, "dynamic_hdr") == "preserve" && output_kind == "hdr" &&
                          text(options, "transform") == "none";
    if (preserve)
    {
        if (before_dovi != nullptr)
        {
            const auto after = first(json, after_side, "DOVI configuration record");
            const auto profile = integer(before_dovi, "dv_profile");
            require(after != nullptr &&
                        integer(after, "dv_profile") == (codec == "av1"  ? 10
                                                         : profile == 10 ? 8
                                                                         : profile) &&
                        integer(after, "dv_bl_signal_compatibility_id") ==
                            integer(before_dovi, "dv_bl_signal_compatibility_id"),
                    "Output Dolby Vision configuration differs.");
            require(equals(first(json, before_side, "Dolby Vision Metadata"),
                           first(json, after_side, "Dolby Vision Metadata")),
                    "Output Dolby Vision first-frame metadata differs.");
        }
        if (before_hdr != nullptr)
            require(equals(before_hdr, first(json, after_side, "HDR10+", true)),
                    "Output HDR10+ first-frame metadata differs.");
    }
    else
        require(first(json, after_side, "DOVI configuration record") == nullptr &&
                    first(json, after_side, "Dolby Vision", true) == nullptr &&
                    first(json, after_side, "HDR10+", true) == nullptr,
                "Standard output retained dynamic HDR metadata.");
    if (text(options, "audio") == "flac" && !motion)
    {
        require(before_audio.size() == after_audio.size(), "FLAC output audio track count differs.");
        for (size_t i = 0; i < before_audio.size(); ++i)
        {
            const auto bits = luma::flac_depth(json, before_audio[i]);
            const auto before = before_audio[i], after = after_audio[i];
            require(text(after, "codec_name") == "flac" &&
                        text(before, "sample_rate") == text(after, "sample_rate") &&
                        integer(before, "channels") == integer(after, "channels") &&
                        integer(after, "bits_per_raw_sample") >= bits &&
                        (luma::unspecified(text(before, "channel_layout")) ||
                         text(before, "channel_layout") == text(after, "channel_layout")),
                    "FLAC output precision, sample rate or channel order differs.");
        }
    }
    if (output_kind == "sdr")
        for (const auto entry : after_side)
        {
            const auto type = text(entry, "side_data_type");
            require(type.find("HDR") == std::string::npos && type.find("Dolby") == std::string::npos &&
                        type != "Mastering display metadata" && type != "Content light level metadata",
                    "SDR output retained HDR metadata.");
        }
    if (output_kind == "hdr" && text(options, "transform") == "none")
        for (const auto before : before_side)
        {
            const auto type = text(before, "side_data_type");
            if (type != "Mastering display metadata" && type != "Content light level metadata")
                continue;
            const auto after = first(json, after_side, type.c_str());
            require(after != nullptr, "Output is missing source static HDR metadata.");
            yyjson_mut_obj_foreach(before, index, count, key, value)
            {
                const auto name = json.text(key);
                if (name == "side_data_type")
                    continue;
                const auto target = member(after, name.c_str());
                const auto left = luma::probe_number(json.text(value), NAN),
                           right = luma::probe_number(json.text(target), NAN);
                const auto tolerance = type == "Content light level metadata" ? 0.0
                                       : codec == "av1"              ? name == "max_luminance"   ? 1.0 / 256
                                                                       : name == "min_luminance" ? 1.0 / 16384
                                                                                                 : 1.0 / 65536
                                       : name.ends_with("luminance") ? 1.0 / 10000
                                                                     : 1.0 / 50000;
                require(target != nullptr && std::isfinite(left) && std::isfinite(right) &&
                            std::abs(left - right) <= tolerance + 1e-12,
                        "Static HDR metadata exceeds target quantization tolerance.");
            }
        }
}
lr_status diagnose(lr_error *error, lr_status status, const char *message)
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
} // namespace
extern "C" lr_status lr_plan_verify_output(const lr_plan *plan, lr_utf8_view source_json,
                                           lr_utf8_view output_json, lr_error *error)
{
    if (plan == nullptr || (error != nullptr && error->struct_size != sizeof(*error)) ||
        (source_json.size != 0 && source_json.data == nullptr) ||
        (output_json.size != 0 && output_json.data == nullptr))
        return LR_INVALID_ARGUMENT;
    try
    {
        luma::probe_json json;
        auto planning =
            parse(json, {reinterpret_cast<const uint8_t *>(plan->json.data()), plan->json.size()});
        auto source = parse(json, source_json), output = parse(json, output_json);
        verify(json, planning, source, output);
        return diagnose(error, LR_OK, "");
    }
    catch (const luma::probe_failure &failure)
    {
        return diagnose(error, failure.status, failure.what());
    }
    catch (const std::bad_alloc &)
    {
        return diagnose(error, LR_RESOURCE_LIMIT, "Verification memory budget exhausted.");
    }
    catch (...)
    {
        return diagnose(error, LR_INTERNAL_ERROR, "Output verification failed unexpectedly.");
    }
}
