#ifndef LUMARECODE_PROCESS_H
#define LUMARECODE_PROCESS_H

#include "lumarecode/core.h"

#ifdef __cplusplus
extern "C"
{
#endif

/* Views are borrowed only by create(). Lengths exclude a terminator. Strings
 * must be valid UTF-8 without embedded NUL; an empty directory inherits cwd. */
typedef struct lr_utf8_view
{
    const uint8_t *data;
    uint64_t size;
} lr_utf8_view;

#define LR_CAPTURE_FULL 0U
#define LR_CAPTURE_TAIL 1U
#define LR_CAPTURE_DISCARD 2U
#define LR_STREAM_STDOUT 1U
#define LR_STREAM_STDERR 2U
#define LR_PROCESS_CREATED 0U
#define LR_PROCESS_RUNNING 1U
#define LR_PROCESS_FINISHED 2U
#define LR_CLEANUP_PENDING 0U
#define LR_CLEANUP_CONFIRMED 1U
#define LR_CLEANUP_FAILED 2U

typedef struct lr_process_options
{
    uint32_t struct_size;
    uint32_t abi_version;
    lr_utf8_view executable;
    lr_utf8_view working_directory;
    /* Arguments exclude argv[0]. They are passed directly, without a shell. */
    const lr_utf8_view *arguments;
    uint32_t argument_count;
    uint32_t stdout_mode;
    uint64_t stdout_capacity;
    uint64_t stderr_capacity;
    /* Zero disables the deadline. Retention budgets never limit input files. */
    uint64_t timeout_ms;
} lr_process_options;

typedef struct lr_process_snapshot
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t state;
    lr_status status;
    /* -1 until an exit code is available. POSIX signal exits use 128+signal. */
    int32_t exit_code;
    /* File transactions require CONFIRMED before publishing or removing work. */
    uint32_t cleanup_state;
    uint64_t elapsed_ms;
    uint64_t stdout_seen;
    uint64_t stderr_seen;
    uint64_t stdout_retained;
    uint64_t stderr_retained;
} lr_process_snapshot;

typedef struct lr_process lr_process;

LR_API lr_status lr_process_options_init(lr_process_options *value, uint64_t capacity);
LR_API lr_status lr_process_snapshot_init(lr_process_snapshot *value, uint64_t capacity);
LR_API lr_status lr_utf8_validate(const uint8_t *data, uint64_t size);

/* create() copies all strings and arguments before returning. It launches
 * nothing. The executable and nonempty working directory must be absolute.
 * Limits: 1024 arguments, one MiB of strings, 64 MiB stdout, one MiB stderr. */
LR_API lr_status lr_process_create(const lr_process_options *options, lr_process **result, lr_error *error);
/* run() blocks its caller and may run once. Hosts dispatch it off the UI thread.
 * Cancellation is sticky, including before run(); terminal state is immutable.
 * Nonzero child exits return LR_IO_ERROR. Pipes and the direct child are joined
 * before returning; descendants in the owned job/process group are terminated.
 * Unconfirmed cleanup returns LR_IO_ERROR with LR_CLEANUP_FAILED. */
LR_API lr_status lr_process_run(lr_process *process, lr_error *error);
LR_API lr_status lr_process_cancel(lr_process *process);
LR_API lr_status lr_process_get_snapshot(lr_process *process, lr_process_snapshot *result);
/* Byte snapshots preserve binary data; no terminator is added. required receives
 * the current retained size, including on BUFFER_TOO_SMALL. Destination bytes
 * are unchanged on failure. Reads and cancellation may overlap run(). */
LR_API lr_status lr_process_copy_output(lr_process *process, uint32_t stream, uint8_t *destination,
                                        uint64_t capacity, uint64_t *required);
/* Release only after all calls using this handle have returned. */
LR_API void lr_process_release(lr_process *process);

#ifdef __cplusplus
}
#endif
#endif
