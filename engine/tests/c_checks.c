#include "lumarecode/core.h"
#include "lumarecode/hdr10plus.h"

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

int main(void)
{
    check_initialization();
    check_planes();
    if (failures != 0)
        return 1;
    puts("C ABI initialization, buffer ownership and signed-stride checks passed.");
    return 0;
}
