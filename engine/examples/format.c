#include "lumarecode/core.h"

#include <inttypes.h>
#include <stdio.h>

/* A C caller owns every structure. The engine borrows them only for the call;
 * no C++ object, allocator or Rust type crosses this public interface. */
int main(void)
{
    lr_source_format source;
    lr_encode_options options;
    lr_resolved_format output;
    lr_error error;
    if (lr_source_format_init(&source, sizeof(source)) != LR_OK ||
        lr_encode_options_init(&options, sizeof(options)) != LR_OK ||
        lr_resolved_format_init(&output, sizeof(output)) != LR_OK ||
        lr_error_init(&error, sizeof(error)) != LR_OK)
    {
        fputs("The loaded core uses an incompatible ABI.\n", stderr);
        return 1;
    }

    source.bit_depth = 10;
    source.sampling = LR_SAMPLING_422;
    source.color_range = LR_RANGE_FULL;
    source.dynamic_range = LR_DYNAMIC_RANGE_HDR;
    options.purpose = LR_PURPOSE_ARCHIVE;
    options.codec = LR_CODEC_HEVC;
    const lr_status status = lr_resolve_format(&source, &options, &output, &error);
    if (status != LR_OK)
    {
        fprintf(stderr, "%s: %s\n", lr_status_name(status), error.message);
        return 1;
    }
    printf("HEVC archive: %" PRIu32 "-bit, sampling=%" PRIu32 ", range=%" PRIu32 "\n", output.bit_depth,
           output.sampling, output.color_range);
    return output.bit_depth == 10 && output.sampling == LR_SAMPLING_422 && output.color_range == LR_RANGE_FULL
               ? 0
               : 1;
}
