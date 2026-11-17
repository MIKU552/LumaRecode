#include "lumarecode/timeline.h"

#include <array>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <thread>

namespace fs = std::filesystem;
namespace
{
unsigned failures = 0, checks = 0;
void check(bool condition, const char *message)
{
    ++checks;
    if (!condition)
    {
        ++failures;
        std::cerr << message << '\n';
    }
}
lr_utf8_view view(const std::string &text)
{
    return {reinterpret_cast<const uint8_t *>(text.data()), text.size()};
}
std::string utf8(const fs::path &value)
{
    const auto bytes = value.u8string();
    return {bytes.begin(), bytes.end()};
}
void imports()
{
    std::array<lr_frame_point, 5> points{
        {{-20, 10, 1, 0}, {0, 10, 1, 0}, {10, 20, 0, 0}, {30, 15, 1, 0}, {45, 0, 0, 0}}};
    lr_timeline *timeline = nullptr;
    check(lr_timeline_from_points(points.data(), points.size(), 1, 100, LR_TIMELINE_PACKETS, &timeline,
                                  nullptr) == LR_OK,
          "Cannot import timeline.");
    if (timeline == nullptr)
        return;
    points[0].pts = -99;
    lr_frame_point first{};
    check(lr_timeline_copy_points(timeline, 0, 1, &first, 1) == LR_OK && first.pts == -20,
          "Timeline import borrows caller storage.");
    for (unsigned i = 0; i < 5; ++i)
    {
        uint64_t frame = 99;
        check(lr_timeline_at_or_after(timeline,
                                      (i == 0   ? 0
                                       : i == 1 ? .2
                                       : i == 2 ? .3
                                       : i == 3 ? .5
                                                : .65),
                                      &frame) == LR_OK &&
                  frame == i,
              "Wrong frame time lookup.");
    }
    lr_frame_window window;
    lr_frame_window_init(&window, sizeof(window));
    check(lr_timeline_resolve(timeline, 4, UINT64_MAX, &window) == LR_OK && window.seek_frame == 3 &&
              window.start == 4 && window.end == 5 && std::abs(window.end_seconds - .6) < 1e-12,
          "Wrong VFR final-frame boundary or preceding keyframe.");
    const auto previous = window;
    check(lr_timeline_resolve(timeline, 5, 4, &window) == LR_INVALID_ARGUMENT &&
              window.start == previous.start && window.end == previous.end,
          "Invalid frame selection changed its output.");
    uint64_t frame = 99;
    check(lr_timeline_at_or_after(timeline, std::numeric_limits<double>::quiet_NaN(), &frame) ==
                  LR_INVALID_ARGUMENT &&
              frame == 99,
          "Non-finite time changed output.");
    check(lr_timeline_copy_points(timeline, 4, 2, &first, 1) == LR_INVALID_ARGUMENT && first.pts == -20,
          "Out-of-range copy changed its output.");
    lr_timeline_release(timeline);
    timeline = nullptr;
    points[0].pts = 1;
    check(lr_timeline_from_points(points.data(), points.size(), 1, 100, 0, &timeline, nullptr) ==
                  LR_INVALID_DATA &&
              timeline == nullptr,
          "Descending imported PTS accepted.");
    points[0].pts = 0;
    check(lr_timeline_from_points(points.data(), points.size(), 1, 100, LR_TIMELINE_PACKETS, &timeline,
                                  nullptr) == LR_INVALID_DATA,
          "Duplicate packet PTS accepted.");
    check(lr_timeline_from_points(points.data(), points.size(), 1, 100, 0, &timeline, nullptr) == LR_OK,
          "Decoded duplicate display PTS rejected.");
    lr_timeline_release(timeline);
    timeline = nullptr;
    points[0].reserved = 1;
    check(lr_timeline_from_points(points.data(), points.size(), 1, 100, 0, &timeline, nullptr) ==
              LR_INVALID_DATA,
          "Nonzero reserved frame field accepted.");
    check(lr_timeline_from_points(points.data(), UINT64_MAX, 1, 100, 0, &timeline, nullptr) ==
              LR_RESOURCE_LIMIT,
          "Oversized index import read beyond bounded input.");
}
} // namespace
int main(int count, char **arguments)
{
    if (count != 2)
        return 2;
    imports();
    const auto parent = fs::canonical(fs::temp_directory_path());
    const auto root = parent / ("LumaRecode-timeline-native-" +
                                std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
    if (!fs::create_directory(root))
        return 2;
    const auto file = root / "source.dat";
    const auto source = utf8(file), tool = utf8(fs::absolute(arguments[1]));
    const std::string codec = "hevc";
    lr_timeline_options options;
    lr_timeline_options_init(&options, sizeof(options));
    options.ffprobe = view(tool);
    options.source = view(source);
    options.codec = view(codec);
    options.flags = LR_TIMELINE_PROGRESSIVE;
    options.time_base_numerator = 1;
    options.time_base_denominator = 100;
    for (const auto *mode : {"valid", "missing", "duplicate", "descending", "empty", "overflow", "longline",
                             "many", "error", "wait", "cancel", "pre_cancel"})
    {
        {
            std::ofstream stream(file);
            stream << (std::string(mode).find("cancel") != std::string::npos ? "wait" : mode);
        }
        const std::string name(mode);
        options.flags = name == "descending" ? 0 : LR_TIMELINE_PROGRESSIVE;
        options.timeout_ms = name == "wait" ? 60 : 0;
        options.index_capacity = name == "many" ? 480 : 512ULL * 1024 * 1024;
        lr_timeline *timeline = nullptr;
        lr_error error;
        lr_error_init(&error, sizeof(error));
        check(lr_timeline_create(&options, &timeline, &error) == LR_OK, error.message);
        if (timeline == nullptr)
            continue;
        uint64_t position = 123;
        check(lr_timeline_at_or_after(timeline, 0, &position) == LR_INVALID_ARGUMENT && position == 123,
              "Created timeline exposes partial frames.");
        lr_status status = LR_INTERNAL_ERROR;
        if (name == "pre_cancel")
            lr_timeline_cancel(timeline);
        if (name == "cancel")
        {
            std::thread worker([&] { status = lr_timeline_run(timeline, &error); });
            lr_timeline_snapshot live;
            lr_timeline_snapshot_init(&live, sizeof(live));
            const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
            do
            {
                lr_timeline_get_snapshot(timeline, &live);
                if (live.state != LR_PROCESS_CREATED)
                    break;
                std::this_thread::yield();
            } while (std::chrono::steady_clock::now() < deadline);
            lr_timeline_cancel(timeline);
            worker.join();
        }
        else
            status = lr_timeline_run(timeline, &error);
        const auto expected = name == "valid" || name == "missing" || name == "duplicate" ? LR_OK
                              : name == "wait"                                            ? LR_TIMED_OUT
                              : name == "cancel" || name == "pre_cancel"                  ? LR_CANCELLED
                              : name == "longline" || name == "many"                      ? LR_RESOURCE_LIMIT
                              : name == "error"                                           ? LR_IO_ERROR
                                                                                          : LR_INVALID_DATA;
        if (status != expected)
            std::cerr << name << ": " << error.message << '\n';
        check(status == expected, "Unexpected timeline terminal status.");
        lr_timeline_snapshot snapshot;
        lr_timeline_snapshot_init(&snapshot, sizeof(snapshot));
        check(lr_timeline_get_snapshot(timeline, &snapshot) == LR_OK &&
                  snapshot.state == LR_PROCESS_FINISHED && snapshot.status == status &&
                  snapshot.cleanup_state == LR_CLEANUP_CONFIRMED,
              "Timeline terminal state/cleanup mismatch.");
        check(lr_timeline_run(timeline, nullptr) == LR_INVALID_ARGUMENT, "Timeline ran twice.");
        if (status == LR_OK)
        {
            check(snapshot.count == 3 && snapshot.flags == (name == "valid" ? LR_TIMELINE_PACKETS : 0) &&
                      std::abs(snapshot.duration - .45) < 1e-12,
                  "Wrong index fallback or VFR duration.");
            std::array<lr_frame_point, 3> points{};
            check(lr_timeline_copy_points(timeline, 0, 3, points.data(), points.size()) == LR_OK &&
                      points[0].pts == 0 && points[1].pts == 10 && points[2].pts == 30,
                  "Packet ordering/preroll/fallback mismatch.");
        }
        else
            check(snapshot.count == 0, "Failed timeline retained a published partial index.");
        lr_timeline_release(timeline);
    }
    // This test owns exactly one file and one directory; never recursively clean
    // an unverified path in the system temporary directory.
    if (fs::canonical(root).parent_path() == parent)
    {
        fs::remove(file);
        fs::remove(root);
    }
    std::cout << checks << " timeline checks, " << failures << " failures.\n";
    return failures == 0 ? 0 : 1;
}
