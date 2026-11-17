#ifndef LUMARECODE_CORE_H
#define LUMARECODE_CORE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32) && !defined(LUMA_CORE_STATIC)
#if defined(LUMA_CORE_BUILD)
#define LR_API __declspec(dllexport)
#else
#define LR_API __declspec(dllimport)
#endif
#elif defined(__GNUC__)
#define LR_API __attribute__((visibility("default")))
#else
#define LR_API
#endif

#ifdef __cplusplus
extern "C"
{
#endif

/* This ABI is versioned independently of the application. Fields use fixed-width
 * values; enum constants never depend on a compiler's enum representation. All
 * pointed-to memory must be accessible for its declared length during a call. */
#define LR_ABI_VERSION 1U
typedef int32_t lr_status;
#define LR_OK 0
#define LR_INVALID_ARGUMENT 1
#define LR_UNSUPPORTED 2
#define LR_BUFFER_TOO_SMALL 3
#define LR_INVALID_DATA 4
#define LR_CANCELLED 5
#define LR_IO_ERROR 6
#define LR_INTERNAL_ERROR 7
#define LR_TIMED_OUT 8
#define LR_NOT_FOUND 9
#define LR_RESOURCE_LIMIT 10

typedef uint32_t lr_sampling;
#define LR_SAMPLING_AUTO 0U
#define LR_SAMPLING_420 1U
#define LR_SAMPLING_422 2U
#define LR_SAMPLING_444 3U
#define LR_SAMPLING_GRAY 4U
#define LR_SAMPLING_RGB 5U
#define LR_SAMPLING_440 6U
#define LR_SAMPLING_411 7U
#define LR_SAMPLING_410 8U
#define LR_SAMPLING_SOURCE 9U

typedef uint32_t lr_color_range;
#define LR_RANGE_AUTO 0U
#define LR_RANGE_LIMITED 1U
#define LR_RANGE_FULL 2U

typedef uint32_t lr_dynamic_range;
#define LR_DYNAMIC_RANGE_AUTO 0U
#define LR_DYNAMIC_RANGE_SDR 1U
#define LR_DYNAMIC_RANGE_HDR 2U

typedef uint32_t lr_purpose;
#define LR_PURPOSE_SHARE 1U
#define LR_PURPOSE_ARCHIVE 2U

typedef uint32_t lr_codec;
#define LR_CODEC_HEVC 1U
#define LR_CODEC_AVC 2U
#define LR_CODEC_AV1 3U

#define LR_SOURCE_ALPHA 1U
#define LR_SOURCE_RAW 2U

typedef struct lr_error
{
    uint32_t struct_size;
    lr_status code;
    /* A diagnostic, not a localized UI message. Always NUL terminated. */
    char message[768];
} lr_error;

typedef struct lr_source_format
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t bit_depth;
    lr_sampling sampling;
    lr_color_range color_range;
    lr_dynamic_range dynamic_range;
    uint32_t flags;
} lr_source_format;

typedef struct lr_encode_options
{
    uint32_t struct_size;
    uint32_t abi_version;
    lr_purpose purpose;
    lr_codec codec;
    lr_dynamic_range dynamic_range;
    /* Zero/AUTO requests the source-aware application policy. */
    uint32_t bit_depth;
    lr_sampling sampling;
    lr_color_range color_range;
} lr_encode_options;

typedef struct lr_resolved_format
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t bit_depth;
    lr_sampling sampling;
    lr_color_range color_range;
    lr_dynamic_range dynamic_range;
} lr_resolved_format;

/* A borrowed byte plane. origin locates logical row zero within [data, size).
 * A negative stride is valid. width_bytes describes active bytes, excluding
 * padding. These functions perform layout operations, not color conversion. */
typedef struct lr_plane
{
    uint8_t *data;
    uint64_t size;
    uint64_t origin;
    int64_t stride;
    uint64_t width_bytes;
    uint64_t height;
} lr_plane;

LR_API uint32_t lr_abi_version(void);
LR_API const char *lr_status_name(lr_status status);
/* Initialization writes exactly the current structure size, only if capacity
 * is sufficient. It does not choose source-dependent output defaults. */
LR_API lr_status lr_source_format_init(lr_source_format *value, uint64_t capacity);
LR_API lr_status lr_encode_options_init(lr_encode_options *value, uint64_t capacity);
LR_API lr_status lr_resolved_format_init(lr_resolved_format *value, uint64_t capacity);
LR_API lr_status lr_error_init(lr_error *value, uint64_t capacity);

LR_API lr_status lr_validate_plane(const lr_plane *plane);
/* On failure, destination bytes are unchanged. Overlapping storage is rejected
 * unless both views are identical. Padding bytes are never read or written. */
LR_API lr_status lr_copy_plane(const lr_plane *source, const lr_plane *destination);

/* This is application policy, before intersecting concrete device capabilities.
 * Result and optional error must first be initialized using their init helpers.
 * On failure, result is unchanged. Every request is validated; no exceptions
 * cross this interface. */
LR_API lr_status lr_resolve_format(const lr_source_format *source, const lr_encode_options *options,
                                   lr_resolved_format *result, lr_error *error);

#ifdef __cplusplus
}
#endif
#endif
