#include "lumarecode/core.h"
#include "lumarecode/hdr10plus.h"

#include <cstdio>
#include <cstring>
#include <vector>

namespace
{
int failures = 0;
#define CHECK(expression)                                                                                    \
    do                                                                                                       \
    {                                                                                                        \
        if (!(expression))                                                                                   \
        {                                                                                                    \
            std::fprintf(stderr, "Core contract failed at line %d: %s\n", __LINE__, #expression);            \
            ++failures;                                                                                      \
        }                                                                                                    \
    } while (0)

void check_policy()
{
    lr_source_format source;
    lr_encode_options options;
    lr_resolved_format result;
    lr_error error;
    lr_source_format_init(&source, sizeof(source));
    lr_encode_options_init(&options, sizeof(options));
    lr_resolved_format_init(&result, sizeof(result));
    lr_error_init(&error, sizeof(error));
    source.bit_depth = 8;
    source.sampling = LR_SAMPLING_420;
    source.dynamic_range = LR_DYNAMIC_RANGE_SDR;
    source.color_range = LR_RANGE_FULL;
    options.purpose = LR_PURPOSE_ARCHIVE;
    CHECK(lr_resolve_format(&source, &options, &result, &error) == LR_OK);
    CHECK(result.bit_depth == 8 && result.sampling == LR_SAMPLING_420 && result.color_range == LR_RANGE_FULL);
    const auto previous = result;
    options.bit_depth = 10;
    CHECK(lr_resolve_format(&source, &options, &result, &error) == LR_UNSUPPORTED);
    CHECK(std::memcmp(&previous, &result, sizeof(result)) == 0);
    options.bit_depth = 0;
    options.sampling = LR_SAMPLING_444;
    CHECK(lr_resolve_format(&source, &options, &result, &error) == LR_UNSUPPORTED);
    options.sampling = LR_SAMPLING_AUTO;
    options.purpose = LR_PURPOSE_SHARE;
    CHECK(lr_resolve_format(&source, &options, &result, &error) == LR_OK);
    CHECK(result.bit_depth == 8 && result.color_range == LR_RANGE_LIMITED);
    source.bit_depth = 10;
    source.dynamic_range = LR_DYNAMIC_RANGE_HDR;
    CHECK(lr_resolve_format(&source, &options, &result, &error) == LR_OK);
    CHECK(result.bit_depth == 10 && result.dynamic_range == LR_DYNAMIC_RANGE_HDR);
    options.codec = LR_CODEC_AVC;
    CHECK(lr_resolve_format(&source, &options, &result, &error) == LR_UNSUPPORTED);
    source.dynamic_range = LR_DYNAMIC_RANGE_SDR;
    source.sampling = LR_SAMPLING_RGB;
    options.purpose = LR_PURPOSE_ARCHIVE;
    CHECK(lr_resolve_format(&source, &options, &result, &error) == LR_OK);
    CHECK(result.bit_depth == 10 && result.sampling == LR_SAMPLING_444);
    options.codec = LR_CODEC_AV1;
    CHECK(lr_resolve_format(&source, &options, &result, &error) == LR_OK);
    CHECK(result.sampling == LR_SAMPLING_420);
    source.sampling = LR_SAMPLING_422;
    options.codec = LR_CODEC_HEVC;
    CHECK(lr_resolve_format(&source, &options, &result, &error) == LR_OK);
    CHECK(result.sampling == LR_SAMPLING_422);
    source.sampling = LR_SAMPLING_440;
    CHECK(lr_resolve_format(&source, &options, &result, &error) == LR_OK);
    CHECK(result.sampling == LR_SAMPLING_420);
    source.sampling = LR_SAMPLING_GRAY;
    CHECK(lr_resolve_format(&source, &options, &result, &error) == LR_OK);
    CHECK(result.sampling == LR_SAMPLING_GRAY);
    source.abi_version = LR_ABI_VERSION + 1;
    CHECK(lr_resolve_format(&source, &options, &result, &error) == LR_INVALID_ARGUMENT);
    CHECK(lr_resolve_format(nullptr, &options, &result, &error) == LR_INVALID_ARGUMENT);
}

std::vector<uint8_t> metadata_fixture(bool curve)
{
    std::vector<uint8_t> bytes{0xb5, 0, 0x3c, 0, 1, 4};
    uint8_t byte = 0;
    unsigned used = 0;
    auto put = [&](uint32_t value, unsigned width)
    {
        while (width > 0)
        {
            byte = static_cast<uint8_t>((byte << 1) | ((value >> --width) & 1));
            if (++used == 8)
            {
                bytes.push_back(byte);
                byte = 0;
                used = 0;
            }
        }
    };
    put(1, 8);
    put(1, 2);
    put(curve ? 1000 : 0, 27);
    put(0, 1);
    put(1000, 17);
    put(2000, 17);
    put(3000, 17);
    put(1500, 17);
    put(2, 4);
    put(1, 7);
    put(500, 17);
    put(100, 7);
    put(3000, 17);
    put(0, 10);
    put(0, 1);
    put(curve ? 1 : 0, 1);
    if (curve)
    {
        put(1024, 12);
        put(2048, 12);
        put(2, 4);
        put(100, 10);
        put(900, 10);
    }
    put(0, 1);
    if (used != 0)
        bytes.push_back(static_cast<uint8_t>(byte << (8 - used)));
    return bytes;
}

void check_rust_bridge()
{
    for (const bool curve : {false, true})
    {
        auto bytes = metadata_fixture(curve);
        lr_hdr10plus_metadata metadata;
        lr_error error;
        lr_hdr10plus_metadata_init(&metadata, sizeof(metadata));
        lr_error_init(&error, sizeof(error));
        CHECK(lr_hdr10plus_parse_t35(bytes.data(), bytes.size(), &metadata, &error) == LR_OK);
        CHECK(metadata.max_scl[0] == 1000 && metadata.max_scl[2] == 3000 && metadata.average_rgb == 1500);
        CHECK(metadata.distribution_count == 2 && metadata.distribution_values[1] == 3000);
        CHECK(metadata.tone_mapping == static_cast<uint32_t>(curve));
        if (curve)
            CHECK(metadata.anchors[1] == 900 && metadata.knee_point_y == 2048);
        const auto previous = metadata;
        for (size_t length = 0; length < bytes.size(); ++length)
        {
            CHECK(lr_hdr10plus_parse_t35(bytes.data(), length, &metadata, &error) != LR_OK);
            CHECK(std::memcmp(&previous, &metadata, sizeof(metadata)) == 0);
        }
        bytes[6] = 2;
        CHECK(lr_hdr10plus_parse_t35(bytes.data(), bytes.size(), &metadata, &error) == LR_UNSUPPORTED);
        CHECK(lr_hdr10plus_parse_t35(nullptr, 0, &metadata, &error) == LR_INVALID_ARGUMENT);
    }
}
} // namespace

int main()
{
    check_policy();
    check_rust_bridge();
    if (failures != 0)
        return 1;
    std::puts("C++ output-policy and Rust HDR10+ boundary checks passed.");
    return 0;
}
