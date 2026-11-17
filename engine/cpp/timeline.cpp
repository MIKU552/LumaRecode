#include "lumarecode/timeline.h"
#include "output_internal.hpp"
#include "process_internal.hpp"

#include <algorithm>
#include <atomic>
#include <charconv>
#include <cmath>
#include <cstring>
#include <memory>
#include <mutex>
#include <string_view>

namespace
{
using clock_type = std::chrono::steady_clock;
constexpr uint64_t default_budget = 512ULL * 1024 * 1024;
constexpr size_t line_limit = 65536;
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
        function();
        return report(error, LR_OK, "");
    }
    catch (const luma::process_failure &failure)
    {
        return report(error, failure.status, failure.what());
    }
    catch (const luma::file_failure &failure)
    {
        return report(error, failure.status, failure.what());
    }
    catch (const std::bad_alloc &)
    {
        return report(error, LR_RESOURCE_LIMIT, "Timeline memory budget exhausted.");
    }
    catch (...)
    {
        return report(error, LR_INTERNAL_ERROR, "Timeline operation failed unexpectedly.");
    }
}
[[noreturn]] void fail(lr_status status, const char *message)
{
    throw luma::process_failure(status, message);
}
std::string copy_text(lr_utf8_view view, size_t maximum)
{
    if (view.size == 0 || view.size > maximum || lr_utf8_validate(view.data, view.size) != LR_OK ||
        std::memchr(view.data, 0, static_cast<size_t>(view.size)) != nullptr)
        fail(LR_INVALID_ARGUMENT, "Timeline strings require bounded UTF-8 without NUL.");
    return {reinterpret_cast<const char *>(view.data), static_cast<size_t>(view.size)};
}
std::filesystem::path path(const std::string &value)
{
    return std::filesystem::path(std::u8string(value.begin(), value.end()));
}
bool integer(std::string_view text, int64_t &result)
{
    const auto white = [](char c) { return c == ' ' || (c >= '\t' && c <= '\r'); };
    while (!text.empty() && white(text.front()))
        text.remove_prefix(1);
    while (!text.empty() && white(text.back()))
        text.remove_suffix(1);
    if (!text.empty() && text.front() == '+')
    {
        text.remove_prefix(1);
        if (!text.empty() && (text.front() == '-' || text.front() == '+'))
            return false;
    }
    if (text.empty())
        return false;
    const auto parsed = std::from_chars(text.data(), text.data() + text.size(), result);
    return parsed.ec == std::errc{} && parsed.ptr == text.data() + text.size();
}
bool read_point(std::string_view line, bool packets, lr_frame_point &point, bool &discarded)
{
    bool timestamp = false;
    point = {};
    discarded = false;
    while (!line.empty())
    {
        const auto separator = line.find('|');
        const auto field = line.substr(0, separator);
        const auto equals = field.find('=');
        if (equals != std::string_view::npos)
        {
            const auto key = field.substr(0, equals), value = field.substr(equals + 1);
            if (key == (packets ? "pts" : "best_effort_timestamp"))
                timestamp = integer(value, point.pts);
            else if (key == "duration")
            {
                if (!integer(value, point.duration))
                    point.duration = 0;
            }
            else if (packets && key == "flags")
            {
                discarded = value.find('D') != std::string_view::npos;
                point.key_frame = value.find('K') != std::string_view::npos;
            }
            else if (!packets && key == "key_frame")
                point.key_frame = value == "1";
        }
        if (separator == std::string_view::npos)
            break;
        line.remove_prefix(separator + 1);
    }
    return timestamp;
}
} // namespace

/* The process driver streams into this sink on run's thread. Only immutable
 * frame storage is published; UI snapshots never lock while sorting or scanning. */
