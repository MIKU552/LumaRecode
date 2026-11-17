#include "lumarecode/core.h"

#include <algorithm>
#include <array>
#include <cstring>
#include <exception>
#include <string_view>

namespace
{
lr_status fail(lr_error *error, lr_status code, std::string_view message) noexcept
{
    if (error != nullptr && error->struct_size == sizeof(lr_error))
    {
        error->code = code;
        const auto size = std::min(message.size(), sizeof(error->message) - 1);
        std::memcpy(error->message, message.data(), size);
        error->message[size] = '\0';
    }
    return code;
}

std::array<unsigned, 2> shifts(lr_sampling sampling)
{
    switch (sampling)
    {
    case LR_SAMPLING_RGB:
    case LR_SAMPLING_444:
        return {0, 0};
    case LR_SAMPLING_422:
        return {1, 0};
    case LR_SAMPLING_420:
        return {1, 1};
    case LR_SAMPLING_440:
        return {0, 1};
    case LR_SAMPLING_411:
        return {2, 0};
    case LR_SAMPLING_410:
        return {2, 2};
    default:
        return {99, 99};
    }
}
} // namespace

extern "C" lr_status lr_resolve_format(const lr_source_format *source, const lr_encode_options *options,
                                       lr_resolved_format *result, lr_error *error)
{
    try
    {
        if (source == nullptr || options == nullptr || result == nullptr ||
            source->struct_size != sizeof(*source) || options->struct_size != sizeof(*options) ||
            result->struct_size != sizeof(*result) || source->abi_version != LR_ABI_VERSION ||
            options->abi_version != LR_ABI_VERSION || result->abi_version != LR_ABI_VERSION ||
            (error != nullptr && error->struct_size != sizeof(*error)))
            return fail(error, LR_INVALID_ARGUMENT, "Invalid structure size or ABI version.");
        if (source->bit_depth == 0 || source->bit_depth > 64 || source->sampling < LR_SAMPLING_420 ||
            source->sampling > LR_SAMPLING_410 || source->color_range > LR_RANGE_FULL ||
            (source->flags & ~(LR_SOURCE_ALPHA | LR_SOURCE_RAW)) != 0 ||
            source->dynamic_range < LR_DYNAMIC_RANGE_SDR || source->dynamic_range > LR_DYNAMIC_RANGE_HDR ||
            options->purpose < LR_PURPOSE_SHARE || options->purpose > LR_PURPOSE_ARCHIVE ||
            options->codec < LR_CODEC_HEVC || options->codec > LR_CODEC_AV1 ||
            options->dynamic_range > LR_DYNAMIC_RANGE_HDR || options->color_range > LR_RANGE_FULL)
            return fail(error, LR_INVALID_ARGUMENT, "Invalid source format or encoding option.");

        auto resolved = *result;
        resolved.dynamic_range =
            options->dynamic_range == LR_DYNAMIC_RANGE_AUTO ? source->dynamic_range : options->dynamic_range;
        if (resolved.dynamic_range == LR_DYNAMIC_RANGE_HDR && options->codec == LR_CODEC_AVC)
            return fail(error, LR_UNSUPPORTED, "HDR output requires HEVC or AV1.");
        // The representation of RGB and grayscale may change, but source color
        // precision limits remain authoritative. HDR output requires ten bits.
        const auto source_depth = std::max(8U, source->bit_depth);
        const auto maximum_depth = options->purpose == LR_PURPOSE_SHARE && options->codec == LR_CODEC_AVC ? 8U
                                   : options->purpose == LR_PURPOSE_ARCHIVE && options->codec == LR_CODEC_HEVC
                                       ? 12U
                                       : 10U;
        constexpr std::array<unsigned, 3> depths{8, 10, 12};
        unsigned selected = 0;
        for (auto depth : depths)
        {
            if (depth > source_depth || depth > maximum_depth ||
                (resolved.dynamic_range == LR_DYNAMIC_RANGE_HDR && depth < 10))
                continue;
            if (options->bit_depth != 0 && depth != options->bit_depth)
                continue;
            if (selected == 0 || options->purpose == LR_PURPOSE_ARCHIVE)
                selected = depth;
        }
        if (selected == 0)
            return fail(error, LR_UNSUPPORTED,
                        "Source precision and encoder cannot satisfy the requested bit depth.");
        resolved.bit_depth = selected;
        auto sampling = options->sampling;
        if (sampling == LR_SAMPLING_SOURCE && (source->flags & LR_SOURCE_ALPHA) != 0)
            return fail(error, LR_UNSUPPORTED,
                        "Select explicit output sampling to remove the alpha channel.");
        if (sampling == LR_SAMPLING_SOURCE)
            sampling = source->sampling == LR_SAMPLING_RGB ? LR_SAMPLING_444 : source->sampling;
        if (sampling == LR_SAMPLING_AUTO)
            sampling = options->purpose == LR_PURPOSE_SHARE || options->codec == LR_CODEC_AV1 ||
                               source->sampling == LR_SAMPLING_440
                           ? LR_SAMPLING_420
                       : source->sampling == LR_SAMPLING_RGB ? LR_SAMPLING_444
                                                             : source->sampling;
        if (sampling < LR_SAMPLING_420 || sampling > LR_SAMPLING_GRAY)
            return fail(error, LR_UNSUPPORTED, "Unsupported output chroma sampling.");
        if ((options->purpose == LR_PURPOSE_SHARE || options->codec == LR_CODEC_AV1) &&
            sampling != LR_SAMPLING_420)
            return fail(error, LR_UNSUPPORTED, "This encoding path requires 4:2:0.");
        const auto input_shifts = shifts(source->sampling);
        const auto output_shifts = shifts(sampling);
        if (source->sampling == LR_SAMPLING_GRAY)
        {
            if (sampling != LR_SAMPLING_GRAY && sampling != LR_SAMPLING_420)
                return fail(error, LR_UNSUPPORTED, "Grayscale supports neutral 4:2:0 or grayscale output.");
        }
        else if (output_shifts[0] < input_shifts[0] || output_shifts[1] < input_shifts[1] ||
                 sampling == LR_SAMPLING_GRAY)
            return fail(error, LR_UNSUPPORTED, "Output sampling would increase source chroma resolution.");
        resolved.sampling = sampling;
        resolved.color_range = options->color_range != LR_RANGE_AUTO  ? options->color_range
                               : options->purpose == LR_PURPOSE_SHARE ? LR_RANGE_LIMITED
                               : source->color_range != LR_RANGE_AUTO ? source->color_range
                               : source->sampling == LR_SAMPLING_RGB  ? LR_RANGE_FULL
                                                                      : LR_RANGE_LIMITED;
        *result = resolved;
        if (error != nullptr)
        {
            error->code = LR_OK;
            error->message[0] = '\0';
        }
        return LR_OK;
    }
    catch (const std::exception &exception)
    {
        return fail(error, LR_INTERNAL_ERROR, exception.what());
    }
    catch (...)
    {
        return fail(error, LR_INTERNAL_ERROR, "Unexpected native error.");
    }
}
