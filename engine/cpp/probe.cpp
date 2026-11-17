#include "lumarecode/probe.h"
#include "output_internal.hpp"
#include "probe_json.hpp"

#include <algorithm>
#include <atomic>
#include <charconv>
#include <chrono>
#include <cstring>
#include <filesystem>
#include <map>
#include <mutex>
#include <type_traits>
#include <vector>

namespace
{
using clock_type = std::chrono::steady_clock;
using luma::member;
using luma::probe_failure;
lr_status report(lr_error *error, lr_status status, const char *message) noexcept
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
template <class Function> lr_status boundary(lr_error *error, Function &&function) noexcept
{
    if (error != nullptr && error->struct_size != sizeof(*error))
        return LR_INVALID_ARGUMENT;
    try
    {
        if constexpr (std::is_void_v<std::invoke_result_t<Function>>)
        {
            function();
            return report(error, LR_OK, "");
        }
        else
            return function();
    }
    catch (const probe_failure &failure)
    {
        return report(error, failure.status, failure.what());
    }
    catch (const luma::file_failure &failure)
    {
        return report(error, failure.status, failure.what());
    }
    catch (const std::bad_alloc &)
    {
        return report(error, LR_RESOURCE_LIMIT, "Probe memory budget exhausted.");
    }
    catch (const std::filesystem::filesystem_error &)
    {
        return report(error, LR_IO_ERROR, "Cannot inspect source or tool file.");
    }
    catch (...)
    {
        return report(error, LR_INTERNAL_ERROR, "Source inspection failed unexpectedly.");
    }
}
std::string copy_path(lr_utf8_view view)
{
    if (view.size == 0 || view.size > 1048576 || lr_utf8_validate(view.data, view.size) != LR_OK ||
        std::memchr(view.data, 0, static_cast<size_t>(view.size)) != nullptr)
        throw probe_failure(LR_INVALID_ARGUMENT, "Probe paths require bounded UTF-8 without NUL.");
    const std::string result(reinterpret_cast<const char *>(view.data), static_cast<size_t>(view.size));
    const std::filesystem::path path(std::u8string(result.begin(), result.end()));
    if (!path.is_absolute())
        throw probe_failure(LR_INVALID_ARGUMENT, "Probe paths must be absolute.");
    return result;
}
lr_utf8_view view(const std::string &text)
{
    return {reinterpret_cast<const uint8_t *>(text.data()), text.size()};
}
std::filesystem::path path(const std::string &text)
{
    return std::filesystem::path(std::u8string(text.begin(), text.end()));
}
struct stamp
{
    std::filesystem::path canonical;
    uintmax_t size;
    std::filesystem::file_time_type modified;
    bool operator==(const stamp &) const = default;
};
stamp file_stamp(const std::string &name)
{
    const auto file = path(name);
    if (!std::filesystem::exists(file))
        throw probe_failure(LR_NOT_FOUND, "Source or tool file does not exist.");
    if (!std::filesystem::is_regular_file(file))
        throw probe_failure(LR_INVALID_ARGUMENT, "Source and tool must be regular files.");
    return {std::filesystem::canonical(file), std::filesystem::file_size(file),
            std::filesystem::last_write_time(file)};
}
std::string stamp_key(const stamp &value)
{
    const auto bytes = value.canonical.u8string();
    return std::string(bytes.begin(), bytes.end()) + "\n" + std::to_string(value.size) + "\n" +
           std::to_string(value.modified.time_since_epoch().count());
}
std::mutex pixel_mutex;
std::map<std::string, std::vector<uint8_t>> pixel_cache;
size_t pixel_cache_bytes = 0;

int32_t integer(luma::probe_json &json, yyjson_mut_val *value, int32_t fallback = 0)
{
    const auto text = json.text(value);
    std::string_view input(text);
    const auto whitespace = [](char byte) { return byte == ' ' || (byte >= '\t' && byte <= '\r'); };
    while (!input.empty() && whitespace(input.front()))
        input.remove_prefix(1);
    while (!input.empty() && whitespace(input.back()))
        input.remove_suffix(1);
    if (!input.empty() && input.front() == '+')
    {
        input.remove_prefix(1);
        if (!input.empty() && (input.front() == '+' || input.front() == '-'))
            return fallback;
    }
    if (input.empty())
        return fallback;
    int32_t result = 0;
    const auto parsed = std::from_chars(input.data(), input.data() + input.size(), result);
    return parsed.ec == std::errc{} && parsed.ptr == input.data() + input.size() ? result : fallback;
}
std::vector<uint8_t> hex_bytes(const std::string &dump)
{
    uint64_t size = 0;
    const auto status = lr_probe_decode_hex(view(dump), nullptr, 0, &size);
    if (status != LR_OK && status != LR_BUFFER_TOO_SMALL)
        throw probe_failure(status, "Invalid ffprobe hexadecimal dump.");
    std::vector<uint8_t> bytes(static_cast<size_t>(size));
    const auto decoded = lr_probe_decode_hex(view(dump), bytes.data(), bytes.size(), &size);
    if (decoded != LR_OK)
        throw probe_failure(decoded, "Cannot decode ffprobe hexadecimal dump.");
    return bytes;
}
const char *kind_name(uint32_t kind)
{
    constexpr const char *names[] = {"unknown", "sdr", "hlg", "pq", "log", "raw"};
    return kind <= LR_MEDIA_RAW ? names[kind] : "unknown";
}
const char *profile_name(uint32_t profile)
{
    constexpr const char *names[] = {"", "pocket3", "samsung", "milog", "applelog", "applelog2"};
    return profile <= LR_PROFILE_APPLE_LOG2 ? names[profile] : "";
}
const char *schema_name(uint32_t schema)
{
    return schema == LR_DJI_POCKET3 ? "dvtm_PP-101.proto"
           : schema == LR_DJI_AC206 ? "dvtm_ac206.proto"
                                    : "dvtm_oq101.proto";
}
} // namespace