struct lr_timeline final : luma::process_sink
{
    std::string executable, source, codec;
    int32_t video_index = 0;
    uint32_t input_flags = 0;
    uint64_t budget = default_budget, timeout = 0;
    std::atomic<bool> cancelled{false};
    std::mutex mutex;
    lr_timeline_snapshot snapshot{};
    std::vector<lr_frame_point> points;
    clock_type::time_point started_at{}, pass_started{};
    bool packets = false, invalid = false, child_started = false, cleanup_confirmed = true;
    bool skip_lf = false;
    int32_t exit_code = -1;
    std::string line, errors;

    lr_timeline()
    {
        lr_timeline_snapshot_init(&snapshot, sizeof(snapshot));
    }
    uint64_t elapsed() const
    {
        return static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::milliseconds>(clock_type::now() - started_at).count());
    }
    void check_stop() override
    {
        if (cancelled.load(std::memory_order_relaxed))
            fail(LR_CANCELLED, "Timeline scan cancelled.");
        if (timeout != 0 && clock_type::now() - pass_started >= std::chrono::milliseconds(timeout))
            fail(LR_TIMED_OUT, "Timeline scan timed out.");
    }
    void started() noexcept override
    {
        child_started = true;
        cleanup_confirmed = false;
    }
    void cleaned() noexcept override
    {
        cleanup_confirmed = true;
    }
    void exited(int32_t code) override
    {
        exit_code = code;
    }
    void add(lr_frame_point point)
    {
        const auto maximum = static_cast<size_t>(budget / (2 * sizeof(lr_frame_point)));
        if (points.size() == maximum)
            fail(LR_RESOURCE_LIMIT, "Timeline index exceeds its memory budget.");
        if (points.size() == points.capacity())
            points.reserve(std::min(maximum, std::max(size_t{256}, points.capacity() * 2)));
        points.push_back(point);
    }
    void finish_line()
    {
        const std::string_view text(line);
        if (text.starts_with(packets ? "packet|" : "frame|"))
        {
            lr_frame_point point{};
            bool discarded = false;
            const bool valid = read_point(text, packets, point, discarded);
            if (!discarded)
            {
                if (!valid)
                {
                    invalid = true;
                    // Stop this pass immediately; its driver still reaps the child.
                    fail(LR_INVALID_DATA, "Frame timestamp is missing or invalid.");
                }
                add(point);
            }
        }
        line.clear();
    }
    void consume(uint32_t stream, std::span<const uint8_t> bytes, bool cleaning) override
    {
        if (stream == LR_STREAM_STDERR)
        {
            constexpr size_t capacity = 4096;
            if (bytes.size() >= capacity)
                errors.assign(reinterpret_cast<const char *>(bytes.data() + bytes.size() - capacity),
                              capacity);
            else
            {
                if (errors.size() + bytes.size() > capacity)
                    errors.erase(0, errors.size() + bytes.size() - capacity);
                errors.append(reinterpret_cast<const char *>(bytes.data()), bytes.size());
            }
            return;
        }
        if (cleaning)
            return;
        for (const auto byte : bytes)
        {
            if (skip_lf)
            {
                skip_lf = false;
                if (byte == '\n')
                    continue;
            }
            if (byte == '\n' || byte == '\r')
            {
                skip_lf = byte == '\r';
                finish_line();
            }
            else
            {
                if (line.size() == line_limit)
                    fail(LR_RESOURCE_LIMIT, "Timeline record exceeds its line budget.");
                line.push_back(static_cast<char>(byte));
            }
        }
    }
    bool scan(bool packet_pass)
    {
        // Free the rejected packet index before growing a decoded replacement.
        std::vector<lr_frame_point>().swap(points);
        packets = packet_pass;
        line.clear();
        errors.clear();
        skip_lf = invalid = child_started = false;
        cleanup_confirmed = true;
        exit_code = -1;
        pass_started = clock_type::now();
        check_stop();
        luma::process_request request{
            executable,
            "",
            {"-v", "error", "-threads", "2", "-select_streams", std::to_string(video_index),
             packets ? "-show_packets" : "-show_frames", "-show_entries",
             packets ? "packet=pts,duration,flags:packet_side_data="
                     : "frame=best_effort_timestamp,duration,key_frame:frame_side_data=",
             "-of", "compact=p=1:nk=0", source}};
        try
        {
            luma::execute_child(request, *this);
            check_stop();
            if (exit_code != 0)
                throw luma::process_failure(LR_IO_ERROR, "Timeline ffprobe failed: " + errors);
            if (!line.empty())
                finish_line();
        }
        catch (const luma::process_failure &)
        {
            if (!invalid || !packets || !cleanup_confirmed)
                throw;
            check_stop();
            return false;
        }
        if (points.empty())
            return false;
        if (packets)
        {
            size_t comparisons = 0;
            std::sort(points.begin(), points.end(),
                      [&](const auto &left, const auto &right)
                      {
                          if ((++comparisons & 4095U) == 0)
                              check_stop();
                          return left.pts < right.pts;
                      });
        }
        for (size_t i = 1; i < points.size(); ++i)
        {
            if ((i & 4095U) == 0)
                check_stop();
            if (points[i].pts < points[i - 1].pts || (packets && points[i].pts == points[i - 1].pts))
                return false;
        }
        return true;
    }
    double seconds(size_t frame) const
    {
        return static_cast<double>(points[frame].pts) * snapshot.time_base_numerator /
               snapshot.time_base_denominator;
    }
    double boundary_seconds(size_t frame) const
    {
        if (frame < points.size())
            return seconds(frame);
        const auto &last = points.back();
        // Convert before subtracting: valid signed PTS can span INT64's range.
        const auto duration =
            last.duration > 0   ? static_cast<double>(last.duration)
            : points.size() > 1 ? std::max(1.0, static_cast<double>(last.pts) - points[points.size() - 2].pts)
                                : 1.0;
        return (static_cast<double>(last.pts) + duration) * snapshot.time_base_numerator /
               snapshot.time_base_denominator;
    }
    bool ready() const
    {
        return snapshot.state == LR_PROCESS_FINISHED && snapshot.status == LR_OK;
    }
};

