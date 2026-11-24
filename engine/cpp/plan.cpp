#include "lumarecode/plan.h"
#include "media_model.hpp"
#include "plan_internal.hpp"
#include "raw_model.hpp"

#include <algorithm>
#include <array>
#include <charconv>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <map>
#include <optional>

namespace
{
using luma::json_value;
using luma::member;
void require(bool valid, const char *message)
{
    if (!valid)
        throw luma::probe_failure(LR_UNSUPPORTED, message);
}
std::string join(const std::vector<std::string> &values, const char *separator)
{
    std::string result;
    for (const auto &value : values)
    {
        if (!result.empty())
            result += separator;
        result += value;
    }
    return result;
}
std::string number(double value, int precision = 17, bool trim = true)
{
    require(std::isfinite(value), "Numeric plan value must be finite.");
    std::array<char, 512> storage{};
    // The existing planner's custom decimal format starts with 15 significant
    // digits and permits at most 17 fractional places. Expand those digits
    // directly: converting back through double would reintroduce binary tails
    // into FFmpeg's seek/trim expressions (for example 0.67500000000000004).
    const auto written =
        std::to_chars(storage.data(), storage.data() + storage.size(), value,
                      trim ? std::chars_format::general : std::chars_format::fixed, trim ? 15 : precision);
    require(written.ec == std::errc{}, "Numeric plan value exceeds its text budget.");
    std::string text(storage.data(), written.ptr);
    if (trim)
    {
        const bool negative = text.starts_with('-');
        if (negative)
            text.erase(0, 1);
        const auto exponent_at = text.find_first_of("eE");
        int exponent = 0;
        if (exponent_at != std::string::npos)
        {
            auto exponent_text = std::string_view(text).substr(exponent_at + 1);
            if (exponent_text.starts_with('+'))
                exponent_text.remove_prefix(1);
            std::from_chars(exponent_text.data(), exponent_text.data() + exponent_text.size(), exponent);
            text.resize(exponent_at);
        }
        const auto dot = text.find('.');
        int decimal = static_cast<int>(dot == std::string::npos ? text.size() : dot) + exponent;
        if (dot != std::string::npos)
            text.erase(dot, 1);
        if (decimal <= 0)
            text = "0." + std::string(static_cast<size_t>(-decimal), '0') + text;
        else if (static_cast<size_t>(decimal) >= text.size())
            text.append(static_cast<size_t>(decimal) - text.size(), '0');
        else
            text.insert(static_cast<size_t>(decimal), 1, '.');
        const auto decimal_at = text.find('.');
        if (decimal_at != std::string::npos && text.size() > decimal_at + 1 + static_cast<size_t>(precision))
        {
            const auto keep = decimal_at + 1 + static_cast<size_t>(precision);
            const bool round_up = text[keep] >= '5';
            text.resize(keep);
            if (round_up)
            {
                bool carry = true;
                for (size_t i = text.size(); i > 0 && carry; --i)
                {
                    if (text[i - 1] == '.')
                        continue;
                    if (text[i - 1] == '9')
                        text[i - 1] = '0';
                    else
                    {
                        ++text[i - 1];
                        carry = false;
                    }
                }
                if (carry)
                    text.insert(0, 1, '1');
            }
        }
        if (negative)
            text.insert(0, 1, '-');
    }
    if (trim && text.find('.') != std::string::npos)
    {
        while (text.back() == '0')
            text.pop_back();
        if (text.back() == '.')
            text.pop_back();
    }
    return text;
}
double round_even(double value)
{
    const auto lower = std::floor(value), fraction = value - lower;
    return fraction < .5 ? lower : fraction > .5 ? lower + 1 : std::fmod(lower, 2) == 0 ? lower : lower + 1;
}
bool hdr(const std::string &kind)
{
    return kind == "hlg" || kind == "pq";
}
struct profile
{
    const char *file;
    const char *matrix;
    const char *range;
};
const std::map<std::string, profile> profiles{
    {"pocket3", {"DJI_Osmo_DLogM_to_Rec2100_HLG_BT2408_Neutral.cube", "709", "limited"}},
    {"samsung", {"Samsung_Log_to_Rec2100_HLG.cube", "2020_ncl", "limited"}},
    {"milog", {"REDMI_K90_MiLog_to_Rec2100_HLG.cube", "2020_ncl", "full"}},
    {"applelog", {"Apple_Log_to_Rec2100_HLG.cube", "2020_ncl", "limited"}},
    {"applelog2", {"Apple_Log_2_to_Rec2100_HLG.cube", "2020_ncl", "limited"}}};
constexpr const char *color_keys[] = {"color_primaries", "color_transfer", "color_space", "color_range"};
constexpr const char *color_flags[] = {"-color_primaries", "-color_trc", "-colorspace", "-color_range"};
std::string scale_filter(uint32_t size)
{
    const auto n = std::to_string(size);
    return "scale=w='min(iw," + n + ")':h='min(ih," + n +
           ")':force_original_aspect_ratio=decrease:force_divisible_by=2";
}
std::string motion_scale(const std::string &size)
{
    const std::string width = size == "1080" ? "1920" : "1280", height = size == "1080" ? "1080" : "720";
    return "scale=w='min(iw,if(gte(iw,ih)," + width + "," + height + "))':h='min(ih,if(gte(iw,ih)," + height +
           "," + width + "))':force_original_aspect_ratio=decrease:force_divisible_by=2";
}

class planner
{
    luma::probe_json json;
    json_value *media, *choices, *presets, *motion_presets, *video, *pixel;
    std::vector<json_value *> side, audio;
    json_value *dovi = nullptr, *hardware_preset = nullptr;
    bool hdr10plus = false, preserve = false, generate_dovi = false;
    std::string purpose, codec, output, transform, selected_profile, sampling, range, variant, audio_choice,
        kind;
    uint32_t depth = 0;
    lr_source_format source{};
    const lr_plan_options &request;