struct lr_probe
{
    std::string executable, source, result_json;
    uint64_t timeout = 25000, capacity = 8 * 1024 * 1024;
    std::atomic<bool> cancelled{false};
    std::mutex mutex;
    lr_process *active = nullptr;
    lr_probe_snapshot snapshot{};

    void cancellation() const
    {
        if (cancelled.load(std::memory_order_relaxed))
            throw probe_failure(LR_CANCELLED, "Source inspection cancelled.");
    }
    void phase(uint32_t value)
    {
        cancellation();
        std::lock_guard lock(mutex);
        snapshot.phase = value;
    }
    bool cleaned()
    {
        std::lock_guard lock(mutex);
        return snapshot.cleanup_state == LR_CLEANUP_CONFIRMED;
    }
    std::vector<uint8_t> invoke(const std::vector<std::string> &arguments)
    {
        cancellation();
        lr_process_options options;
        lr_process_options_init(&options, sizeof(options));
        options.executable = view(executable);
        options.stdout_capacity = capacity;
        options.timeout_ms = timeout;
        std::vector<lr_utf8_view> views;
        views.reserve(arguments.size());
        for (const auto &argument : arguments)
            views.push_back(view(argument));
        options.arguments = views.data();
        options.argument_count = static_cast<uint32_t>(views.size());
        lr_error error;
        lr_error_init(&error, sizeof(error));
        lr_process *created = nullptr;
        auto status = lr_process_create(&options, &created, &error);
        if (status != LR_OK)
            throw probe_failure(status, error.message);
        std::unique_ptr<lr_process, decltype(&lr_process_release)> owned(created, lr_process_release);
        {
            std::lock_guard lock(mutex);
            active = created;
            snapshot.cleanup_state = LR_CLEANUP_PENDING;
            if (cancelled.load(std::memory_order_relaxed))
                lr_process_cancel(active);
        }
        status = lr_process_run(created, &error);
        lr_process_snapshot process;
        lr_process_snapshot_init(&process, sizeof(process));
        const auto inspected = lr_process_get_snapshot(created, &process);
        {
            std::lock_guard lock(mutex);
            active = nullptr;
            snapshot.cleanup_state = inspected == LR_OK ? process.cleanup_state : LR_CLEANUP_FAILED;
        }
        if (inspected != LR_OK || process.cleanup_state != LR_CLEANUP_CONFIRMED)
            throw probe_failure(LR_IO_ERROR, "Probe subprocess cleanup is unconfirmed.");
        if (status != LR_OK)
        {
            std::vector<uint8_t> tail(static_cast<size_t>(process.stderr_retained));
            uint64_t copied = 0;
            lr_process_copy_output(created, LR_STREAM_STDERR, tail.data(), tail.size(), &copied);
            std::string diagnostic(error.message);
            if (!tail.empty())
                diagnostic += " " + std::string(tail.begin(), tail.end());
            throw probe_failure(status, diagnostic);
        }
        std::vector<uint8_t> bytes(static_cast<size_t>(process.stdout_retained));
        uint64_t copied = 0;
        if (lr_process_copy_output(created, LR_STREAM_STDOUT, bytes.data(), bytes.size(), &copied) != LR_OK ||
            copied != bytes.size())
            throw probe_failure(LR_IO_ERROR, "Cannot copy probe subprocess output.");
        cancellation();
        return bytes;
    }
    yyjson_mut_val *read(luma::probe_json &json, const std::vector<std::string> &arguments)
    {
        const auto bytes = invoke(arguments);
        auto *root = json.parse(bytes);
        if (!yyjson_mut_is_obj(root))
            throw probe_failure(LR_INVALID_DATA, "Expected a JSON object from ffprobe.");
        return root;
    }
    yyjson_mut_val *pixel_formats(luma::probe_json &json)
    {
        phase(LR_PROBE_PHASE_PIXEL_FORMATS);
        const auto tool = file_stamp(executable);
        const auto key = stamp_key(tool);
        std::vector<uint8_t> bytes;
        {
            std::lock_guard lock(pixel_mutex);
            if (const auto found = pixel_cache.find(key); found != pixel_cache.end())
                bytes = found->second;
        }
        if (bytes.empty())
        {
            bytes = invoke({"-v", "error", "-show_pixel_formats", "-of", "json"});
            auto *root = json.parse(bytes);
            if (!yyjson_mut_is_obj(root))
                throw probe_failure(LR_INVALID_DATA, "Invalid pixel-format report.");
            if (bytes.size() <= 2 * 1024 * 1024 && key.size() <= 16384 && tool == file_stamp(executable))
            {
                std::lock_guard lock(pixel_mutex);
                if (pixel_cache.size() >= 8 || pixel_cache_bytes + bytes.size() > 16 * 1024 * 1024)
                {
                    pixel_cache.clear();
                    pixel_cache_bytes = 0;
                }
                const auto [position, added] = pixel_cache.try_emplace(key, bytes);
                if (added)
                    pixel_cache_bytes += position->second.size();
            }
            return root;
        }
        cancellation();
        if (bytes.size() > capacity)
            throw probe_failure(LR_RESOURCE_LIMIT, "Cached pixel report exceeds the metadata budget.");
        return json.parse(bytes);
    }

