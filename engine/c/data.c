#include "lumarecode/core.h"
#include "lumarecode/hdr10plus.h"

#include <string.h>

uint32_t lr_abi_version(void)
{
    return LR_ABI_VERSION;
}

const char *lr_status_name(lr_status status)
{
    switch (status)
    {
    case LR_OK:
        return "ok";
    case LR_INVALID_ARGUMENT:
        return "invalid_argument";
    case LR_UNSUPPORTED:
        return "unsupported";
    case LR_BUFFER_TOO_SMALL:
        return "buffer_too_small";
    case LR_INVALID_DATA:
        return "invalid_data";
    case LR_CANCELLED:
        return "cancelled";
    case LR_IO_ERROR:
        return "io_error";
    case LR_INTERNAL_ERROR:
        return "internal_error";
    case LR_TIMED_OUT:
        return "timed_out";
    case LR_NOT_FOUND:
        return "not_found";
    default:
        return "unknown";
    }
}

lr_status lr_source_format_init(lr_source_format *value, uint64_t capacity)
{
    if (value == NULL)
        return LR_INVALID_ARGUMENT;
    if (capacity < sizeof(*value))
        return LR_BUFFER_TOO_SMALL;
    memset(value, 0, sizeof(*value));
    value->struct_size = (uint32_t)sizeof(*value);
    value->abi_version = LR_ABI_VERSION;
    return LR_OK;
}

lr_status lr_encode_options_init(lr_encode_options *value, uint64_t capacity)
{
    if (value == NULL)
        return LR_INVALID_ARGUMENT;
    if (capacity < sizeof(*value))
        return LR_BUFFER_TOO_SMALL;
    memset(value, 0, sizeof(*value));
    value->struct_size = (uint32_t)sizeof(*value);
    value->abi_version = LR_ABI_VERSION;
    value->purpose = LR_PURPOSE_SHARE;
    value->codec = LR_CODEC_HEVC;
    return LR_OK;
}

lr_status lr_resolved_format_init(lr_resolved_format *value, uint64_t capacity)
{
    if (value == NULL)
        return LR_INVALID_ARGUMENT;
    if (capacity < sizeof(*value))
        return LR_BUFFER_TOO_SMALL;
    memset(value, 0, sizeof(*value));
    value->struct_size = (uint32_t)sizeof(*value);
    value->abi_version = LR_ABI_VERSION;
    return LR_OK;
}

lr_status lr_error_init(lr_error *value, uint64_t capacity)
{
    if (value == NULL)
        return LR_INVALID_ARGUMENT;
    if (capacity < sizeof(*value))
        return LR_BUFFER_TOO_SMALL;
    memset(value, 0, sizeof(*value));
    value->struct_size = (uint32_t)sizeof(*value);
    return LR_OK;
}

lr_status lr_hdr10plus_metadata_init(lr_hdr10plus_metadata *value, uint64_t capacity)
{
    if (value == NULL)
        return LR_INVALID_ARGUMENT;
    if (capacity < sizeof(*value))
        return LR_BUFFER_TOO_SMALL;
    memset(value, 0, sizeof(*value));
    value->struct_size = (uint32_t)sizeof(*value);
    value->abi_version = LR_ABI_VERSION;
    return LR_OK;
}

/* All address arithmetic is performed only after the entire view is proven to
 * fit its backing allocation. In particular INT64_MIN cannot be negated. */
lr_status lr_validate_plane(const lr_plane *plane)
{
    uint64_t step, extent;
    if (plane == NULL)
        return LR_INVALID_ARGUMENT;
    if (plane->width_bytes == 0 || plane->height == 0)
        return LR_OK;
    if (plane->data == NULL || plane->size > SIZE_MAX || plane->stride == INT64_MIN ||
        plane->size > UINTPTR_MAX - (uintptr_t)plane->data)
        return LR_INVALID_ARGUMENT;
    step = (uint64_t)(plane->stride < 0 ? -plane->stride : plane->stride);
    if (plane->height > 1 && step < plane->width_bytes)
        return LR_INVALID_ARGUMENT;
    if (plane->height > 1 && step > UINT64_MAX / (plane->height - 1))
        return LR_INVALID_ARGUMENT;
    extent = step * (plane->height - 1);
    if (plane->origin > plane->size || plane->width_bytes > plane->size - plane->origin)
        return LR_BUFFER_TOO_SMALL;
    if (plane->stride < 0)
        return extent <= plane->origin ? LR_OK : LR_BUFFER_TOO_SMALL;
    return extent <= plane->size - plane->origin - plane->width_bytes ? LR_OK : LR_BUFFER_TOO_SMALL;
}

lr_status lr_copy_plane(const lr_plane *source, const lr_plane *destination)
{
    uint64_t row, source_offset, destination_offset;
    uintptr_t source_address, destination_address;
    lr_status status = lr_validate_plane(source);
    if (status != LR_OK)
        return status;
    status = lr_validate_plane(destination);
    if (status != LR_OK)
        return status;
    if (source->width_bytes != destination->width_bytes || source->height != destination->height)
        return LR_INVALID_ARGUMENT;
    if (source->width_bytes == 0 || source->height == 0)
        return LR_OK;
    if (source->data == destination->data && source->origin == destination->origin &&
        source->stride == destination->stride)
        return LR_OK;
    source_address = (uintptr_t)source->data;
    destination_address = (uintptr_t)destination->data;
    /* Compare integer intervals without pointer subtraction across objects or
     * wrapping an end address. Conservative overlap rejection also handles
     * opposite strides and views with different origins in one allocation. */
    if ((source_address <= destination_address && destination_address - source_address < source->size) ||
        (destination_address < source_address && source_address - destination_address < destination->size))
        return LR_INVALID_ARGUMENT;
    source_offset = source->origin;
    destination_offset = destination->origin;
    for (row = 0; row < source->height; ++row)
    {
        memcpy(destination->data + (size_t)destination_offset, source->data + (size_t)source_offset,
               (size_t)source->width_bytes);
        if (row + 1 < source->height)
        {
            if (source->stride < 0)
                source_offset -= (uint64_t)(-source->stride);
            else
                source_offset += (uint64_t)source->stride;
            if (destination->stride < 0)
                destination_offset -= (uint64_t)(-destination->stride);
            else
                destination_offset += (uint64_t)destination->stride;
        }
    }
    return LR_OK;
}