extern "C" lr_status lr_timeline_create(const lr_timeline_options *options, lr_timeline **result,
                                        lr_error *error)
{
    if (options == nullptr || result == nullptr || options->struct_size != sizeof(*options) ||
        options->abi_version != LR_ABI_VERSION || options->time_base_numerator <= 0 ||
        options->time_base_denominator <= 0 || options->video_index < 0 ||
        (options->flags & ~LR_TIMELINE_PROGRESSIVE) != 0 || options->index_capacity < 48 ||
        options->index_capacity > default_budget || options->timeout_ms > INT64_MAX)
        return report(error, LR_INVALID_ARGUMENT, "Invalid timeline options.");
    return boundary(error,
                    [&]
                    {
                        auto value = std::make_unique<lr_timeline>();
                        value->executable = copy_text(options->ffprobe, 500000);
                        value->source = copy_text(options->source, 500000);
                        value->codec = copy_text(options->codec, 128);
                        if (!path(value->source).is_absolute() || !path(value->executable).is_absolute())
                            fail(LR_INVALID_ARGUMENT, "Timeline paths must be absolute.");
                        value->video_index = options->video_index;
                        value->input_flags = options->flags;
                        value->snapshot.time_base_numerator = options->time_base_numerator;
                        value->snapshot.time_base_denominator = options->time_base_denominator;
                        value->budget = options->index_capacity;
                        value->timeout = options->timeout_ms;
                        *result = value.release();
                    });
}

