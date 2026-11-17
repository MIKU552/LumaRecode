#include "lumarecode/core.h"
#include "lumarecode/hdr10plus.h"
#include "lumarecode/process.h"

#include <stdio.h>
#include <string.h>

static int failures;
#define CHECK(expression)                                                                                    \
    do                                                                                                       \
    {                                                                                                        \
        if (!(expression))                                                                                   \
        {                                                                                                    \
            fprintf(stderr, "C contract failed at line %d: %s\n", __LINE__, #expression);                    \
            ++failures;                                                                                      \
        }                                                                                                    \
    } while (0)

static void check_initialization(void)
{
    lr_encode_options options;
    lr_source_format source;
    lr_resolved_format result;
    lr_hdr10plus_metadata metadata;
    unsigned char before[sizeof(options)];
    CHECK(lr_abi_version() == LR_ABI_VERSION);
    memset(&options, 0xa5, sizeof(options));
    memcpy(before, &options, sizeof(options));
    CHECK(lr_encode_options_init(&options, sizeof(options) - 1) == LR_BUFFER_TOO_SMALL);
    CHECK(memcmp(before, &options, sizeof(options)) == 0);
    CHECK(lr_encode_options_init(NULL, sizeof(options)) == LR_INVALID_ARGUMENT);
    CHECK(lr_encode_options_init(&options, sizeof(options)) == LR_OK);
    CHECK(options.struct_size == sizeof(options) && options.abi_version == LR_ABI_VERSION);
    CHECK(options.bit_depth == 0 && options.sampling == LR_SAMPLING_AUTO);
    CHECK(lr_source_format_init(&source, sizeof(source)) == LR_OK);
    CHECK(lr_resolved_format_init(&result, sizeof(result)) == LR_OK);
    CHECK(lr_hdr10plus_metadata_init(&metadata, sizeof(metadata)) == LR_OK);
    CHECK(sizeof(metadata) == 236);
    CHECK(strcmp(lr_status_name(LR_CANCELLED), "cancelled") == 0);
    CHECK(strcmp(lr_status_name(-1), "unknown") == 0);
}

static void check_planes(void)
{
    uint8_t bytes[12] = {1, 2, 3, 90, 4, 5, 6, 91, 7, 8, 9, 92};
    uint8_t output[15];
    lr_plane source = {bytes, sizeof(bytes), 0, 4, 3, 3};
    lr_plane destination = {output, sizeof(output), 0, 5, 3, 3};
    memset(output, 0xa5, sizeof(output));
    CHECK(lr_copy_plane(&source, &destination) == LR_OK);
    CHECK(output[0] == 1 && output[5] == 4 && output[10] == 7);
    CHECK(output[3] == 0xa5 && output[4] == 0xa5 && output[14] == 0xa5);
    source.origin = 8;
    source.stride = -4;
    CHECK(lr_copy_plane(&source, &destination) == LR_OK);
    CHECK(output[0] == 7 && output[5] == 4 && output[10] == 1);
    destination.size = 12;
    memset(output, 0xa5, sizeof(output));
    CHECK(lr_copy_plane(&source, &destination) == LR_BUFFER_TOO_SMALL);
    for (size_t i = 0; i < sizeof(output); ++i)
        CHECK(output[i] == 0xa5);
    CHECK(lr_copy_plane(&source, &source) == LR_OK);
    destination = source;
    destination.data = bytes + 1;
    destination.size = sizeof(bytes) - 1;
    destination.origin = 8;
    CHECK(lr_copy_plane(&source, &destination) == LR_INVALID_ARGUMENT);
    source.stride = INT64_MIN;
    CHECK(lr_validate_plane(&source) == LR_INVALID_ARGUMENT);
    source.stride = 4;
    source.origin = UINT64_MAX;
    CHECK(lr_validate_plane(&source) == LR_BUFFER_TOO_SMALL);
    source.origin = 0;
    source.height = UINT64_MAX;
    CHECK(lr_validate_plane(&source) == LR_INVALID_ARGUMENT);
    source.width_bytes = 0;
    CHECK(lr_validate_plane(&source) == LR_OK);
}

static void check_utf8(void)
{
    uint32_t scalar;
    const uint8_t invalid[][4] = {
        {0x80, 0, 0, 0},          {0xc0, 0x80, 0, 0},       {0xe0, 0x80, 0x80, 0},    {0xed, 0xa0, 0x80, 0},
        {0xf0, 0x80, 0x80, 0x80}, {0xf4, 0x90, 0x80, 0x80}, {0xf5, 0x80, 0x80, 0x80}, {0xe2, 0x28, 0xa1, 0}};
    size_t i;
    CHECK(lr_utf8_validate(NULL, 0) == LR_OK);
    CHECK(lr_utf8_validate(NULL, 1) == LR_INVALID_ARGUMENT);
    for (i = 0; i < sizeof(invalid) / sizeof(invalid[0]); ++i)
    {
        CHECK(lr_utf8_validate(invalid[i], sizeof(invalid[i])) == LR_INVALID_DATA);
    }
    for (scalar = 0; scalar <= 0x10ffff; ++scalar)
    {
        uint8_t bytes[4];
        size_t count;
        if (scalar >= 0xd800 && scalar <= 0xdfff)
        {
            continue;
        }
        if (scalar < 0x80)
        {
            bytes[0] = (uint8_t)scalar;
            count = 1;
        }
        else if (scalar < 0x800)
        {
            bytes[0] = (uint8_t)(0xc0 | (scalar >> 6));
            bytes[1] = (uint8_t)(0x80 | (scalar & 0x3f));
            count = 2;
        }
        else if (scalar < 0x10000)
        {
            bytes[0] = (uint8_t)(0xe0 | (scalar >> 12));
            bytes[1] = (uint8_t)(0x80 | ((scalar >> 6) & 0x3f));
            bytes[2] = (uint8_t)(0x80 | (scalar & 0x3f));
            count = 3;
        }
        else
        {
            bytes[0] = (uint8_t)(0xf0 | (scalar >> 18));
            bytes[1] = (uint8_t)(0x80 | ((scalar >> 12) & 0x3f));
            bytes[2] = (uint8_t)(0x80 | ((scalar >> 6) & 0x3f));
            bytes[3] = (uint8_t)(0x80 | (scalar & 0x3f));
            count = 4;
        }
        CHECK(lr_utf8_validate(bytes, count) == LR_OK);
        if (count > 1)
        {
            CHECK(lr_utf8_validate(bytes, count - 1) == LR_INVALID_DATA);
        }
    }
}

static void check_process_initialization(void)
{
    lr_process_options options;
    lr_process_snapshot snapshot;
    CHECK(sizeof(options) == 80 && sizeof(snapshot) == 64 && sizeof(lr_utf8_view) == 16);
    CHECK(lr_process_options_init(&options, sizeof(options) - 1) == LR_BUFFER_TOO_SMALL);
    CHECK(lr_process_options_init(&options, sizeof(options)) == LR_OK);
    CHECK(options.stdout_mode == LR_CAPTURE_FULL && options.stdout_capacity == 8 * 1024 * 1024);
    CHECK(options.stderr_capacity == 8192 && options.timeout_ms == 25000);
    CHECK(lr_process_snapshot_init(&snapshot, sizeof(snapshot)) == LR_OK);
    CHECK(snapshot.state == LR_PROCESS_CREATED && snapshot.exit_code == -1);
}

int main(void)
{
    check_initialization();
    check_planes();
    check_utf8();
    check_process_initialization();
    if (failures != 0)
        return 1;
    puts("C ABI initialization, buffer ownership and signed-stride checks passed.");
    return 0;
}
