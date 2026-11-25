#ifndef LUMARECODE_PROBE_H
#define LUMARECODE_PROBE_H

#include "lumarecode/media.h"
#include "lumarecode/process.h"

#ifdef __cplusplus
extern "C"
{
#endif

#define LR_PROBE_CREATED 0U
#define LR_PROBE_RUNNING 1U
#define LR_PROBE_FINISHED 2U
#define LR_PROBE_HDR10PLUS 1U
#define LR_PROBE_DOLBY_VISION 2U
#define LR_PROBE_RAW 4U
#define LR_PROBE_RAW_CALIBRATION_PENDING 8U
#define LR_PROBE_PHASE_PENDING 0U
#define LR_PROBE_PHASE_STREAMS 1U
#define LR_PROBE_PHASE_FRAMES 2U
#define LR_PROBE_PHASE_DJI 3U
#define LR_PROBE_PHASE_PIXEL_FORMATS 4U
#define LR_PROBE_PHASE_ASSEMBLE 5U
#define LR_PROBE_PHASE_COMPLETE 6U

typedef struct lr_probe_options
{
    uint32_t struct_size;
    uint32_t abi_version;
    lr_utf8_view ffprobe;
    lr_utf8_view source;
    uint64_t timeout_ms;
    uint64_t metadata_capacity;
    uint64_t reserved;
} lr_probe_options;

typedef struct lr_probe_snapshot
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t state;
    lr_status status;
    uint32_t flags;
    uint32_t cleanup_state;
    int32_t video_index;
    int32_t dji_index;
    uint32_t audio_count;
    uint32_t phase;
    double duration;
    uint64_t elapsed_ms;
    uint64_t json_size;
} lr_probe_snapshot;
typedef struct lr_probe lr_probe;

LR_API lr_status lr_probe_options_init(lr_probe_options *value, uint64_t capacity);
LR_API lr_status lr_probe_snapshot_init(lr_probe_snapshot *value, uint64_t capacity);
/* Copies absolute UTF-8 paths without launching processes. Default deadline is
 * 25 seconds per subprocess; 0 disables it. JSON capture defaults to 8 MiB and
 * may be raised to 64 MiB for large metadata, independently of media file size. */
LR_API lr_status lr_probe_create(const lr_probe_options *options, lr_probe **result, lr_error *error);
/* Run once off the UI thread. Selection, first-frame fallback, DJI, pixel
 * descriptors and classification use the shared core. RAW returns stream
 * metadata with RAW_CALIBRATION_PENDING for the development stage. */
LR_API lr_status lr_probe_run(lr_probe *probe, lr_error *error);
LR_API lr_status lr_probe_cancel(lr_probe *probe);
LR_API lr_status lr_probe_get_snapshot(lr_probe *probe, lr_probe_snapshot *result);
/* A successful immutable JSON result with schema_version=1. Includes complete
 * streams, selected video, first frames, merged tags, pixel descriptor and
 * classification evidence. No result is exposed after cancellation or failure.
 * As with process copy, required is written on BUFFER_TOO_SMALL. */
LR_API lr_status lr_probe_copy_json(lr_probe *probe, uint8_t *destination, uint64_t capacity,
                                    uint64_t *required);
/* Release only after all other calls return. */
LR_API void lr_probe_release(lr_probe *probe);
/* Normalize ffprobe duplicate object keys into arrays, retaining array-valued
 * originals as elements. Strict UTF-8 JSON, 64 input nesting levels, 64 MiB input
 * and 128 MiB tree/workspace budget. Destination is unchanged on failure. */
LR_API lr_status lr_probe_normalize_json(lr_utf8_view input, uint8_t *destination, uint64_t capacity,
                                         uint64_t *required, lr_error *error);

#ifdef __cplusplus
}
#endif
#endif