extern "C" lr_status lr_timeline_run(lr_timeline *timeline, lr_error *error)
{
    if (timeline == nullptr || (error != nullptr && error->struct_size != sizeof(*error)))
        return LR_INVALID_ARGUMENT;
    {
        std::lock_guard lock(timeline->mutex);
        if (timeline->snapshot.state != LR_PROCESS_CREATED)
            return report(error, LR_INVALID_ARGUMENT, "A timeline can run only once.");
        timeline->snapshot.state = LR_PROCESS_RUNNING;
        timeline->started_at = clock_type::now();
    }
    const auto status = boundary(
        error,
        [&]
        {
            timeline->pass_started = clock_type::now();
            timeline->check_stop();
            const auto lease = luma::open_source_lease(path(timeline->source));
            const bool packet_candidate =
                (timeline->input_flags & LR_TIMELINE_PROGRESSIVE) != 0 &&
                (timeline->codec == "h264" || timeline->codec == "hevc" || timeline->codec == "prores" ||
                 timeline->codec == "prores_raw" || timeline->codec == "mjpeg" || timeline->codec == "dnxhd");
            if (!packet_candidate || !timeline->scan(true))
            {
                timeline->check_stop();
                lease->validate();
                if (!timeline->scan(false))
                    fail(LR_INVALID_DATA, "Cannot establish decoded frame display order.");
            }
            timeline->check_stop();
            lease->validate();
        });
    std::lock_guard lock(timeline->mutex);
    timeline->snapshot.status = timeline->cleanup_confirmed ? status : LR_IO_ERROR;
    timeline->snapshot.cleanup_state = timeline->cleanup_confirmed ? LR_CLEANUP_CONFIRMED : LR_CLEANUP_FAILED;
    timeline->snapshot.state = LR_PROCESS_FINISHED;
    timeline->snapshot.elapsed_ms = timeline->elapsed();
    if (timeline->snapshot.status == LR_OK)
    {
        timeline->snapshot.count = timeline->points.size();
        timeline->snapshot.flags = timeline->packets ? LR_TIMELINE_PACKETS : 0;
        timeline->snapshot.duration =
            timeline->boundary_seconds(timeline->points.size()) - timeline->seconds(0);
    }
    else
        std::vector<lr_frame_point>().swap(timeline->points);
    if (!timeline->cleanup_confirmed)
        return report(error, LR_IO_ERROR, "Timeline child cleanup is unconfirmed; preserve task files.");
    return status;
}

