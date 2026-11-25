#ifndef LUMARECODE_HDR10PLUS_H
#define LUMARECODE_HDR10PLUS_H

#include "lumarecode/core.h"

#ifdef __cplusplus
extern "C"
{
#endif

/* Integer code values, matching the existing x265 interchange. max_scl,
 * average_rgb and distribution_values use a denominator of 100000;
 * knee points use 4095 and anchors use 1023. No floating-point round trip. */
typedef struct lr_hdr10plus_metadata
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t application_version;
    uint32_t number_of_windows;
    uint32_t targeted_display_luminance;
    uint32_t max_scl[3];
    uint32_t average_rgb;
    uint32_t distribution_count;
    uint32_t distribution_indices[15];
    uint32_t distribution_values[15];
    uint32_t tone_mapping;
    uint32_t knee_point_x;
    uint32_t knee_point_y;
    uint32_t anchor_count;
    uint32_t anchors[15];
} lr_hdr10plus_metadata;

LR_API lr_status lr_hdr10plus_metadata_init(lr_hdr10plus_metadata *value, uint64_t capacity);
/* Parse one complete ITU-T T.35 payload, including its six-byte HDR10+ prefix.
 * The current representable subset matches the stable application's x265
 * writer: application version 1, one window and no peak grid/saturation map.
 * Unsupported features are reported; output is unchanged on every failure.
 * Input is borrowed for this call only, with a one-MiB per-record budget. */
LR_API lr_status lr_hdr10plus_parse_t35(const uint8_t *payload, uint64_t length,
                                        lr_hdr10plus_metadata *result, lr_error *error);

#ifdef __cplusplus
}
#endif
#endif
