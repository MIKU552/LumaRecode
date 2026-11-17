#ifndef LUMARECODE_PLAN_H
#define LUMARECODE_PLAN_H
#include "lumarecode/timeline.h"

#ifdef __cplusplus
extern "C"
{
#endif
#define LR_PLAN_QSV_TIMING 1U
#define LR_PLAN_AUTO_LOW_POWER 2U
#define LR_PLAN_MOTION_PHOTO 4U

typedef struct lr_plan_options
{
    uint32_t struct_size;
    uint32_t abi_version;
    /* probe schema 1; encode choices use the application's documented JSON keys.
     * Presets are supplied by the host from the versioned application assets. */
    lr_utf8_view media_json;
    lr_utf8_view encode_json;
    lr_utf8_view presets_json;
    lr_utf8_view motion_presets_json;
    lr_utf8_view ffmpeg;
    lr_utf8_view destination;
    lr_utf8_view assets;
    /* Zero means the complete input/selection. */
    double seconds;
    uint32_t flags;
    uint32_t preview_size;
    /* Optional, borrowed for create only. Obtained from timeline_resolve. */
    const lr_frame_window *window;
    /* Empty means source; otherwise source, 1080 or 720. */
    lr_utf8_view motion_size;
} lr_plan_options;

typedef struct lr_plan lr_plan;
LR_API lr_status lr_plan_options_init(lr_plan_options *value, uint64_t capacity);
/* Pure planning: validates source precision, transforms, audio and dynamic HDR,
 * resolves defaults, and builds a shell-free FFmpeg argument vector. Does not
 * inspect devices, start processes, access files or create output directories.
 * Paths are absolute UTF-8. Limits: media 64 MiB, each preset 1 MiB, choices 64 KiB,
 * each path 500,000 bytes. A 128 MiB JSON allocation budget is shared per call.
 * RAW calibration/development is a separate pipeline; RAW returns UNSUPPORTED
 * until that pipeline supplies a supported planning contract. */
LR_API lr_status lr_plan_create(const lr_plan_options *options, lr_plan **result, lr_error *error);
/* Immutable schema 1 JSON: arguments, colors, options, working_directory,
 * asset_directory, qsv_timing_compatibility, uses_software_decoding and sidecars.
 * Sidecars must be prepared before execution (for example hdr10plus.json).
 * required is set on OK/BUFFER_TOO_SMALL; bytes have no trailing NUL. */
LR_API lr_status lr_plan_copy_json(const lr_plan *plan, uint8_t *destination, uint64_t capacity,
                                   uint64_t *required);
/* Check a newly probed staging output against this plan before seal/commit.
 * source_json carries the first selected frame's side data for trimmed HDR.
 * Both media snapshots have the same 64 MiB/128 MiB JSON budgets as planning.
 * A format/codec/audio/color/metadata mismatch returns INVALID_DATA. */
LR_API lr_status lr_plan_verify_output(const lr_plan *plan, lr_utf8_view source_json,
                                       lr_utf8_view output_json, lr_error *error);
LR_API void lr_plan_release(lr_plan *plan);
#ifdef __cplusplus
}
#endif
#endif
