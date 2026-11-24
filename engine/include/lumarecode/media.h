#ifndef LUMARECODE_MEDIA_H
#define LUMARECODE_MEDIA_H

#include "lumarecode/core.h"

#ifdef __cplusplus
extern "C"
{
#endif

#define LR_MEDIA_UNKNOWN 0U
#define LR_MEDIA_SDR 1U
#define LR_MEDIA_HLG 2U
#define LR_MEDIA_PQ 3U
#define LR_MEDIA_LOG 4U
#define LR_MEDIA_RAW 5U
#define LR_PROFILE_NONE 0U
#define LR_PROFILE_DJI_POCKET3 1U
#define LR_PROFILE_SAMSUNG_LOG 2U
#define LR_PROFILE_MI_LOG 3U
#define LR_PROFILE_APPLE_LOG 4U
#define LR_PROFILE_APPLE_LOG2 5U

#define LR_EVIDENCE_UNKNOWN_TRANSFER 0U
#define LR_EVIDENCE_RAW 1U
#define LR_EVIDENCE_APPLE_LOG 2U
#define LR_EVIDENCE_APPLE_LOG2 3U
#define LR_EVIDENCE_MI_LOG 4U
#define LR_EVIDENCE_SAMSUNG_LOG 5U
#define LR_EVIDENCE_DJI_DLOG_M 6U
#define LR_EVIDENCE_DJI_OTHER_DLOG_M 7U
#define LR_EVIDENCE_DJI_LOG 8U
#define LR_EVIDENCE_DJI_HLG 9U
#define LR_EVIDENCE_DJI_SDR 10U
#define LR_EVIDENCE_CAMERA_TAG 11U
#define LR_EVIDENCE_LOG_TAG 12U
#define LR_EVIDENCE_LOG_SWITCH 13U
#define LR_EVIDENCE_TRANSFER_HLG 14U
#define LR_EVIDENCE_TRANSFER_PQ 15U
#define LR_EVIDENCE_TRANSFER_SDR 16U
#define LR_EVIDENCE_DOLBY_VISION 17U

#define LR_CLASSIFY_HAS_DJI 1U
#define LR_CLASSIFY_HAS_DOLBY_VISION 2U
#define LR_DJI_POCKET3 1U
#define LR_DJI_AC206 2U
#define LR_DJI_OQ101 3U

typedef struct lr_media_tag
{
    lr_utf8_view key;
    lr_utf8_view value;
} lr_media_tag;

typedef struct lr_classify_input
{
    uint32_t struct_size;
    uint32_t abi_version;
    lr_utf8_view codec;
    lr_utf8_view transfer;
    const lr_media_tag *tags;
    uint32_t tag_count;
    uint32_t flags;
    lr_utf8_view dji_schema;
    uint64_t dji_mode;
    uint32_t dolby_vision_profile;
    uint32_t dolby_vision_compatibility;
} lr_classify_input;

typedef struct lr_classification
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t kind;
    uint32_t profile;
    uint32_t evidence;
    /* Original input tag index, or UINT32_MAX when evidence is not a tag. */
    uint32_t tag_index;
} lr_classification;

typedef struct lr_dji_color
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t schema;
    uint32_t reserved;
    uint64_t mode;
} lr_dji_color;

LR_API lr_status lr_classify_input_init(lr_classify_input *value, uint64_t capacity);
LR_API lr_status lr_classification_init(lr_classification *value, uint64_t capacity);
LR_API lr_status lr_dji_color_init(lr_dji_color *value, uint64_t capacity);
/* Camera markers use ASCII case folding. Later case-equivalent tag keys replace
 * values without changing precedence. Dolby Vision overrides camera/transfer
 * classification, matching source inspection. Missing curves remain PROFILE_NONE.
 * Borrowed views live only for this call: 1024 tags, 64 KiB/view, 1 MiB total.
 * On failure result is unchanged; error and result must be initialized. */
LR_API lr_status lr_media_classify(const lr_classify_input *input, lr_classification *result,
                                   lr_error *error);

/* Decode ffprobe's ASCII offset/hex/ASCII-column dump. Two passes validate before
 * writing; required is set on OK/BUFFER_TOO_SMALL. Buffers must not overlap.
 * Limits: 16 MiB dump, 1 MiB decoded bytes, independent of media file size. */
LR_API lr_status lr_probe_decode_hex(lr_utf8_view dump, uint8_t *destination, uint64_t capacity,
                                     uint64_t *required);
/* Parse one djmd packet, at most 1 MiB. Known schema IDs are listed above;
 * unknown schemas return NOT_FOUND. No heap allocation or media-file ownership. */
LR_API lr_status lr_dji_parse_color(const uint8_t *packet, uint64_t size, lr_dji_color *result,
                                    lr_error *error);
/* Examine decoded AVC extradata (avcC or Annex B). NOT_FOUND means no SPS was
 * found. Malformed input returns INVALID_DATA. The result is unchanged on error. */
LR_API lr_status lr_avc_is_monochrome(const uint8_t *extradata, uint64_t size, uint32_t *result,
                                      lr_error *error);

/* Read the schema-version-1 media snapshot produced by probe.h. Pixel flags,
 * component precision, storage padding, camera range and monochrome/RAW evidence
 * determine the source format. AUTO sampling means an unknown representation.
 * profile_range overrides only an absent range tag. Input is bounded to 64 MiB;
 * initialized result/error and pointed-to memory remain caller owned. */
LR_API lr_status lr_media_source_format(lr_utf8_view media_json, lr_color_range profile_range,
                                        lr_source_format *result, lr_error *error);
/* Validate one ffprobe audio stream for lossless integer FLAC conversion.
 * On success depth receives 1-32; on failure it is unchanged. */
LR_API lr_status lr_media_flac_depth(lr_utf8_view stream_json, uint32_t *depth, lr_error *error);

typedef struct lr_raw_calibration
{
    uint32_t width;
    uint32_t height;
    uint32_t crop[4];
    uint32_t white_balance_kelvin;
    uint32_t bayer_pattern;
    double matrix[12];
} lr_raw_calibration;

/* Decode the first 86 bytes of a ProRes RAW packet. The full MOV sample size
 * is verified separately from the borrowed header. Output changes on success. */
LR_API lr_status lr_media_raw_calibration(const uint8_t *header, uint64_t header_size, uint64_t packet_size,
                                          lr_raw_calibration *result);

#ifdef __cplusplus
}
#endif
#endif
