#include "lumarecode/media.h"

static int whitespace(uint8_t value)
{
    return value == ' ' || (value >= '\t' && value <= '\r');
}
static int digit(uint8_t value)
{
    if (value >= '0' && value <= '9')
        return value - '0';
    if (value >= 'a' && value <= 'f')
        return value - 'a' + 10;
    if (value >= 'A' && value <= 'F')
        return value - 'A' + 10;
    return -1;
}
/* Count first, then decode exactly the same byte ranges. Offset numbers and
 * ASCII previews never become payload, even when they contain hex digits. */
static lr_status visit(lr_utf8_view dump, uint8_t *output, uint64_t *count)
{
    uint64_t start = 0, nibbles = 0;
    int high = 0;
    while (start < dump.size)
    {
        uint64_t end = start, colon, position;
        while (end < dump.size && dump.data[end] != '\n')
            ++end;
        colon = start;
        while (colon < end && dump.data[colon] != ':')
            ++colon;
        position = colon < end ? colon + 1 : end;
        while (position < end && whitespace(dump.data[position]))
            ++position;
        for (; position < end; ++position)
        {
            int value;
            if (dump.data[position] == ' ' && position + 1 < end && dump.data[position + 1] == ' ')
                break;
            if (whitespace(dump.data[position]))
                continue;
            value = digit(dump.data[position]);
            if (value < 0)
                return LR_INVALID_DATA;
            if ((nibbles & 1) == 0)
                high = value << 4;
            else if (output != NULL)
                output[nibbles / 2] = (uint8_t)(high | value);
            ++nibbles;
            if (nibbles > 2 * 1024 * 1024)
                return LR_RESOURCE_LIMIT;
        }
        start = end < dump.size ? end + 1 : end;
    }
    if ((nibbles & 1) != 0)
        return LR_INVALID_DATA;
    *count = nibbles / 2;
    return LR_OK;
}

lr_status lr_probe_decode_hex(lr_utf8_view dump, uint8_t *destination, uint64_t capacity, uint64_t *required)
{
    uint64_t size = 0;
    lr_status status;
    uintptr_t source_address = (uintptr_t)dump.data, destination_address = (uintptr_t)destination;
    if (required == NULL || (dump.size != 0 && dump.data == NULL) || (capacity != 0 && destination == NULL) ||
        dump.size > SIZE_MAX || capacity > SIZE_MAX || dump.size > UINTPTR_MAX - source_address ||
        capacity > UINTPTR_MAX - destination_address)
        return LR_INVALID_ARGUMENT;
    if (dump.size > 16 * 1024 * 1024)
        return LR_RESOURCE_LIMIT;
    if (dump.size != 0 && capacity != 0 &&
        ((source_address <= destination_address && destination_address - source_address < dump.size) ||
         (destination_address < source_address && source_address - destination_address < capacity)))
        return LR_INVALID_ARGUMENT;
    status = visit(dump, NULL, &size);
    if (status != LR_OK)
        return status;
    *required = size;
    if (capacity < size)
        return LR_BUFFER_TOO_SMALL;
    return visit(dump, destination, &size);
}
