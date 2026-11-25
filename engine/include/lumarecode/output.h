#ifndef LUMARECODE_OUTPUT_H
#define LUMARECODE_OUTPUT_H

#include "lumarecode/core.h"

#ifdef __cplusplus
extern "C"
{
#endif

#define LR_OUTPUT_CREATED 0U
#define LR_OUTPUT_SEALED 1U
#define LR_OUTPUT_COMMITTED 2U
#define LR_OUTPUT_ABORTED 3U
#define LR_OUTPUT_RETAINED 4U
#define LR_OUTPUT_WORKSPACE_EXISTS 1U
#define LR_OUTPUT_PATH_WORKSPACE 1U
#define LR_OUTPUT_PATH_STAGING 2U
#define LR_OUTPUT_PATH_DESTINATION 3U

typedef struct lr_output_options
{
    uint32_t struct_size;
    uint32_t abi_version;
    lr_utf8_view source;
    lr_utf8_view destination;
    uint32_t overwrite;
    uint32_t reserved;
} lr_output_options;

typedef struct lr_output_snapshot
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t state;
    uint32_t flags;
    uint64_t source_size;
    uint64_t output_size;
} lr_output_snapshot;

typedef struct lr_output_transaction lr_output_transaction;

LR_API lr_status lr_output_options_init(lr_output_options *value, uint64_t capacity);
LR_API lr_status lr_output_snapshot_init(lr_output_snapshot *value, uint64_t capacity);
/* Existing regular files are compared by OS identity, following source aliases.
 * Missing paths return NOT_FOUND and leave result unchanged. */
LR_API lr_status lr_files_same(lr_utf8_view left, lr_utf8_view right, uint32_t *result, lr_error *error);

/* Paths must be absolute, without dot components. create() copies strings, pins
 * the source and output parent, then creates an exclusive private workspace in
 * that parent. No destination file is created or changed. */
LR_API lr_status lr_output_create(const lr_output_options *options, lr_output_transaction **result,
                                  lr_error *error);
LR_API lr_status lr_output_get_snapshot(lr_output_transaction *transaction, lr_output_snapshot *result);
/* UTF-8 paths exclude a terminator. required is written on BUFFER_TOO_SMALL;
 * destination bytes are unchanged unless the entire path fits. */
LR_API lr_status lr_output_copy_path(lr_output_transaction *transaction, uint32_t kind, uint8_t *destination,
                                     uint64_t capacity, uint64_t *required);
/* The caller first completes media verification and confirms that every process
 * touching staging output has finished cleanup. seal() pins a nonempty regular
 * staging file, rejects symlinks/hard links and records its identity and metadata. */
LR_API lr_status lr_output_seal(lr_output_transaction *transaction, uint32_t cleanup_confirmed,
                                lr_error *error);
/* Recheck source, staging and destination before one OS rename. Overwrite
 * consent is immutable. Post-publication workspace cleanup failure is exposed
 * by WORKSPACE_EXISTS while the committed result remains successful. */
LR_API lr_status lr_output_commit(lr_output_transaction *transaction, lr_error *error);
/* Remove only the known staging entry and the empty owned directory. No
 * recursive traversal. Unconfirmed cleanup retains the workspace and returns
 * IO_ERROR; cleanup can be retried after processes have stopped. */
LR_API lr_status lr_output_cleanup(lr_output_transaction *transaction, uint32_t cleanup_confirmed,
                                   lr_error *error);
/* No implicit deletion. Release after other calls return; close native handles
 * while leaving unpublished files available for explicit recovery. */
LR_API void lr_output_release(lr_output_transaction *transaction);

#ifdef __cplusplus
}
#endif
#endif