    void execute()
    {
        cancellation();
        const auto original = luma::open_source_lease(path(source));
        luma::probe_json json;
        phase(LR_PROBE_PHASE_STREAMS);
        auto *root = read(json, {"-v", "error", "-show_streams", "-show_format", "-show_data", "-of", "json",
                                 "-i", source});
        auto *streams = member(root, "streams");
        auto *format = member(root, "format");
        yyjson_mut_val *video = nullptr, *metadata = nullptr;
        uint32_t audio = 0;
        yyjson_mut_arr_iter iterator = yyjson_mut_arr_iter_with(streams);
        while (auto *stream = yyjson_mut_arr_iter_next(&iterator))
        {
            if (!yyjson_mut_is_obj(stream))
                continue;
            const auto type = json.text(member(stream, "codec_type"));
            if (type == "audio")
                ++audio;
            const auto attached = json.text(member(member(stream, "disposition"), "attached_pic"));
            if (type == "video" && attached != "1" && attached != "true" &&
                (video == nullptr || (json.text(member(stream, "codec_name")) == "prores_raw" &&
                                      json.text(member(video, "codec_name")) != "prores_raw")))
                video = stream;
            if (metadata == nullptr && json.text(member(stream, "codec_tag_string")) == "djmd")
                metadata = stream;
        }
        if (video == nullptr)
            throw probe_failure(LR_INVALID_DATA, "No playable video stream.");
        const auto video_index = integer(json, member(video, "index"), -1);
        const auto dji_index = metadata == nullptr ? -1 : integer(json, member(metadata, "index"), -1);
        if (video_index < 0)
            throw probe_failure(LR_INVALID_DATA, "Selected video has an invalid stream index.");
        {
            std::lock_guard lock(mutex);
            snapshot.video_index = video_index;
            snapshot.dji_index = dji_index;
            snapshot.audio_count = audio;
        }
        const auto codec = json.text(member(video, "codec_name"));
        bool mono = false;
        if (codec == "h264")
        {
            try
            {
                const auto extra = hex_bytes(json.text(member(video, "extradata")));
                uint32_t value = 0;
                mono =
                    lr_avc_is_monochrome(extra.data(), extra.size(), &value, nullptr) == LR_OK && value != 0;
            }
            catch (const probe_failure &)
            {
            }
        }
        json.put(video, "_monochrome", yyjson_mut_bool(json.doc(), mono));
        auto start = member(format, "start_time");
        json.put(video, "_format_start_time",
                 json.string(start == nullptr || yyjson_mut_is_null(start) ? "0" : json.text(start)));
        const bool raw = codec == "prores_raw";
        auto *frames = json.array();
        if (raw)
            json.put(video, "color_range", json.string("pc"));
        else
        {
            phase(LR_PROBE_PHASE_FRAMES);
            auto *first =
                read(json, {"-v", "error", "-select_streams", std::to_string(video_index), "-read_intervals",
                            "%+#1", "-show_frames", "-of", "json", "-i", source});
            if (auto *list = member(first, "frames"); yyjson_mut_is_arr(list))
                frames = list;
            for (const auto *name : {"color_transfer", "color_primaries", "color_space", "color_range"})
            {
                if (!luma::unspecified(json.text(member(video, name))))
                    continue;
                yyjson_mut_arr_iter frame_iterator = yyjson_mut_arr_iter_with(frames);
                while (auto *frame = yyjson_mut_arr_iter_next(&frame_iterator))
                {
                    const auto value = json.text(member(frame, name));
                    if (!luma::unspecified(value))
                    {
                        json.put(video, name, json.string(value));
                        break;
                    }
                }
            }
        }
        auto *tags = json.object();
        for (auto *source_tags : {member(format, "tags"), member(video, "tags")})
        {
            yyjson_mut_obj_iter tag_iterator = yyjson_mut_obj_iter_with(source_tags);
            while (auto *key = yyjson_mut_obj_iter_next(&tag_iterator))
                json.put(tags, luma::string_value(key), json.copy(yyjson_mut_obj_iter_get_val(key)));
        }
        lr_dji_color dji;
        lr_dji_color_init(&dji, sizeof(dji));
        lr_status dji_status = LR_NOT_FOUND;
        if (dji_index >= 0)
        {
            phase(LR_PROBE_PHASE_DJI);
            try
            {
                auto *packets = read(json, {"-v", "error", "-select_streams", std::to_string(dji_index),
                                            "-read_intervals", "%+#1", "-show_packets", "-show_data", "-of",
                                            "json", "-i", source});
                yyjson_mut_val *packet = nullptr;
                yyjson_mut_arr_iter packet_iterator = yyjson_mut_arr_iter_with(member(packets, "packets"));
                while (auto *candidate = yyjson_mut_arr_iter_next(&packet_iterator))
                    if (yyjson_mut_is_obj(candidate))
                    {
                        packet = candidate;
                        break;
                    }
                if (packet != nullptr && integer(json, member(packet, "size")) <= 1048576)
                {
                    const auto bytes = hex_bytes(json.text(member(packet, "data")));
                    dji_status = lr_dji_parse_color(bytes.data(), bytes.size(), &dji, nullptr);
                }
            }
            catch (const probe_failure &failure)
            {
                if (failure.status == LR_CANCELLED || !cleaned())
                    throw;
                dji_status = failure.status;
            }
        }
        auto *formats = pixel_formats(json);
        auto *pixel = json.object();
        yyjson_mut_arr_iter pixel_iterator = yyjson_mut_arr_iter_with(member(formats, "pixel_formats"));
        const auto pixel_name = json.text(member(video, "pix_fmt"));
        while (auto *item = yyjson_mut_arr_iter_next(&pixel_iterator))
            if (yyjson_mut_is_obj(item) && json.text(member(item, "name")) == pixel_name)
            {
                pixel = item;
                break;
            }
        phase(LR_PROBE_PHASE_ASSEMBLE);
        uint32_t flags = raw ? LR_PROBE_RAW | LR_PROBE_RAW_CALIBRATION_PENDING : 0;
        yyjson_mut_val *dolby = nullptr;
        const auto side_data = [&](yyjson_mut_val *owner)
        {
            yyjson_mut_arr_iter sides = yyjson_mut_arr_iter_with(member(owner, "side_data_list"));
            while (auto *side = yyjson_mut_arr_iter_next(&sides))
            {
                const auto type = json.text(member(side, "side_data_type"));
                if (type.find("HDR10+") != std::string::npos)
                    flags |= LR_PROBE_HDR10PLUS;
                if (dolby == nullptr && type == "DOVI configuration record")
                {
                    dolby = side;
                    flags |= LR_PROBE_DOLBY_VISION;
                }
            }
        };
        side_data(video);
        yyjson_mut_arr_iter frame_iterator = yyjson_mut_arr_iter_with(frames);
        while (auto *frame = yyjson_mut_arr_iter_next(&frame_iterator))
            side_data(frame);
        std::vector<std::pair<std::string, std::string>> strings;
        if (yyjson_mut_obj_size(tags) > 1024)
            throw probe_failure(LR_RESOURCE_LIMIT, "Too many source tags.");
        yyjson_mut_obj_iter tag_iterator = yyjson_mut_obj_iter_with(tags);
        while (auto *key = yyjson_mut_obj_iter_next(&tag_iterator))
            strings.emplace_back(json.text(key), json.text(yyjson_mut_obj_iter_get_val(key)));
        std::vector<lr_media_tag> native_tags;
        native_tags.reserve(strings.size());
        for (const auto &[key, value] : strings)
            native_tags.push_back({view(key), view(value)});
        const auto transfer = json.text(member(video, "color_transfer"));
        const std::string schema = dji_status == LR_OK ? schema_name(dji.schema) : "";
        lr_classify_input input;
        lr_classify_input_init(&input, sizeof(input));
        input.codec = view(codec);
        input.transfer = view(transfer);
        input.tags = native_tags.data();
        input.tag_count = static_cast<uint32_t>(native_tags.size());
        if (dji_status == LR_OK)
        {
            input.flags |= LR_CLASSIFY_HAS_DJI;
            input.dji_schema = view(schema);
            input.dji_mode = dji.mode;
        }
        if (dolby != nullptr)
        {
            input.flags |= LR_CLASSIFY_HAS_DOLBY_VISION;
            input.dolby_vision_profile =
                static_cast<uint32_t>(std::max(0, integer(json, member(dolby, "dv_profile"))));
            input.dolby_vision_compatibility = static_cast<uint32_t>(
                std::max(0, integer(json, member(dolby, "dv_bl_signal_compatibility_id"))));
        }
        lr_classification classified;
        lr_classification_init(&classified, sizeof(classified));
        lr_error error;
        lr_error_init(&error, sizeof(error));
        const auto status = lr_media_classify(&input, &classified, &error);
        if (status != LR_OK)
            throw probe_failure(status, error.message);
        const auto duration = luma::probe_number(json.text(member(format, "duration")),
                                                 luma::probe_number(json.text(member(video, "duration"))));
        auto *result = json.object();
        json.put(result, "schema_version", yyjson_mut_uint(json.doc(), 1));
        json.put(result, "path", json.string(source));
        json.put(result, "video", json.copy(video));
        json.put(result, "streams", yyjson_mut_is_arr(streams) ? json.copy(streams) : json.array());
        json.put(result, "frames", json.copy(frames));
        json.put(result, "tags", json.copy(tags));
        json.put(result, "pixel", json.copy(pixel));
        json.put(result, "kind", json.string(kind_name(classified.kind)));
        json.put(result, "profile",
                 classified.profile == LR_PROFILE_NONE ? yyjson_mut_null(json.doc())
                                                       : json.string(profile_name(classified.profile)));
        json.put(result, "duration", yyjson_mut_real(json.doc(), duration));
        json.put(result, "dolby_vision", dolby == nullptr ? yyjson_mut_null(json.doc()) : json.copy(dolby));
        json.put(result, "hdr10plus", yyjson_mut_bool(json.doc(), (flags & LR_PROBE_HDR10PLUS) != 0));
        json.put(result, "raw_calibration_pending", yyjson_mut_bool(json.doc(), raw));
        json.put(result, "dji_status", yyjson_mut_int(json.doc(), dji_status));
        if (dji_status == LR_OK)
        {
            auto *color = json.object();
            json.put(color, "schema", json.string(schema));
            json.put(color, "mode", yyjson_mut_uint(json.doc(), dji.mode));
            json.put(result, "dji_color", color);
        }
        else
            json.put(result, "dji_color", yyjson_mut_null(json.doc()));
        auto *evidence = json.object();
        json.put(evidence, "kind", yyjson_mut_uint(json.doc(), classified.kind));
        json.put(evidence, "profile", yyjson_mut_uint(json.doc(), classified.profile));
        json.put(evidence, "evidence", yyjson_mut_uint(json.doc(), classified.evidence));
        json.put(evidence, "tag_index",
                 classified.tag_index == UINT32_MAX ? yyjson_mut_null(json.doc())
                                                    : yyjson_mut_uint(json.doc(), classified.tag_index));
        json.put(result, "classification", evidence);
        auto serialized = json.write(result);
        cancellation();
        original->validate();
        {
            std::lock_guard lock(mutex);
            result_json = std::move(serialized);
            snapshot.flags = flags;
            snapshot.duration = duration;
            snapshot.json_size = result_json.size();
            snapshot.phase = LR_PROBE_PHASE_COMPLETE;
        }
    }
};

