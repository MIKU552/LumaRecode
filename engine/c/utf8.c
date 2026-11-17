#include "lumarecode/process.h"

/* Validate scalar values directly: reject overlong forms, UTF-16 surrogates,
 * stray continuations and code points beyond U+10FFFF. No locale is consulted. */
lr_status lr_utf8_validate(const uint8_t *data, uint64_t size)
{
    uint64_t offset = 0;
    if (size > SIZE_MAX || (data == NULL && size != 0))
    {
        return LR_INVALID_ARGUMENT;
    }
    while (offset < size)
    {
        uint32_t scalar;
        uint32_t remaining;
        uint32_t minimum;
        uint8_t first = data[offset++];
        if (first < 0x80)
        {
            continue;
        }
        if (first >= 0xc2 && first <= 0xdf)
        {
            scalar = first & 0x1f;
            remaining = 1;
            minimum = 0x80;
        }
        else if (first >= 0xe0 && first <= 0xef)
        {
            scalar = first & 0x0f;
            remaining = 2;
            minimum = 0x800;
        }
        else if (first >= 0xf0 && first <= 0xf4)
        {
            scalar = first & 0x07;
            remaining = 3;
            minimum = 0x10000;
        }
        else
        {
            return LR_INVALID_DATA;
        }
        if (remaining > size - offset)
        {
            return LR_INVALID_DATA;
        }
        while (remaining-- != 0)
        {
            uint8_t continuation = data[offset++];
            if ((continuation & 0xc0) != 0x80)
            {
                return LR_INVALID_DATA;
            }
            scalar = (scalar << 6) | (continuation & 0x3f);
        }
        if (scalar < minimum || scalar > 0x10ffff || (scalar >= 0xd800 && scalar <= 0xdfff))
        {
            return LR_INVALID_DATA;
        }
    }
    return LR_OK;
}
