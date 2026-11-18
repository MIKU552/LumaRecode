#include "lumarecode/hdr10plus.h"

#include <cstring>
#include <limits>

// Private Rust entry. Only the validated public wrapper is exported by the
// shared library, so native exceptions and representation checks stay local.
extern "C" int32_t luma_hdr10plus_parse_t35(const uint8_t *, uint64_t, lr_hdr10plus_metadata *);

static_assert(sizeof(lr_hdr10plus_metadata) == 59 * sizeof(uint32_t));

extern "C" lr_status lr_hdr10plus_parse_t35(const uint8_t *payload, uint64_t length,
                                            lr_hdr10plus_metadata *result, lr_error *error)
{
    lr_status status;
    if (payload == nullptr || result == nullptr || length > 1048576 ||
        result->struct_size != sizeof(*result) || result->abi_version != LR_ABI_VERSION ||
        (error != nullptr && error->struct_size != sizeof(*error)))
        status = LR_INVALID_ARGUMENT;
    else
        status = luma_hdr10plus_parse_t35(payload, length, result);
    if (error != nullptr && error->struct_size == sizeof(*error))
    {
        error->code = status;
        const char *message = status == LR_OK ? ""
                              : status == LR_UNSUPPORTED
                                  ? "HDR10+ record contains fields the selected output cannot preserve."
                              : status == LR_NOT_FOUND ? "Payload is not an HDR10+ T.35 record."
                                                       : "Invalid HDR10+ record or argument.";
        std::strncpy(error->message, message, sizeof(error->message) - 1);
        error->message[sizeof(error->message) - 1] = '\0';
    }
    return status;
}
