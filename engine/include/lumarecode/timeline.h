#ifndef LUMARECODE_TIMELINE_H
#define LUMARECODE_TIMELINE_H

#include "lumarecode/process.h"

#ifdef __cplusplus
extern "C"
{
#endif

#define LR_TIMELINE_PACKETS 1U
#define LR_TIMELINE_PROGRESSIVE 1U

/* Ticks are signed source timestamps in decoded display order. Duration may be
 * absent (zero). reserved must be zero; key_frame is either zero or one. */
typedef struct lr_frame_point
{
    int64_t pts;
    int64_t duration;
    uint32_t key_frame;
    uint32_t reserved;
} lr_frame_point;

typedef struct lr_frame_window
{
    uint32_t struct_size;
    uint32_t abi_version;
    /* Zero-based, end-exclusive display indices. */
    uint64_t start;
    uint64_t end;
    uint64_t seek_frame;
    double seek_seconds;
    double start_seconds;
    double end_seconds;
} lr_frame_window;

typedef struct lr_timeline_options
{
    uint32_t struct_size;
    uint32_t abi_version;
    lr_utf8_view ffprobe;
    lr_utf8_view source;
    lr_utf8_view codec;
    int64_t time_base_numerator;
    int64_t time_base_denominator;
    int32_t video_index;
    uint32_t flags;
    /* Index budget includes replacement storage during growth. Default 512 MiB;
     * no limit is imposed on the input movie's bytes or total streamed text. */
    uint64_t index_capacity;
    /* Per-pass deadline. Zero allows a full scan of a long movie. */
    uint64_t timeout_ms;
} lr_timeline_options;

typedef struct lr_timeline_snapshot
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t state;
    lr_status status;
    uint32_t flags;
    uint32_t cleanup_state;
    uint64_t count;
    int64_t time_base_numerator;
    int64_t time_base_denominator;
    double duration;
    uint64_t elapsed_ms;
} lr_timeline_snapshot;

typedef struct lr_timeline lr_timeline;
LR_API lr_status lr_timeline_options_init(lr_timeline_options *value, uint64_t capacity);
LR_API lr_status lr_timeline_snapshot_init(lr_timeline_snapshot *value, uint64_t capacity);
LR_API lr_status lr_frame_window_init(lr_frame_window *value, uint64_t capacity);

/* create copies strings without starting processes. run is blocking, once only;
 * cancel and get_snapshot may overlap it. Release after all calls have returned.
 * Packet PTS are sorted, excluding discarded preroll. Missing/duplicate packet
 * PTS fall back to decoded display frames. Source identity is pinned per scan. */
LR_API lr_status lr_timeline_create(const lr_timeline_options *options, lr_timeline **result,
                                    lr_error *error);
/* Derive codec, index, time base, source path and interlaced routing from a
 * probe schema-1 snapshot. Consume only ffprobe/index_capacity/timeout_ms from
 * initialized options; remaining source fields must be zero. JSON is bounded
 * to 64 MiB with the shared 128 MiB allocation budget. */
LR_API lr_status lr_timeline_create_from_probe(const lr_timeline_options *options, lr_utf8_view media_json,
                                               lr_timeline **result, lr_error *error);
LR_API lr_status lr_timeline_run(lr_timeline *timeline, lr_error *error);
LR_API lr_status lr_timeline_cancel(lr_timeline *timeline);
LR_API lr_status lr_timeline_get_snapshot(lr_timeline *timeline, lr_timeline_snapshot *result);

/* Import an already-owned display index: copied, validated and immediately ready.
 * Points must be nondecreasing in PTS. Import and load use the same 512 MiB
 * default index budget, including spare storage. On error *result is unchanged. */
LR_API lr_status lr_timeline_from_points(const lr_frame_point *points, uint64_t count, int64_t numerator,
                                         int64_t denominator, uint32_t flags, lr_timeline **result,
                                         lr_error *error);
/* These operations require a successful terminal state. Copy supports slices to
 * avoid duplicating an entire long-movie index in the host. Outputs are unchanged
 * on failure. at_or_after uses elapsed position relative to the first frame. */
LR_API lr_status lr_timeline_copy_points(lr_timeline *timeline, uint64_t start, uint64_t count,
                                         lr_frame_point *destination, uint64_t capacity);
LR_API lr_status lr_timeline_at_or_after(lr_timeline *timeline, double position, uint64_t *result);
/* end == UINT64_MAX selects the end of the movie. */
LR_API lr_status lr_timeline_resolve(lr_timeline *timeline, uint64_t start, uint64_t end,
                                     lr_frame_window *result);
LR_API void lr_timeline_release(lr_timeline *timeline);

#ifdef __cplusplus
}
#endif
#endif