extern "C" lr_status lr_probe_create(const lr_probe_options *options, lr_probe **result, lr_error *error)
{
    return boundary(error,
                    [&]
                    {
                        if (options == nullptr || result == nullptr ||
                            options->struct_size != sizeof(*options) ||
                            options->abi_version != LR_ABI_VERSION || options->metadata_capacity == 0 ||
                            options->metadata_capacity > 64 * 1024 * 1024 || options->reserved != 0)
                            throw probe_failure(LR_INVALID_ARGUMENT, "Invalid source inspection options.");
                        auto owned = std::make_unique<lr_probe>();
                        owned->executable = copy_path(options->ffprobe);
                        owned->source = copy_path(options->source);
                        owned->timeout = options->timeout_ms;
                        owned->capacity = options->metadata_capacity;
                        lr_probe_snapshot_init(&owned->snapshot, sizeof(owned->snapshot));
                        *result = owned.release();
                    });
}
extern "C" lr_status lr_probe_run(lr_probe *probe, lr_error *error)
{
    return boundary(
        error,
        [&]() -> lr_status
        {
            if (probe == nullptr || (error != nullptr && error->struct_size != sizeof(*error)))
                return LR_INVALID_ARGUMENT;
            const auto started = boundary(error,
                                          [&]
                                          {
                                              std::lock_guard lock(probe->mutex);
                                              if (probe->snapshot.state != LR_PROBE_CREATED)
                                                  throw probe_failure(LR_INVALID_ARGUMENT,
                                                                      "Source inspection can run only once.");
                                              probe->snapshot.state = LR_PROBE_RUNNING;
                                              probe->snapshot.cleanup_state = LR_CLEANUP_CONFIRMED;
                                          });
            if (started != LR_OK)
                return started;
            const auto start = clock_type::now();
            auto status = boundary(error, [&] { probe->execute(); });
            {
                std::lock_guard lock(probe->mutex);
                if (status == LR_OK && probe->cancelled.load(std::memory_order_relaxed))
                    status = report(error, LR_CANCELLED, "Source inspection cancelled.");
                probe->snapshot.state = LR_PROBE_FINISHED;
                probe->snapshot.status = status;
                probe->snapshot.elapsed_ms = static_cast<uint64_t>(
                    std::chrono::duration_cast<std::chrono::milliseconds>(clock_type::now() - start).count());
                if (status != LR_OK)
                {
                    probe->result_json.clear();
                    probe->snapshot.json_size = 0;
                }
            }
            return status;
        });
}
extern "C" lr_status lr_probe_cancel(lr_probe *probe)
{
    return boundary(nullptr,
                    [&]
                    {
                        if (probe == nullptr)
                            throw probe_failure(LR_INVALID_ARGUMENT, "Invalid probe handle.");
                        std::lock_guard lock(probe->mutex);
                        if (probe->snapshot.state == LR_PROBE_FINISHED)
                            return;
                        probe->cancelled.store(true, std::memory_order_relaxed);
                        if (probe->active != nullptr)
                            lr_process_cancel(probe->active);
                    });
}
extern "C" lr_status lr_probe_get_snapshot(lr_probe *probe, lr_probe_snapshot *result)
{
    return boundary(nullptr,
                    [&]
                    {
                        if (probe == nullptr || result == nullptr || result->struct_size != sizeof(*result) ||
                            result->abi_version != LR_ABI_VERSION)
                            throw probe_failure(LR_INVALID_ARGUMENT, "Invalid probe snapshot.");
                        std::lock_guard lock(probe->mutex);
                        *result = probe->snapshot;
                    });
}
extern "C" lr_status lr_probe_copy_json(lr_probe *probe, uint8_t *destination, uint64_t capacity,
                                        uint64_t *required)
{
    return boundary(
        nullptr,
        [&]
        {
            if (probe == nullptr || required == nullptr || (capacity != 0 && destination == nullptr))
                throw probe_failure(LR_INVALID_ARGUMENT, "Invalid probe result buffer.");
            std::lock_guard lock(probe->mutex);
            if (probe->snapshot.state != LR_PROBE_FINISHED || probe->snapshot.status != LR_OK)
                throw probe_failure(LR_INVALID_ARGUMENT, "Source inspection has no successful result.");
            *required = probe->result_json.size();
            if (capacity < probe->result_json.size())
                throw probe_failure(LR_BUFFER_TOO_SMALL, "Probe result buffer is too small.");
            std::memcpy(destination, probe->result_json.data(), probe->result_json.size());
        });
}
extern "C" void lr_probe_release(lr_probe *probe)
{
    delete probe;
}
extern "C" lr_status lr_probe_normalize_json(lr_utf8_view input, uint8_t *destination, uint64_t capacity,
                                             uint64_t *required, lr_error *error)
{
    return boundary(error,
                    [&]
                    {
                        if (required == nullptr || (input.size != 0 && input.data == nullptr) ||
                            input.size > SIZE_MAX || (capacity != 0 && destination == nullptr))
                            throw probe_failure(LR_INVALID_ARGUMENT, "Invalid probe JSON buffer.");
                        luma::probe_json json;
                        const auto normalized =
                            json.write(json.parse({input.data, static_cast<size_t>(input.size)}));
                        *required = normalized.size();
                        if (capacity < normalized.size())
                            throw probe_failure(LR_BUFFER_TOO_SMALL, "Normalized JSON buffer is too small.");
                        std::memcpy(destination, normalized.data(), normalized.size());
                    });
}