extern "C" lr_status lr_timeline_from_points(const lr_frame_point *points, uint64_t count, int64_t numerator,
                                             int64_t denominator, uint32_t flags, lr_timeline **result,
                                             lr_error *error)
{
    if (points == nullptr || count == 0 || result == nullptr || numerator <= 0 || denominator <= 0 ||
        (flags & ~LR_TIMELINE_PACKETS) != 0)
        return report(error, LR_INVALID_ARGUMENT, "Invalid timeline import.");
    if (count > default_budget / (2 * sizeof(lr_frame_point)))
        return report(error, LR_RESOURCE_LIMIT, "Timeline import exceeds its memory budget.");
    return boundary(
        error,
        [&]
        {
            for (size_t i = 0; i < count; ++i)
            {
                if (points[i].reserved != 0 || points[i].key_frame > 1 ||
                    (i > 0 && (points[i].pts < points[i - 1].pts ||
                               ((flags & LR_TIMELINE_PACKETS) != 0 && points[i].pts == points[i - 1].pts))))
                    fail(LR_INVALID_DATA, "Invalid frame point or display order.");
            }
            auto value = std::make_unique<lr_timeline>();
            value->points.assign(points, points + count);
            value->snapshot.time_base_numerator = numerator;
            value->snapshot.time_base_denominator = denominator;
            value->snapshot.state = LR_PROCESS_FINISHED;
            value->snapshot.cleanup_state = LR_CLEANUP_CONFIRMED;
            value->snapshot.count = count;
            value->snapshot.flags = flags;
            value->snapshot.duration =
                value->boundary_seconds(static_cast<size_t>(count)) - value->seconds(0);
            *result = value.release();
        });
}
extern "C" lr_status lr_timeline_cancel(lr_timeline *timeline)
{
    if (timeline == nullptr)
        return LR_INVALID_ARGUMENT;
    timeline->cancelled.store(true, std::memory_order_relaxed);
    return LR_OK;
}
extern "C" lr_status lr_timeline_get_snapshot(lr_timeline *timeline, lr_timeline_snapshot *result)
{
    if (timeline == nullptr || result == nullptr || result->struct_size != sizeof(*result) ||
        result->abi_version != LR_ABI_VERSION)
        return LR_INVALID_ARGUMENT;
    std::lock_guard lock(timeline->mutex);
    *result = timeline->snapshot;
    if (result->state == LR_PROCESS_RUNNING)
        result->elapsed_ms = timeline->elapsed();
    return LR_OK;
}
extern "C" lr_status lr_timeline_copy_points(lr_timeline *timeline, uint64_t start, uint64_t count,
                                             lr_frame_point *destination, uint64_t capacity)
{
    if (timeline == nullptr || (count != 0 && destination == nullptr))
        return LR_INVALID_ARGUMENT;
    std::lock_guard lock(timeline->mutex);
    if (!timeline->ready() || start > timeline->points.size() || count > timeline->points.size() - start)
        return LR_INVALID_ARGUMENT;
    if (capacity < count)
        return LR_BUFFER_TOO_SMALL;
    if (count != 0)
        std::memcpy(destination, timeline->points.data() + start,
                    static_cast<size_t>(count) * sizeof(*destination));
    return LR_OK;
}
extern "C" lr_status lr_timeline_at_or_after(lr_timeline *timeline, double position, uint64_t *result)
{
    if (timeline == nullptr || result == nullptr || !std::isfinite(position))
        return LR_INVALID_ARGUMENT;
    std::lock_guard lock(timeline->mutex);
    if (!timeline->ready())
        return LR_INVALID_ARGUMENT;
    size_t lower = 0, upper = timeline->points.size();
    while (lower < upper)
    {
        const auto middle = lower + (upper - lower) / 2;
        if (timeline->seconds(middle) - timeline->seconds(0) + 1e-9 < position)
            lower = middle + 1;
        else
            upper = middle;
    }
    *result = lower;
    return LR_OK;
}
extern "C" lr_status lr_timeline_resolve(lr_timeline *timeline, uint64_t start, uint64_t end,
                                         lr_frame_window *result)
{
    if (timeline == nullptr || result == nullptr || result->struct_size != sizeof(*result) ||
        result->abi_version != LR_ABI_VERSION)
        return LR_INVALID_ARGUMENT;
    std::lock_guard lock(timeline->mutex);
    if (!timeline->ready())
        return LR_INVALID_ARGUMENT;
    if (end == UINT64_MAX)
        end = timeline->points.size();
    if (start >= end || end > timeline->points.size())
        return LR_INVALID_ARGUMENT;
    size_t seek = 0;
    if ((timeline->snapshot.flags & LR_TIMELINE_PACKETS) != 0)
    {
        for (size_t i = static_cast<size_t>(start) + 1; i > 0; --i)
        {
            if (timeline->points[i - 1].key_frame != 0 && timeline->seconds(i - 1) >= 0)
            {
                seek = i - 1;
                break;
            }
        }
    }
    lr_frame_window window{};
    lr_frame_window_init(&window, sizeof(window));
    window.start = start;
    window.end = end;
    window.seek_frame = seek;
    window.seek_seconds = seek == 0 ? 0 : timeline->seconds(seek);
    window.start_seconds = timeline->seconds(static_cast<size_t>(start));
    window.end_seconds = timeline->boundary_seconds(static_cast<size_t>(end));
    *result = window;
    return LR_OK;
}
extern "C" void lr_timeline_release(lr_timeline *timeline)
{
    delete timeline;
}