    std::string text(json_value *object, const char *key, const char *fallback = "")
    {
        return luma::field(json, object, key, fallback);
    }
    int64_t integer(json_value *object, const char *key, int64_t fallback = 0)
    {
        return luma::int_field(json, object, key, fallback);
    }
    void put(json_value *object, const char *key, const std::string &value)
    {
        json.put(object, key, json.string(value));
    }
    json_value *parse(lr_utf8_view input, uint64_t maximum)
    {
        if ((input.size != 0 && input.data == nullptr) || input.size > maximum)
            throw luma::probe_failure(input.size > maximum ? LR_RESOURCE_LIMIT : LR_INVALID_ARGUMENT,
                                      "Plan JSON exceeds its input budget.");
        auto value = json.parse({input.data, static_cast<size_t>(input.size)});
        require(yyjson_mut_is_obj(value), "Plan JSON inputs must be objects.");
        return value;
    }
    std::string path(lr_utf8_view input)
    {
        require(input.size != 0 && input.size <= 500000 &&
                    lr_utf8_validate(input.data, input.size) == LR_OK &&
                    std::memchr(input.data, 0, static_cast<size_t>(input.size)) == nullptr,
                "Plan paths require bounded UTF-8 without NUL.");
        const std::string value(reinterpret_cast<const char *>(input.data), static_cast<size_t>(input.size));
        require(std::filesystem::path(std::u8string(value.begin(), value.end())).is_absolute(),
                "Plan paths must be absolute.");
        return value;
    }
    json_value *color(bool hlg)
    {
        auto value = json.object();
        put(value, "color_primaries", hlg ? "bt2020" : "bt709");
        put(value, "color_transfer", hlg ? "arib-std-b67" : "bt709");
        put(value, "color_space", hlg ? "bt2020nc" : "bt709");
        put(value, "color_range", "tv");
        return value;
    }
    std::string frame_tags(json_value *colors)
    {
        std::vector<std::string> parts;
        constexpr const char *targets[] = {"color_primaries", "color_trc", "colorspace", "range"};
        for (size_t i = 0; i < 4; ++i)
        {
            auto value = text(colors, color_keys[i]);
            if (luma::unspecified(value))
                continue;
            if (i == 3)
                value = value == "pc" ? "full" : value == "tv" ? "limited" : value;
            parts.push_back(std::string(targets[i]) + "=" + value);
        }
        return "setparams=" + join(parts, ":");
    }
    std::string pixel_format() const
    {
        return sampling == "gray" ? depth == 8 ? "gray" : "gray" + std::to_string(depth) + "le"
                                  : "yuv" + sampling + "p" + (depth == 8 ? "" : std::to_string(depth) + "le");
    }
    std::string resolved_audio()
    {
        return audio_choice != "auto" ? audio_choice
               : purpose == "archive"
                   ? text(member(presets, "audio"), "archive_default")
                   : text(member(member(presets, "audio"), "share_default"), codec.c_str());
    }
    void validate()
    {
        purpose = text(choices, "purpose", "share");
        codec = text(choices, "codec", "hevc");
        output = text(choices, "output", "sdr");
        transform = text(choices, "transform", "none");
        selected_profile = text(choices, "profile");
        variant = text(choices, "variant", "standard");
        audio_choice = text(choices, "audio", "auto");
        const auto source_kind = text(choices, "source_kind", "auto"),
                   container = text(choices, "container", "auto");
        require(purpose == "share" || purpose == "archive", "Unknown encoding purpose.");
        require(codec == "hevc" || codec == "avc" || codec == "av1", "Unknown video codec.");
        require(output == "sdr" || output == "hdr", "Unknown output dynamic range.");
        require(container == "auto" || container == "mp4" || container == "mkv", "Unknown output container.");
        require(audio_choice == "auto" || audio_choice == "copy" || audio_choice == "aac" ||
                    audio_choice == "opus" || audio_choice == "flac",
                "Unknown audio choice.");
        require(variant == "standard" || (variant == "fast" && purpose == "archive" && codec == "av1"),
                "Fast variant requires archive AV1.");
        kind = source_kind == "auto" ? text(media, "kind") : source_kind;
        require(kind == "sdr" || kind == "hlg" || kind == "pq" || kind == "log" || kind == "raw" ||
                    kind == "unknown",
                "Unknown input interpretation.");
        require((kind == "raw") == luma::media_raw(json, media),
                "ProRes RAW requires its calibrated source interpretation.");
        if (kind == "raw")
        {
            const auto raw = member(video, "_raw");
            require(yyjson_mut_is_obj(raw) && luma::flag_field(json, raw, "decoder_version_supported"),
                    "ProRes RAW requires FFmpeg 9 or later.");
            require(luma::flag_field(json, raw, "decoder_supported"),
                    "ProRes RAW is not supported by the selected decoder.");
            luma::raw_orientation(json, media);
        }
        const auto selected = profiles.find(selected_profile);
        source = luma::source_format(
            json, media,
            transform.starts_with("log-") && selected != profiles.end()
                ? std::string_view(selected->second.range) == "full" ? LR_RANGE_FULL : LR_RANGE_LIMITED
                : LR_RANGE_AUTO);
        lr_encode_options options;
        lr_encode_options_init(&options, sizeof(options));
        options.purpose = purpose == "share" ? LR_PURPOSE_SHARE : LR_PURPOSE_ARCHIVE;
        options.codec = codec == "hevc" ? LR_CODEC_HEVC : codec == "avc" ? LR_CODEC_AVC : LR_CODEC_AV1;
        options.dynamic_range = output == "sdr" ? LR_DYNAMIC_RANGE_SDR : LR_DYNAMIC_RANGE_HDR;
        const auto requested_sampling = text(choices, "sampling", "auto"),
                   requested_range = text(choices, "color_range", "auto"),
                   requested_depth = text(choices, "bit_depth", "auto");
        const std::map<std::string, uint32_t> samplings{
            {"auto", LR_SAMPLING_AUTO}, {"source", LR_SAMPLING_SOURCE}, {"420", LR_SAMPLING_420},
            {"422", LR_SAMPLING_422},   {"444", LR_SAMPLING_444},       {"gray", LR_SAMPLING_GRAY}};
        require(samplings.contains(requested_sampling), "Unknown output sampling.");
        options.sampling = samplings.at(requested_sampling);
        require(requested_range == "auto" || requested_range == "source" || requested_range == "limited" ||
                    requested_range == "full",
                "Unknown sample range.");
        options.color_range = requested_range == "source"    ? source.color_range
                              : requested_range == "limited" ? LR_RANGE_LIMITED
                              : requested_range == "full"    ? LR_RANGE_FULL
                                                             : LR_RANGE_AUTO;
        require(requested_depth == "auto" || requested_depth == "8" || requested_depth == "10" ||
                    requested_depth == "12",
                "Unknown bit depth.");
        options.bit_depth =
            requested_depth == "auto" ? 0U : static_cast<uint32_t>(std::stoul(requested_depth));
        lr_resolved_format format;
        lr_resolved_format_init(&format, sizeof(format));
        lr_error error;
        lr_error_init(&error, sizeof(error));
        if (lr_resolve_format(&source, &options, &format, &error) != LR_OK)
            throw luma::probe_failure(LR_UNSUPPORTED, error.message);
        sampling = luma::sampling_name(format.sampling);
        depth = format.bit_depth;
        range = format.color_range == LR_RANGE_FULL ? "full" : "limited";
        const auto hardware = text(choices, "hardware", "qsv");
        if (purpose == "share" && hardware != "qsv")
        {
            require((request.flags & LR_PLAN_MOTION_PHOTO) == 0, "Motion Photo hardware encoding uses QSV.");
            hardware_preset = member(member(member(member(presets, "hardware"), hardware.c_str()), "codecs"),
                                     codec.c_str());
            require(yyjson_mut_is_arr(
                        member(member(hardware_preset, "arguments"), std::to_string(depth).c_str())),
                    "Selected hardware encoder does not support this codec or bit depth.");
        }
        if (audio_choice == "flac")
        {
            require(container != "mp4" && !audio.empty(), "FLAC requires audio and an MKV container.");
            for (const auto track : audio)
                luma::flac_depth(json, track);
        }
        const auto dynamic = text(choices, "dynamic_hdr", "preserve");
        require(dynamic == "preserve" || dynamic == "standard" || dynamic == "generate_84",
                "Unknown dynamic HDR policy.");
        generate_dovi = dynamic == "generate_84";
        if (generate_dovi)
        {
            require(hardware_preset != nullptr && hardware == "videotoolbox" &&
                        text(hardware_preset, "encoder") == "hevc_videotoolbox" && codec == "hevc" &&
                        text(media, "kind") == "hlg" && kind == "hlg" && dovi == nullptr && !hdr10plus &&
                        text(video, "color_transfer") == "arib-std-b67" && source.bit_depth >= 10 &&
                        output == "hdr" && transform == "none" && depth == 10 && sampling == "420" &&
                        range == "limited" && container == "mp4" && request.preview_size == 0,
                    "Dolby Vision 8.4 generation requires standard 10-bit HLG, VideoToolbox HEVC and MP4.");
        }
        if (dovi != nullptr || hdr10plus)
        {
            require(source_kind == "auto" || source_kind == text(media, "kind"),
                    "Dynamic HDR determines its input interpretation.");
            if (dovi != nullptr)
            {
                const auto profile = integer(dovi, "dv_profile"),
                           compatibility = integer(dovi, "dv_bl_signal_compatibility_id");
                require(integer(dovi, "rpu_present_flag") == 1 && integer(dovi, "bl_present_flag") == 1 &&
                            integer(dovi, "el_present_flag") == 0 &&
                            (profile == 5 ? compatibility == 0
                                          : (profile == 8 || profile == 10) &&
                                                (compatibility == 1 || compatibility == 4)),
                        "Unsupported Dolby Vision layer/profile.");
            }
            preserve = dynamic == "preserve" && output == "hdr" && transform == "none";
            if (preserve)
            {
                require(purpose == "archive" && codec != "avc",
                        "Dynamic HDR preservation requires archive HEVC/AV1.");
                require(!hdr10plus || codec == "hevc", "HDR10+ preservation requires HEVC.");
                require(dovi == nullptr || integer(dovi, "dv_profile") != 5 || codec == "hevc",
                        "Dolby Vision profile 5 preservation requires HEVC.");
                require(sampling == "420" && depth == 10 && format.color_range == source.color_range,
                        "Dynamic HDR preservation requires source range, 10 bit and 4:2:0.");
                for (const auto entry : side)
                    require(text(entry, "side_data_type") != "Display Matrix",
                            "Dynamic HDR orientation must be rendered before conversion.");
            }
        }
        if (transform == "log-hdr" || transform == "log-sdr")
        {
            require(selected != profiles.end() && kind == "log" && source.bit_depth >= 10 &&
                        transform == (output == "hdr" ? "log-hdr" : "log-sdr"),
                    "Log transform requires a matching profile, output and high-precision Log input.");
        }
        else if (transform == "hdr-sdr")
            require(output == "sdr" && hdr(kind), "HDR-to-SDR requires HLG/PQ input and SDR output.");
        else if (transform == "none")
            require(kind != "log" && kind != "unknown" && !(output == "hdr" && kind == "sdr") &&
                        !(output == "sdr" && hdr(kind)),
                    "Input interpretation and output require a color transform.");
        else
            require(false, "Unknown color transform.");
        // Match existing validation order: validate requested audio first, then
        // resolve selection/motion-specific defaults for the actual command.
        if ((request.flags & LR_PLAN_MOTION_PHOTO) != 0)
        {
            require(codec == "hevc" || codec == "avc", "Motion Photo video requires HEVC or AVC.");
            audio_choice = "aac";
        }
        if (request.window != nullptr && !audio.empty())
        {
            if (audio_choice == "auto" && resolved_audio() == "copy")
            {
                bool eligible = container != "mp4";
                try
                {
                    for (const auto track : audio)
                        luma::flac_depth(json, track);
                }
                catch (const luma::probe_failure &)
                {
                    eligible = false;
                }
                audio_choice = eligible ? "flac" : "aac";
            }
            require(resolved_audio() != "copy", "Frame-accurate trimming requires audio re-encoding.");
        }
        put(choices, "purpose", purpose);
        put(choices, "codec", codec);
        put(choices, "output", output);
        put(choices, "transform", transform);
        put(choices, "sampling", sampling);
        put(choices, "color_range", range);
        put(choices, "bit_depth", std::to_string(depth));
        put(choices, "variant", variant);
        put(choices, "audio", audio_choice);
        put(choices, "source_kind", source_kind);
        put(choices, "container", container);
        put(choices, "dynamic_hdr", dynamic);
        if (member(choices, "profile") == nullptr)
            json.put(choices, "profile", yyjson_mut_null(json.doc()));
        json.put(choices, "cpu_decode",
                 yyjson_mut_bool(json.doc(), luma::flag_field(json, choices, "cpu_decode")));
    }
    std::string log_filter()
    {
        const auto &profile = profiles.at(selected_profile);
        const auto tagged_matrix = text(video, "color_space"), tagged_range = text(video, "color_range");
        const auto matrix = tagged_matrix == "gbr"         ? "gbr"
                            : tagged_matrix == "bt709"     ? "709"
                            : tagged_matrix == "bt2020nc"  ? "2020_ncl"
                            : tagged_matrix == "smpte170m" ? "170m"
                            : tagged_matrix == "bt470bg"   ? "470bg"
                                                           : profile.matrix;
        const auto source_range = tagged_range == "pc"   ? "full"
                                  : tagged_range == "tv" ? "limited"
                                                         : profile.range;
        return "zscale=matrixin=" + std::string(matrix) + ":rangein=" + source_range +
               ":range=full,format=gbrpf32le,lut3d=file=" + profile.file +
               ":interp=tetrahedral,setparams=range=full:color_primaries=bt2020:color_trc=arib-std-b67:"
               "colorspace=gbr,zscale=matrix=2020_ncl:rangein=full:range=limited:dither=error_diffusion,"
               "format=yuv444p16le";
    }
    std::vector<std::string> hdr_metadata()
    {
        json_value *mastering = nullptr, *content = nullptr;
        for (const auto entry : side)
        {
            if (text(entry, "side_data_type") == "Mastering display metadata" && mastering == nullptr)
                mastering = entry;
            if (text(entry, "side_data_type") == "Content light level metadata" && content == nullptr)
                content = entry;
        }
        constexpr const char *fields[] = {"green_x",       "green_y",      "blue_x",        "blue_y",
                                          "red_x",         "red_y",        "white_point_x", "white_point_y",
                                          "max_luminance", "min_luminance"};
        std::vector<std::string> metadata, values;
        for (size_t i = 0; i < 10; ++i)
        {
            if (member(mastering, fields[i]) == nullptr)
                break;
            const auto value = luma::probe_number(text(mastering, fields[i]));
            values.push_back(codec == "hevc" ? number(round_even(value * (i < 8 ? 50000 : 10000)))
                                             : number(value, 8, false));
        }
        if (values.size() == 10)
            metadata.push_back(std::string(codec == "hevc" ? "master-display" : "mastering-display") + "=G(" +
                               values[0] + "," + values[1] + ")B(" + values[2] + "," + values[3] + ")R(" +
                               values[4] + "," + values[5] + ")WP(" + values[6] + "," + values[7] + ")L(" +
                               values[8] + "," + values[9] + ")");
        if (member(content, "max_content") != nullptr && member(content, "max_average") != nullptr)
            metadata.push_back(std::string(codec == "hevc" ? "max-cll" : "content-light") + "=" +
                               text(content, "max_content") + "," + text(content, "max_average"));
        return metadata;
    }

