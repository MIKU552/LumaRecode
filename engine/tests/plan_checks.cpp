#include "lumarecode/media.h"
#include "lumarecode/plan.h"

#include <cstring>
#include <filesystem>
#include <iostream>
#include <limits>
#include <vector>

namespace
{
unsigned checks = 0, failures = 0;
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
} // namespace
int main()
{
    // No fixture files are needed: planning must be free of filesystem effects.
    const auto root_bytes = std::filesystem::current_path().u8string();
    const std::string root(root_bytes.begin(), root_bytes.end());
    const auto quote = [](const std::string &text)
    {
        std::string escaped;
        for (const auto c : text)
        {
            if (c == '\\' || c == '"')
                escaped += '\\';
            escaped += c;
        }
        return escaped;
    };
    const std::string media = "{\"schema_version\":1,\"path\":\"" + quote(root + "/missing.mkv") +
                              "\",\"kind\":\"sdr\",\"video\":{\"index\":0,\"width\":128,\"height\":72,\"pix_"
                              "fmt\":\"yuv420p\",\"codec_name\":\"h264\"}}";
    const std::string choices = R"({"purpose":"archive","codec":"avc"})";
    const std::string presets =
        R"({"archive":{"avc":{"encoder":"libx264","preset":"medium","quality":21}},"audio":{"archive_default":"copy"}})";
    const auto tool = root + "/absent-ffmpeg", destination = root + "/absent-output.mkv";
    lr_plan_options options;
    lr_plan_options_init(&options, sizeof(options));
    options.media_json = view(media);
    options.encode_json = view(choices);
    options.presets_json = view(presets);
    options.ffmpeg = view(tool);
    options.destination = view(destination);
    options.assets = view(root);
    lr_plan *plan = nullptr;
    lr_error error;
    lr_error_init(&error, sizeof(error));
    check(lr_plan_create(&options, &plan, &error) == LR_OK, error.message);
    if (plan == nullptr)
        return 1;
    uint64_t size = 0;
    check(lr_plan_copy_json(plan, nullptr, 0, &size) == LR_BUFFER_TOO_SMALL && size > 20,
          "Plan size query failed.");
    uint8_t sentinel = 0xa5;
    check(lr_plan_copy_json(plan, &sentinel, 1, &size) == LR_BUFFER_TOO_SMALL && sentinel == 0xa5,
          "Short plan buffer was modified.");
    std::vector<uint8_t> bytes(static_cast<size_t>(size));
    check(lr_plan_copy_json(plan, bytes.data(), bytes.size(), &size) == LR_OK, "Cannot copy immutable plan.");
    check(lr_plan_verify_output(plan, view(media), view(media), &error) == LR_UNSUPPORTED ||
              error.code == LR_INVALID_DATA,
          "Verification accepted absent required output color tags.");
    lr_plan_release(plan);
    plan = nullptr;
    for (unsigned failure = 0; failure < 8; ++failure)
    {
        auto bad = options;
        if (failure == 0)
            bad.flags = 0x80000000U;
        if (failure == 1)
            bad.seconds = std::numeric_limits<double>::infinity();
        if (failure == 2)
            bad.media_json.size = 64ULL * 1024 * 1024 + 1;
        if (failure == 3)
            bad.presets_json.size = 1024 * 1024 + 1;
        if (failure == 4)
            bad.destination = {nullptr, 1};
        if (failure == 5)
            bad.encode_json = {nullptr, 1};
        if (failure == 6)
            bad.seconds = -1;
        if (failure == 7)
            bad.motion_size = {nullptr, 1};
        check(lr_plan_create(&bad, &plan, &error) != LR_OK && plan == nullptr,
              "Invalid plan request changed result/was accepted.");
    }
    const std::string invalid = "{";
    lr_source_format format;
    lr_source_format_init(&format, sizeof(format));
    format.bit_depth = 99;
    check(lr_media_source_format(view(invalid), LR_RANGE_AUTO, &format, &error) == LR_INVALID_DATA &&
              format.bit_depth == 99,
          "Malformed source JSON changed initialized output.");
    uint32_t depth = 99;
    const std::string lossy = R"({"codec_name":"aac","channels":2,"bits_per_sample":16})";
    check(lr_media_flac_depth(view(lossy), &depth, &error) == LR_UNSUPPORTED && depth == 99,
          "Lossy FLAC request changed output depth.");
    std::cout << checks << " plan checks, " << failures << " failures.\n";
    return failures == 0 ? 0 : 1;
}
