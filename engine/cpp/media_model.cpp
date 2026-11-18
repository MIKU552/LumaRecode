#include "media_model.hpp"
#include "lumarecode/media.h"

#include <algorithm>
#include <charconv>
#include <cstring>
#include <limits>

namespace luma
{
std::string field(probe_json &json, json_value *object, const char *key, const char *fallback)
{
    const auto value = member(object, key);
    return value == nullptr || yyjson_mut_is_null(value) ? std::string(fallback) : json.text(value);
}
int64_t int_field(probe_json &json, json_value *object, const char *key, int64_t fallback)
{
    const auto storage = field(json, object, key);
    std::string_view text(storage);
    const auto white = [](char byte) { return byte == ' ' || (byte >= '\t' && byte <= '\r'); };
    while (!text.empty() && white(text.front()))
        text.remove_prefix(1);
    while (!text.empty() && white(text.back()))
        text.remove_suffix(1);
    if (!text.empty() && text.front() == '+')
    {
        text.remove_prefix(1);
        if (!text.empty() && (text.front() == '+' || text.front() == '-'))
            return fallback;
    }
    int32_t value = 0;
    const auto parsed = std::from_chars(text.data(), text.data() + text.size(), value);
    return parsed.ec == std::errc{} && parsed.ptr == text.data() + text.size() ? value : fallback;
}
bool flag_field(probe_json &json, json_value *object, const char *key)
{
    const auto text = field(json, object, key);
    return text == "true" || text == "1";
}
std::vector<json_value *> objects(json_value *array)
{
    std::vector<json_value *> result;
    size_t index, count;
    json_value *value;
    yyjson_mut_arr_foreach(array, index, count, value) if (yyjson_mut_is_obj(value)) result.push_back(value);
    return result;
}
std::vector<json_value *> side_data(json_value *media)
{
    auto result = objects(member(member(media, "video"), "side_data_list"));
    for (const auto frame : objects(member(media, "frames")))
    {
        const auto frame_side = objects(member(frame, "side_data_list"));
        result.insert(result.end(), frame_side.begin(), frame_side.end());
    }
    return result;
}
std::vector<json_value *> audio_streams(probe_json &json, json_value *media)
{
    auto streams = objects(member(media, "streams"));
    std::erase_if(streams, [&](auto stream) { return field(json, stream, "codec_type") != "audio"; });
    return streams;
}
bool media_rgb(probe_json &json, json_value *media)
{
    const auto flags = member(member(media, "pixel"), "flags");
    const auto name = field(json, member(media, "video"), "pix_fmt");
    if (flag_field(json, flags, "rgb") || flag_field(json, flags, "palette") || name == "pal8")
        return true;
    for (const auto prefix : {"rgb", "bgr", "gbr", "argb", "abgr", "rgba", "bgra"})
        if (name.starts_with(prefix))
            return true;
    return false;
}
bool media_raw(probe_json &json, json_value *media)
{
    return yyjson_mut_is_obj(member(member(media, "video"), "_raw")) || field(json, media, "kind") == "raw";
}
std::string sampling_name(lr_sampling sampling)
{
    constexpr const char *names[] = {"unknown", "420", "422", "444", "gray",
                                     "444",     "440", "411", "410", "source"};
    return sampling <= LR_SAMPLING_SOURCE ? names[sampling] : "unknown";
}
lr_source_format source_format(probe_json &json, json_value *media, lr_color_range profile_range)
{
    lr_source_format result;
    lr_source_format_init(&result, sizeof(result));
    auto video = member(media, "video"), pixel = member(media, "pixel"), flags = member(pixel, "flags");
    if (!yyjson_mut_is_obj(media) || !yyjson_mut_is_obj(video))
        throw probe_failure(LR_INVALID_DATA, "Media snapshot requires a video object.");
    const auto name = field(json, video, "pix_fmt");
    const bool rgb = media_rgb(json, media), raw = media_raw(json, media),
               alpha = flag_field(json, flags, "alpha");
    result.flags = (alpha ? LR_SOURCE_ALPHA : 0) | (raw ? LR_SOURCE_RAW : 0) | (rgb ? LR_SOURCE_RGB : 0);
    if (raw)
        result.sampling = LR_SAMPLING_444;
    else if (flag_field(json, video, "_monochrome"))
        result.sampling = LR_SAMPLING_GRAY;
    else if (flag_field(json, flags, "bayer"))
        result.sampling = LR_SAMPLING_AUTO;
    else if (rgb)
        result.sampling = LR_SAMPLING_RGB;
    else if (int_field(json, pixel, "nb_components") == 1 || int_field(json, pixel, "nb_components") == 2 ||
             name.starts_with("gray") || name.starts_with("ya"))
        result.sampling = LR_SAMPLING_GRAY;
    else if (member(pixel, "log2_chroma_w") != nullptr && member(pixel, "log2_chroma_h") != nullptr)
    {
        const auto w = int_field(json, pixel, "log2_chroma_w"), h = int_field(json, pixel, "log2_chroma_h");
        if (w == 0 && h == 0)
            result.sampling = LR_SAMPLING_444;
        else if (w == 1 && h == 0)
            result.sampling = LR_SAMPLING_422;
        else if (w == 1 && h == 1)
            result.sampling = LR_SAMPLING_420;
        else if (w == 0 && h == 1)
            result.sampling = LR_SAMPLING_440;
        else if (w == 2 && h == 0)
            result.sampling = LR_SAMPLING_411;
        else if (w == 2 && h == 2)
            result.sampling = LR_SAMPLING_410;
    }
    else
    {
        size_t first = std::string::npos;
        for (const auto sampling : {LR_SAMPLING_420, LR_SAMPLING_422, LR_SAMPLING_444, LR_SAMPLING_440,
                                    LR_SAMPLING_411, LR_SAMPLING_410})
        {
            const auto at = name.find(sampling_name(sampling));
            if (at < first)
            {
                first = at;
                result.sampling = sampling;
            }
        }
        if (first == std::string::npos &&
            (name == "nv12" || name == "nv21" || name == "p010le" || name == "p012le" || name == "p016le"))
            result.sampling = LR_SAMPLING_420;
    }
    int64_t raw_depth = int_field(json, video, "bits_per_raw_sample");
    if (raw_depth <= 0)
        raw_depth = int_field(json, video, "bits_per_sample");
    auto components = objects(member(pixel, "components"));
    if (alpha && components.size() > 1)
        components.pop_back();
    int64_t depth = 0;
    for (const auto component : components)
        depth = std::max(depth, int_field(json, component, "bit_depth"));
    if (depth > 0 && raw_depth > 0)
        depth = std::min(depth, raw_depth);
    else if (depth <= 0)
        depth = raw_depth;
    if (depth <= 0)
    {
        size_t first = std::string::npos;
        for (const auto bits : {10, 12, 14, 16})
            for (const auto prefix : {"p", "gray"})
            {
                const auto at = name.find(std::string(prefix) + std::to_string(bits));
                if (at < first)
                {
                    first = at;
                    depth = bits;
                }
            }
    }
    result.bit_depth = raw ? 16U : depth <= 0 ? 8U : static_cast<uint32_t>(depth);
    const auto range = field(json, video, "color_range");
    result.color_range = range == "pc" || range == "full" || range == "jpeg"      ? LR_RANGE_FULL
                         : range == "tv" || range == "limited" || range == "mpeg" ? LR_RANGE_LIMITED
                         : rgb || name.starts_with("yuvj")                        ? LR_RANGE_FULL
                         : profile_range != LR_RANGE_AUTO                         ? profile_range
                         : field(json, media, "profile") == "milog"               ? LR_RANGE_FULL
                                                                                  : LR_RANGE_LIMITED;
    const auto kind = field(json, media, "kind");
    result.dynamic_range = kind == "hlg" || kind == "pq" || kind == "log" || kind == "raw"
                               ? LR_DYNAMIC_RANGE_HDR
                               : LR_DYNAMIC_RANGE_SDR;
    return result;
}
uint32_t flac_depth(probe_json &json, json_value *stream)
{
    const auto codec = field(json, stream, "codec_name"), sample = field(json, stream, "sample_fmt");
    bool lossless = codec == "dts" && field(json, stream, "profile") == "DTS-HD MA";
    for (const auto name : {"flac", "alac", "ape", "tta", "tak", "shorten", "truehd", "mlp", "wmalossless"})
        lossless = lossless || codec == name;
    int32_t pcm_depth = 0;
    if (codec.starts_with("pcm_s") || codec.starts_with("pcm_u"))
    {
        std::string_view suffix(codec.data() + 5, codec.size() - 5);
        const auto parsed = std::from_chars(suffix.data(), suffix.data() + suffix.size(), pcm_depth);
        if (!suffix.empty() && suffix.front() >= '0' && suffix.front() <= '9' && parsed.ec == std::errc{} &&
            parsed.ptr != suffix.data())
        {
            suffix.remove_prefix(static_cast<size_t>(parsed.ptr - suffix.data()));
            if (suffix.starts_with("le") || suffix.starts_with("be"))
                suffix.remove_prefix(2);
            if (suffix == "_planar")
                suffix = {};
            lossless = lossless || suffix.empty();
        }
    }
    if (!lossless || sample.starts_with("flt") || sample.starts_with("dbl"))
        throw probe_failure(LR_UNSUPPORTED, "FLAC requires lossless integer audio.");
    auto depth = int_field(json, stream, "bits_per_raw_sample");
    if (depth <= 0)
        depth = int_field(json, stream, "bits_per_sample");
    if (depth <= 0)
        depth = pcm_depth;
    const auto channels = int_field(json, stream, "channels");
    if (depth < 1 || depth > 32 || channels < 1 || channels > 8)
        throw probe_failure(LR_UNSUPPORTED, "FLAC requires known 1-32 bit precision and 1-8 channels.");
    const auto layout = field(json, stream, "channel_layout");
    constexpr const char *layouts[] = {"", "mono", "stereo", "3.0", "quad", "5.0", "5.1", "6.1", "7.1"};
    if ((!unspecified(layout) && layout != layouts[channels]) || (channels > 2 && unspecified(layout)))
        throw probe_failure(LR_UNSUPPORTED, "Audio channel order cannot be preserved in FLAC.");
    return static_cast<uint32_t>(depth);
}
} // namespace luma