  public:
    explicit planner(const lr_plan_options &request) : request(request)
    {
        media = parse(request.media_json, 64 * 1024 * 1024);
        choices = parse(request.encode_json, 65536);
        presets = parse(request.presets_json, 1024 * 1024);
        motion_presets = request.motion_presets_json.size == 0
                             ? json.object()
                             : parse(request.motion_presets_json, 1024 * 1024);
        video = member(media, "video");
        pixel = member(media, "pixel");
        require(yyjson_mut_is_obj(video), "Media snapshot requires video metadata.");
        side = luma::side_data(media);
        audio = luma::audio_streams(json, media);
        for (const auto entry : side)
        {
            const auto type = text(entry, "side_data_type");
            if (type == "DOVI configuration record" && dovi == nullptr)
                dovi = entry;
            hdr10plus = hdr10plus || type.find("HDR10+") != std::string::npos;
        }
    }
    std::string build()
    {
        validate();
        const auto ffmpeg = path(request.ffmpeg), destination = path(request.destination),
                   assets = path(request.assets);
        const auto source_path = text(media, "path");
        const lr_utf8_view source_view{reinterpret_cast<const uint8_t *>(source_path.data()),
                                       source_path.size()};
        path(source_view);
        const auto window = request.window;
        const bool motion = (request.flags & LR_PLAN_MOTION_PHOTO) != 0;
        const std::string motion_size =
            request.motion_size.size == 0
                ? "source"
                : std::string(reinterpret_cast<const char *>(request.motion_size.data),
                              static_cast<size_t>(request.motion_size.size));
        require(!motion || motion_size == "source" || motion_size == "1080" || motion_size == "720",
                "Unknown Motion Photo size.");
        put(video, "color_range", source.color_range == LR_RANGE_FULL ? "pc" : "tv");
        if (hdr(kind) || kind == "sdr")
        {
            if (hdr(kind))
                put(video, "color_transfer", kind == "hlg" ? "arib-std-b67" : "smpte2084");
            const auto defaults = color(hdr(kind));
            for (const auto key : color_keys)
                if (luma::unspecified(text(video, key)))
                    json.put(video, key, json.copy(member(defaults, key)));
        }
        if (preserve && dovi != nullptr && integer(dovi, "dv_profile") == 5)
            put(video, "color_space", "ipt-c2");
        const bool rgb = luma::media_rgb(json, media), render_dolby = dovi != nullptr && !preserve;
        if (rgb)
            put(video, "color_space", "gbr");
        auto colors = output == "sdr"                                   ? color(false)
                      : kind == "log" || kind == "raw" || generate_dovi ? color(true)
                                                                        : json.object();
        if (yyjson_mut_obj_size(colors) == 0)
            for (const auto key : color_keys)
                json.put(colors, key,
                         member(video, key) == nullptr ? yyjson_mut_null(json.doc())
                                                       : json.copy(member(video, key)));
        if (text(colors, "color_space") == "gbr")
            put(colors, "color_space", output == "hdr" ? "bt2020nc" : "bt709");
        put(colors, "color_range", range == "full" ? "pc" : "tv");
        auto config = hardware_preset != nullptr ? hardware_preset
                      : motion            ? member(member(motion_presets, purpose.c_str()), codec.c_str())
                      : variant == "fast" ? member(presets, "archive_fast_av1")
                                          : member(member(presets, purpose.c_str()), codec.c_str());
        require(yyjson_mut_is_obj(config), "Selected encoder preset is missing.");
        bool conversion = false, display_transform = false;
        for (const auto key : color_keys)
            conversion = conversion || text(video, key) != text(colors, key);
        for (const auto entry : side)
            display_transform = display_transform || text(entry, "side_data_type") == "Display Matrix";
        const bool qsv = purpose == "share" && hardware_preset == nullptr;
        const bool must_decode_in_software =
            transform != "none" || luma::flag_field(json, choices, "cpu_decode") || conversion ||
            display_transform || dovi != nullptr || hdr10plus || window != nullptr || motion ||
            kind == "raw";
        const auto average = luma::probe_number(text(video, "avg_frame_rate")),
                   nominal = luma::probe_number(text(video, "r_frame_rate"));
        const bool timing =
            qsv &&
            ((request.flags & LR_PLAN_QSV_TIMING) != 0 ||
             (average > 0 && nominal > 0 && std::abs(average - nominal) > std::max(.01, nominal * .001)));
        const auto source_codec = text(video, "codec_name"), source_pixel = text(video, "pix_fmt");
        const auto decoder = source_codec == "h264"   ? "h264_qsv"
                             : source_codec == "hevc" ? "hevc_qsv"
                             : source_codec == "av1"  ? "av1_qsv"
                                                      : "";
        const bool eight_bit = source_pixel == "yuv420p" || source_pixel == "nv12";
        const bool ten_bit = source_pixel == "yuv420p10le" || source_pixel == "p010le";
        const bool hardware_input = !must_decode_in_software && decoder[0] != 0 && (eight_bit || ten_bit);
        const bool gpu = qsv && hardware_input && (!timing || depth == source.bit_depth);
        // CUDA and D3D11VA produce device frames. Download to the corresponding
        // source layout before the shared colour/range path.
        const auto encoder = text(config, "encoder");
        const std::string_view hwaccel = hardware_input && purpose == "share"
            ? encoder.ends_with("_nvenc") ? "cuda"
              : encoder.ends_with("_amf") || encoder.ends_with("_mf") ? "d3d11va" : ""
            : "";
        std::vector<std::string> args{ffmpeg,      "-hide_banner", "-nostdin", "-n",        "-xerror",
                                      "-loglevel", "warning",      "-nostats", "-progress", "pipe:1"};
        const auto add = [&](std::initializer_list<std::string> values) { args.insert(args.end(), values); };
        if (gpu)
            add({"-hwaccel", "qsv", "-hwaccel_output_format", "qsv", "-c:v", decoder});
        if (!hwaccel.empty())
            add({"-hwaccel", std::string(hwaccel), "-hwaccel_output_format", hwaccel == "cuda" ? "cuda" : "d3d11"});
        if (kind == "raw")
        {
            add({"-threads:v", "2", "-filter_threads", "2"});
#if defined(_M_X64) || defined(__x86_64__)
            add({"-cpuflags", "-avx2"});
#endif
            add({"-apply_cropping", "0", "-noautorotate"});
        }
        if (window != nullptr && window->seek_frame > 0)
            add({"-seek_timestamp", "1", "-ss", number(window->seek_seconds)});
        add({"-i", source_path, "-map", "0:" + text(video, "index"), "-map", motion ? "0:a:0?" : "0:a?",
             "-map_metadata", motion || transform.starts_with("log-") ? "-1" : "0", "-map_chapters",
             motion || window != nullptr ? "-1" : "0", "-sn", "-dn"});
        std::vector<std::string> filters;
        if (!hwaccel.empty())
            filters.push_back(eight_bit ? "hwdownload,format=nv12" : "hwdownload,format=p010le");
        if (window != nullptr)
            filters.push_back("trim=start_frame=" + std::to_string(window->start - window->seek_frame) +
                              ":end_frame=" + std::to_string(window->end - window->seek_frame));
        if (request.preview_size > 0 && kind != "raw")
            filters.push_back(scale_filter(request.preview_size));
        if (motion && kind != "raw")
            filters.emplace_back("pad=ceil(iw/2)*2:ceil(ih/2)*2");
        if (kind == "raw")
        {
            filters.push_back(luma::raw_filter(json, member(video, "_raw"), request.preview_size));
            const auto orientation = luma::raw_orientation(json, media);
            filters.insert(filters.end(), orientation.begin(), orientation.end());
            if (sampling == "420" || sampling == "422")
                filters.push_back("pad=ceil(iw/2)*2:" +
                                  std::string(sampling == "420" ? "ceil(ih/2)*2" : "ih"));
        }
        else if (preserve)
            filters.emplace_back("null");
        else if (render_dolby)
        {
            if (output == "hdr")
                filters.push_back(
                    "libplacebo=format=yuv444p16le:colorspace=bt2020nc:color_primaries=bt2020:color_trc=" +
                    std::string(kind == "hlg" ? "arib-std-b67" : "smpte2084") +
                    ":range=" + (range == "full" ? "pc" : "tv") + ":apply_dolbyvision=1");
        }
        else if (kind == "log")
        {
            if (rgb)
                filters.emplace_back("format=gbrpf32le");
            filters.push_back(log_filter());
        }
        else if (!gpu)
        {
            if (rgb)
                filters.emplace_back(luma::flag_field(json, member(pixel, "flags"), "float")
                                         ? "format=gbrpf32le"
                                         : "format=gbrp16le");
            filters.push_back(frame_tags(video));
        }
        if (transform == "hdr-sdr" || transform == "log-sdr" || (kind == "raw" && output == "sdr"))
        {
            if (luma::flag_field(json, choices, "cpu_tone_map") && !render_dolby && kind != "raw")
                filters.push_back(
                    "zscale=transfer=linear:npl=100,format=gbrpf32le,tonemap=tonemap=mobius:desat=0,"
                    "zscale=primaries=bt709:transfer=bt709:matrix=bt709:range=" +
                    range + ",format=yuv444p16le");
            else
                filters.push_back(
                    "libplacebo=format=yuv444p16le:colorspace=bt709:color_primaries=bt709:color_"
                    "trc=bt709:range=" +
                    std::string(range == "full" ? "pc" : "tv") +
                    ":tonemapping=bt.2390:apply_dolbyvision=" + (render_dolby ? "1" : "0"));
            filters.emplace_back(
                "sidedata=delete:type=MASTERING_DISPLAY_METADATA,sidedata=delete:type=CONTENT_"
                "LIGHT_LEVEL,sidedata=delete:type=DYNAMIC_HDR_PLUS");
        }
        if ((dovi != nullptr || hdr10plus) && !preserve)
            filters.emplace_back("sidedata=delete:type=DOVI_RPU_BUFFER,sidedata=delete:type=DOVI_METADATA,"
                                 "sidedata=delete:type=DYNAMIC_HDR_PLUS");
        if (gpu)
        {
            if (timing)
                filters.push_back(frame_tags(colors));
            else
                filters.push_back(
                    "vpp_qsv=format=" + std::string(depth == 10 ? "p010le" : "nv12") +
                    ":out_range=" + text(colors, "color_range") +
                    (output == "sdr"
                         ? ":out_color_matrix=bt709:out_color_primaries=bt709:out_color_transfer=bt709"
                         : ""));
        }
        else if (!preserve)
        {
            std::vector<std::string> conversion_parts{"range=" + range, "dither=error_diffusion"};
            constexpr const char *targets[] = {"primaries", "transfer", "matrix"};
            for (size_t i = 0; i < 3; ++i)
                if (!luma::unspecified(text(colors, color_keys[i])))
                    conversion_parts.push_back(std::string(targets[i]) + "=" + text(colors, color_keys[i]));
            filters.push_back("zscale=" + join(conversion_parts, ":"));
            const auto encoder_pixel = purpose == "share" && depth == 10 ? "p010le"
                                       : hardware_preset != nullptr      ? "nv12"
                                                                         : pixel_format();
            filters.push_back("format=" + encoder_pixel);
            filters.push_back(frame_tags(colors));
        }
        if (motion && motion_size != "source")
            filters.push_back(motion_scale(motion_size));
        if (window != nullptr)
            filters.emplace_back("setpts=PTS-STARTPTS");
        add({"-vf", join(filters, ","), "-c:v", text(config, "encoder")});
        if (hardware_preset == nullptr)
            add({"-preset", text(config, "preset")});
        const auto fps = average > 0 ? average : nominal > 0 ? nominal : 30;
        if (hardware_preset != nullptr)
        {
            const auto arguments = member(member(config, "arguments"), std::to_string(depth).c_str());
            const auto length = yyjson_mut_arr_size(arguments);
            require(length > 0 && length % 2 == 0, "Invalid hardware encoder arguments.");
            size_t index, count;
            json_value *value;
            yyjson_mut_arr_foreach(arguments, index, count, value) args.push_back(json.text(value));
            add({"-g", number(std::clamp(round_even(2 * fps), 1.0, 240.0))});
            if (codec == "hevc" && text(config, "encoder") != "hevc_mf")
                add({"-profile:v", depth == 10 ? "main10" : "main"});
        }
        else if (qsv)
        {
            add({"-global_quality:v", text(config, "quality"), "-low_power",
                 (request.flags & LR_PLAN_AUTO_LOW_POWER) != 0 ? "-1" : "1", "-async_depth",
                 text(config, "async_depth", "4"), "-g",
                 number(std::clamp(round_even(2 * fps), 1.0, 240.0))});
            if (timing && (codec == "avc" || codec == "hevc"))
                add({"-bf:v", "0"});
            if (codec == "hevc")
                add({"-profile:v", depth == 10 ? "main10" : "main"});
        }
        else
        {
            add({"-crf", text(config, "quality"), "-pix_fmt", pixel_format(), "-g",
                 number(std::max(1.0, round_even((motion ? 2 : 8) * fps)))});
            auto extra = json.object();
            if (codec == "hevc")
            {
                put(extra, "pools", "16");
                put(extra, "log-level", "warning");
            }
            else if (codec == "av1")
            {
                put(extra, "lp", text(config, "lp", "0"));
                put(extra, "tune", text(config, "tune", "0"));
            }
            size_t index, count;
            json_value *key, *value;
            yyjson_mut_obj_foreach(member(config, "encoder_parameters"), index, count, key, value)
                json.put(extra, luma::string_value(key), json.copy(value));
            if (preserve && dovi != nullptr && codec == "hevc")
            {
                put(extra, "vbv-maxrate", "100000");
                put(extra, "vbv-bufsize", "100000");
            }
            if (preserve && hdr10plus)
                put(extra, "dhdr10-info", "hdr10plus.json");
            auto metadata = output == "hdr" && kind == "pq" && (codec == "hevc" || codec == "av1")
                                ? hdr_metadata()
                                : std::vector<std::string>{};
            if (codec == "hevc" && !metadata.empty())
            {
                put(extra, "hdr10", "1");
                put(extra, "hdr10-opt", "1");
                put(extra, "repeat-headers", "1");
            }
            if (yyjson_mut_obj_size(extra) > 0 || codec == "hevc" || codec == "av1")
            {
                std::vector<std::string> parameters;
                yyjson_mut_obj_foreach(extra, index, count, key, value)
                    parameters.push_back(json.text(key) + "=" + json.text(value));
                parameters.insert(parameters.end(), metadata.begin(), metadata.end());
                add({codec == "hevc"  ? "-x265-params"
                     : codec == "av1" ? "-svtav1-params"
                                      : "-x264-params",
                     join(parameters, ":")});
            }
        }
        if (dovi != nullptr && purpose == "archive" && (codec == "hevc" || codec == "av1"))
            add({"-dolbyvision", preserve ? "1" : "0"});
        // VideoToolbox can insert Dolby Vision metadata when encoding HLG.
        // Profile 8.4 is remuxed separately once demuxing exposes the coded
        // yuv420p10le format to the Dolby Vision bitstream filter. Record the
        // nominal rate in the HEVC headers so that filter can select the Dolby
        // level from the pixel rate. Preserve the actual container timestamps.
        if (generate_dovi)
        {
            const auto rate = nominal > 0 && nominal <= 240 ? nominal : fps;
            auto headers = "hevc_metadata=tick_rate=" + number(rate, 6) + ":num_ticks_poc_diff_one=0";
            const auto samples = double(integer(video, "width")) * integer(video, "height");
            const auto samples_per_second = samples * rate;
            if (samples <= 8912896 && samples_per_second > 267386880 && samples_per_second <= 534773760)
            {
                // Main-tier HEVC 5.1 covers 4K60. VideoToolbox's automatic
                // level can advertise 5.0 when the input starts in VFR mode.
                headers += ":level=5.1";
                add({"-maxrate:v", "40M"});
            }
            add({"-bsf:v", headers});
        }
        if (hardware_preset != nullptr && text(config, "encoder") == "hevc_videotoolbox" &&
            text(colors, "color_transfer") == "arib-std-b67" && !generate_dovi)
            add({"-bsf:v", "dovi_rpu=strip=1"});
        if (preserve)
            add({"-strict", "unofficial"});
        for (size_t i = 0; i < 4; ++i)
            if (!luma::unspecified(text(colors, color_keys[i])) &&
                !(preserve && dovi != nullptr && integer(dovi, "dv_profile") == 5))
                add({color_flags[i], text(colors, color_keys[i])});
        add({"-fps_mode:v", "passthrough", "-enc_time_base:v", "filter"});
        if (kind == "raw")
            add({"-metadata:s:v:0", "rotate=0"});
        std::vector<std::string> audio_filters;
        if (window != nullptr && !audio.empty())
        {
            const auto origin = window->seek_frame > 0
                                    ? window->seek_seconds
                                    : luma::probe_number(text(video, "_format_start_time"));
            audio_filters.push_back("asetpts=PTS+round(" + number(origin) + "*SR)/SR/TB");
            audio_filters.push_back("atrim=start=" + number(window->start_seconds) +
                                    ":end=" + number(window->end_seconds));
            audio_filters.push_back("asetpts=PTS-(" + number(window->start_seconds) + ")/TB");
        }
        if (motion && !audio.empty())
            audio_filters.push_back("aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=" +
                                    std::string(integer(audio.front(), "channels") == 1 ? "mono" : "stereo"));
        if (!audio_filters.empty())
            add({"-af", join(audio_filters, ",")});
        const auto audio_mode = resolved_audio();
        if (motion && !audio.empty())
        {
            const auto policy = member(motion_presets, "audio");
            const bool mono = integer(audio.front(), "channels") == 1;
            add({"-c:a", text(policy, "encoder"), "-profile:a", text(policy, "profile"), "-b:a",
                 text(policy, mono ? "mono_bitrate_kbps" : "stereo_bitrate_kbps") + "k", "-ar",
                 text(policy, "sample_rate"), "-ac", mono ? "1" : "2"});
        }
        else if (audio_mode == "copy")
            add({"-c:a", "copy"});
        else if (audio_mode == "flac")
        {
            for (size_t i = 0; i < audio.size(); ++i)
            {
                const auto bits = luma::flac_depth(json, audio[i]);
                const auto suffix = ":a:" + std::to_string(i);
                add({"-c" + suffix, "flac", "-compression_level" + suffix, "8", "-sample_fmt" + suffix,
                     bits <= 16 ? "s16" : "s32", "-bits_per_raw_sample" + suffix, std::to_string(bits)});
                if (bits > 24)
                    add({"-strict" + suffix, "experimental"});
            }
        }
        else
        {
            const auto policy = member(member(presets, "audio"), audio_mode.c_str());
            require(yyjson_mut_is_obj(policy), "Selected audio preset is missing.");
            add({"-c:a", text(policy, "encoder"), "-b:a", text(policy, "bitrate_kbps") + "k", "-ac",
                 text(policy, "channels")});
            if (audio_mode == "opus")
                add({"-ar", text(policy, "sample_rate"), "-vbr", text(policy, "vbr"), "-application:a",
                     text(policy, "application"), "-compression_level:a", text(policy, "compression_level")});
        }
        auto extension =
            std::filesystem::path(std::u8string(destination.begin(), destination.end())).extension().string();
        std::transform(extension.begin(), extension.end(), extension.begin(),
                       [](unsigned char c) { return static_cast<char>(c >= 'A' && c <= 'Z' ? c + 32 : c); });
        if (extension == ".mp4")
        {
            if (codec == "hevc")
                add({"-tag:v", "hvc1"});
            add({"-movflags", generate_dovi ? "+write_colr" : "+faststart+write_colr"});
        }
        if (request.seconds > 0)
            add({"-t", number(request.seconds)});
        args.push_back(destination);
        auto result = json.object(), arguments = json.array(), sidecars = json.array();
        for (const auto &argument : args)
            json.append(arguments, json.string(argument));
        if (preserve && hdr10plus)
            json.append(sidecars, json.string("hdr10plus.json"));
        if (kind == "raw")
            json.append(sidecars, json.string("raw-calibration.cmd"));
        json.put(result, "schema_version", yyjson_mut_uint(json.doc(), 1));
        json.put(result, "arguments", arguments);
        json.put(result, "colors", colors);
        json.put(result, "options", json.copy(choices));
        json.put(result, "qsv_timing_compatibility", yyjson_mut_bool(json.doc(), timing));
        json.put(result, "uses_software_decoding", yyjson_mut_bool(json.doc(), !gpu && hwaccel.empty()));
        json.put(result, "working_directory",
                 (preserve && hdr10plus) || kind == "raw" ? json.string(luma_path_parent(destination))
                                                          : yyjson_mut_null(json.doc()));
        put(result, "asset_directory", assets);
        json.put(result, "sidecars", sidecars);
        json.put(result, "motion_photo", yyjson_mut_bool(json.doc(), motion));
        put(result, "motion_size", motion_size);
        json.put(result, "preview_size", yyjson_mut_uint(json.doc(), request.preview_size));
        return json.write(result);
    }
    static std::string luma_path_parent(const std::string &value)
    {
        const auto parent =
            std::filesystem::path(std::u8string(value.begin(), value.end())).parent_path().u8string();
        return {parent.begin(), parent.end()};
    }
};
lr_status report(lr_error *error, lr_status status, const char *message)
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
extern "C" lr_status lr_plan_create(const lr_plan_options *options, lr_plan **result, lr_error *error)
{
    if (options == nullptr || result == nullptr || options->struct_size != sizeof(*options) ||
        options->abi_version != LR_ABI_VERSION ||
        (error != nullptr && error->struct_size != sizeof(*error)) || (options->flags & ~7U) != 0 ||
        !std::isfinite(options->seconds) || options->seconds < 0 || options->preview_size > INT32_MAX ||
        options->motion_size.size > 16 ||
        (options->motion_size.size != 0 && options->motion_size.data == nullptr))
        return report(error, LR_INVALID_ARGUMENT, "Invalid plan options.");
    if (const auto w = options->window;
        w != nullptr &&
        (w->struct_size != sizeof(*w) || w->abi_version != LR_ABI_VERSION || w->start >= w->end ||
         w->seek_frame > w->start || !std::isfinite(w->seek_seconds) || !std::isfinite(w->start_seconds) ||
         !std::isfinite(w->end_seconds) || w->end_seconds <= w->start_seconds))
        return report(error, LR_INVALID_ARGUMENT, "Invalid selected frame window.");
    try
    {
        auto created = std::make_unique<lr_plan>();
        created->json = planner(*options).build();
        *result = created.release();
        return report(error, LR_OK, "");
    }
    catch (const luma::probe_failure &failure)
    {
        return report(error, failure.status, failure.what());
    }
    catch (const std::bad_alloc &)
    {
        return report(error, LR_RESOURCE_LIMIT, "Plan memory budget exhausted.");
    }
    catch (...)
    {
        return report(error, LR_INTERNAL_ERROR, "Plan construction failed unexpectedly.");
    }
}
extern "C" lr_status lr_plan_copy_json(const lr_plan *plan, uint8_t *destination, uint64_t capacity,
                                       uint64_t *required)
{
    if (plan == nullptr || required == nullptr || (capacity != 0 && destination == nullptr))
        return LR_INVALID_ARGUMENT;
    *required = plan->json.size();
    if (capacity < plan->json.size())
        return LR_BUFFER_TOO_SMALL;
    std::memcpy(destination, plan->json.data(), plan->json.size());
    return LR_OK;
}
extern "C" void lr_plan_release(lr_plan *plan)
{
    delete plan;
}
