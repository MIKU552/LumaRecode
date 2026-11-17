#include "lumarecode/probe.h"

#include <chrono>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <thread>

namespace fs = std::filesystem;
namespace
{
unsigned failures = 0;
void check(bool value, const char *message)
{
    if (!value)
    {
        ++failures;
        std::cerr << message << '\n';
    }
}
lr_utf8_view view(const std::string &text)
{
    return {reinterpret_cast<const uint8_t *>(text.data()), text.size()};
}
std::string utf8(const fs::path &path)
{
    const auto bytes = path.u8string();
    return {bytes.begin(), bytes.end()};
}
void json_contracts()
{
    lr_error error;
    lr_error_init(&error, sizeof(error));
    const std::string duplicate = R"({"x":1,"x":2})";
    uint8_t output[32];
    std::memset(output, 0xa5, sizeof(output));
    uint64_t needed = 42;
    check(lr_probe_normalize_json(view(duplicate), output, 1, &needed, &error) == LR_BUFFER_TOO_SMALL &&
              needed > 1,
          "Normalized JSON capacity query failed.");
    check(output[0] == 0xa5 && output[31] == 0xa5, "Normalized JSON partially wrote a short buffer.");
    const uint8_t invalid[] = {0xff};
    needed = 42;
    check(lr_probe_normalize_json({invalid, sizeof(invalid)}, output, sizeof(output), &needed, &error) ==
                  LR_INVALID_DATA &&
              needed == 42,
          "Invalid JSON changed its size/result outputs.");
    check(output[0] == 0xa5 && output[31] == 0xa5, "Invalid JSON changed destination bytes.");
    check(lr_probe_normalize_json({invalid, 64 * 1024 * 1024 + 1}, nullptr, 0, &needed, &error) ==
              LR_RESOURCE_LIMIT,
          "Probe JSON input limit was ignored.");
}
} // namespace
int main(int count, char **arguments)
{
    if (count != 2)
        return 2;
    json_contracts();
    const auto parent = fs::canonical(fs::temp_directory_path());
    const auto root = parent / ("LumaRecode-probe-native-" +
                                std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
    if (!fs::create_directory(root))
        return 2;
    const auto source_path = root / "source.dat";
    {
        std::ofstream file(source_path);
        file << "frame_wait";
    }
    const std::string source = utf8(source_path), executable = utf8(fs::absolute(arguments[1]));
    lr_probe_options options;
    lr_probe_options_init(&options, sizeof(options));
    options.source = view(source);
    options.ffprobe = view(executable);
    lr_error error;
    lr_error_init(&error, sizeof(error));
    lr_probe *probe = nullptr;
    check(lr_probe_create(&options, &probe, &error) == LR_OK, error.message);
    if (probe == nullptr)
        return 1;
    uint8_t sentinel = 0xa5;
    uint64_t needed = 42;
    check(lr_probe_copy_json(probe, &sentinel, 1, &needed) == LR_INVALID_ARGUMENT && sentinel == 0xa5 &&
              needed == 42,
          "Unfinished probe exposed partial JSON.");
    lr_status status = LR_INTERNAL_ERROR;
    std::thread worker([&] { status = lr_probe_run(probe, nullptr); });
    lr_probe_snapshot snapshot;
    lr_probe_snapshot_init(&snapshot, sizeof(snapshot));
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(8);
    do
    {
        check(lr_probe_get_snapshot(probe, &snapshot) == LR_OK, "Cannot observe running probe.");
        if (snapshot.phase == LR_PROBE_PHASE_FRAMES && snapshot.cleanup_state == LR_CLEANUP_PENDING)
            break;
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    } while (snapshot.state != LR_PROBE_FINISHED && std::chrono::steady_clock::now() < deadline);
    check(snapshot.phase == LR_PROBE_PHASE_FRAMES && snapshot.state == LR_PROBE_RUNNING,
          "Probe never entered its first-frame stage.");
    check(lr_probe_run(probe, &error) == LR_INVALID_ARGUMENT, "A probe was run concurrently twice.");
    check(lr_probe_cancel(probe) == LR_OK, "Probe cancellation request failed.");
    worker.join();
    check(status == LR_CANCELLED, "Probe did not return cancellation.");
    lr_probe_get_snapshot(probe, &snapshot);
    check(snapshot.state == LR_PROBE_FINISHED && snapshot.status == LR_CANCELLED &&
              snapshot.cleanup_state == LR_CLEANUP_CONFIRMED,
          "Probe cancellation did not finish cleanup.");
    const auto terminal = snapshot;
    check(lr_probe_cancel(probe) == LR_OK, "Terminal cancellation failed.");
    lr_probe_get_snapshot(probe, &snapshot);
    check(std::memcmp(&terminal, &snapshot, sizeof(snapshot)) == 0, "Terminal probe state changed.");
    check(lr_probe_copy_json(probe, &sentinel, 1, &needed) == LR_INVALID_ARGUMENT && sentinel == 0xa5 &&
              needed == 42,
          "Cancelled probe exposed JSON.");
    lr_probe_release(probe);
    if (terminal.cleanup_state == LR_CLEANUP_CONFIRMED && root.parent_path() == parent)
    {
        fs::remove(source_path);
        fs::remove(root);
    }
    return failures == 0 ? 0 : 1;
}