namespace
{
lr_status diagnose(lr_error *error, lr_status code, const char *message)
{
    if (error != nullptr && error->struct_size == sizeof(*error))
    {
        error->code = code;
        const auto count = std::min(std::strlen(message), sizeof(error->message) - 1);
        std::memcpy(error->message, message, count);
        error->message[count] = 0;
    }
    return code;
}
template <class Function> lr_status read_model(lr_utf8_view input, lr_error *error, Function &&function)
{
    if ((error != nullptr && error->struct_size != sizeof(*error)) ||
        (input.size != 0 && input.data == nullptr))
        return LR_INVALID_ARGUMENT;
    if (input.size > 64 * 1024 * 1024)
        return diagnose(error, LR_RESOURCE_LIMIT, "Media JSON exceeds its byte budget.");
    try
    {
        luma::probe_json json;
        function(json, json.parse({input.data, static_cast<size_t>(input.size)}));
        return diagnose(error, LR_OK, "");
    }
    catch (const luma::probe_failure &failure)
    {
        return diagnose(error, failure.status, failure.what());
    }
    catch (const std::bad_alloc &)
    {
        return diagnose(error, LR_RESOURCE_LIMIT, "Media model memory budget exhausted.");
    }
    catch (...)
    {
        return diagnose(error, LR_INTERNAL_ERROR, "Media model failed unexpectedly.");
    }
}
} // namespace
extern "C" lr_status lr_media_source_format(lr_utf8_view input, lr_color_range profile_range,
                                            lr_source_format *result, lr_error *error)
{
    if (result == nullptr || result->struct_size != sizeof(*result) ||
        result->abi_version != LR_ABI_VERSION || profile_range > LR_RANGE_FULL)
        return LR_INVALID_ARGUMENT;
    return read_model(input, error,
                      [&](luma::probe_json &json, auto media)
                      {
                          const auto format = luma::source_format(json, media, profile_range);
                          *result = format;
                      });
}
extern "C" lr_status lr_media_flac_depth(lr_utf8_view input, uint32_t *depth, lr_error *error)
{
    if (depth == nullptr)
        return LR_INVALID_ARGUMENT;
    return read_model(input, error,
                      [&](luma::probe_json &json, auto stream)
                      {
                          if (!yyjson_mut_is_obj(stream))
                              throw luma::probe_failure(LR_INVALID_DATA, "Expected an audio stream object.");
                          const auto value = luma::flac_depth(json, stream);
                          *depth = value;
                      });
}
