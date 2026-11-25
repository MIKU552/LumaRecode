#include "raw_model.hpp"
#include "lumarecode/media.h"
#include <bit>
#include <cmath>
#include <cstring>
#include <regex>

extern "C" lr_status lr_media_raw_calibration(const uint8_t *header, uint64_t header_size, uint64_t packet_size,
                                                lr_raw_calibration *result)
{
    if (header == nullptr || result == nullptr || header_size < 86 || packet_size < 86)
        return LR_INVALID_ARGUMENT;
    // The probe retains only the calibration header. Compare its declaration
    // with the complete packet length before reading the big-endian fields.
    const auto u16 = [&](size_t offset) -> uint32_t {
        return (static_cast<uint32_t>(header[offset]) << 8) | header[offset + 1];
    };
    const auto u32 = [&](size_t offset) { return (u16(offset) << 16) | u16(offset + 2); };
    if (u32(0) != packet_size || std::memcmp(header + 4, "prrf", 4) != 0 || u16(8) < 78 ||
        u16(8) > packet_size - 8 || header[11] > 1)
        return LR_INVALID_DATA;
    lr_raw_calibration values{};
    values.width = u16(16);
    values.height = u16(18);
    for (size_t i = 0; i < 4; ++i)
        values.crop[i] = header[20 + i];
    values.white_balance_kelvin = u16(76);
    values.bayer_pattern = u16(24) & 3;
    const auto sensor_range = u16(26);
    if ((values.width & 1) != 0 || (values.height & 1) != 0 ||
        values.crop[0] + values.crop[1] >= values.width || values.crop[2] + values.crop[3] >= values.height ||
        sensor_range == 0 || sensor_range > 65279)
        return LR_INVALID_DATA;

    std::array<double, 12> coefficients{};
    for (size_t i = 0; i < coefficients.size(); ++i)
    {
        coefficients[i] = std::bit_cast<float>(u32(28 + i * 4));
        if (!std::isfinite(coefficients[i]))
            return LR_INVALID_DATA;
    }
    const auto &c = coefficients;
    // The camera's calibration supplies an RGB-to-XYZ matrix and per-channel
    // gains. Reject singular calibrations before composing the Rec.2020 matrix.
    const double determinant = c[2] * (c[6] * c[10] - c[7] * c[9]) -
                               c[3] * (c[5] * c[10] - c[7] * c[8]) +
                               c[4] * (c[5] * c[9] - c[6] * c[8]);
    if (c[0] <= 0 || c[1] <= 0 || c[11] <= 0 || std::abs(determinant) < 1e-9)
        return LR_INVALID_DATA;
    constexpr double xyz_to_2020[3][3]{
        {1.716651187971268, -0.355670783776392, -0.253366281373660},
        {-0.666684351832489, 1.616481236634939, 0.015768545813911},
        {0.017639857445311, -0.042770613257809, 0.942103121235474}};
    for (size_t row = 0; row < 3; ++row)
        for (size_t column = 0; column < 3; ++column)
        {
            double value = 0;
            for (size_t k = 0; k < 3; ++k)
                value += xyz_to_2020[row][k] * c[2 + k * 3 + column];
            value *= (column == 0 ? c[0] : column == 2 ? c[1] : 1) * 65535 / sensor_range * c[11] / 1024;
            if (!std::isfinite(value) || std::abs(value) > 2)
                return LR_INVALID_DATA;
            values.matrix[row * 4 + column] = value;
            values.matrix[row * 4 + 3] -= 256.0 / 65535 * value;
        }
    *result = values;
    return LR_OK;
}

namespace luma
{
std::array<int64_t, 4> raw_crop(probe_json &json, json_value *raw)
{
    const auto array = member(raw, "crop");
    if (yyjson_mut_arr_size(array) != 4)
        throw probe_failure(LR_INVALID_DATA, "RAW crop requires four integer offsets.");
    std::array<int64_t, 4> result{};
    for (size_t i = 0; i < 4; ++i)
    {
        const auto value = probe_number(json.text(yyjson_mut_arr_get(array, i)), -1);
        if (!std::isfinite(value) || value < 0 || value > 65535 || std::floor(value) != value)
            throw probe_failure(LR_INVALID_DATA, "Invalid RAW crop offset.");
        result[i] = static_cast<int64_t>(value);
    }
    if (int_field(json, raw, "width") <= result[0] + result[1] ||
        int_field(json, raw, "height") <= result[2] + result[3])
        throw probe_failure(LR_INVALID_DATA, "Invalid RAW crop dimensions.");
    return result;
}
std::vector<std::string> raw_orientation(probe_json &json, json_value *media)
{
    for (const auto side : side_data(media))
    {
        if (field(json, side, "side_data_type") != "Display Matrix")
            continue;
        const auto text = field(json, side, "displaymatrix");
        static const std::regex rows(R"(^\s*[0-9a-fA-F]+:\s*(-?\d+)\s+(-?\d+)\s+(-?\d+))",
                                     std::regex_constants::multiline);
        std::vector<int> signs;
        for (auto at = std::sregex_iterator(text.begin(), text.end(), rows); at != std::sregex_iterator();
             ++at)
            for (size_t i = 1; i <= 3; ++i)
            {
                const auto value = probe_number((*at)[i].str(), NAN);
                if (!std::isfinite(value))
                    throw probe_failure(LR_INVALID_DATA, "Invalid RAW display matrix.");
                signs.push_back(value < 0 ? -1 : value > 0 ? 1 : 0);
            }
        if (signs.size() != 9)
            throw probe_failure(LR_INVALID_DATA, "RAW display matrix requires nine entries.");
        const std::array<int, 4> axes{signs[0], signs[1], signs[3], signs[4]};
        if (axes == std::array<int, 4>{1, 0, 0, 1})
            return {};
        if (axes == std::array<int, 4>{-1, 0, 0, 1})
            return {"hflip"};
        if (axes == std::array<int, 4>{1, 0, 0, -1})
            return {"vflip"};
        if (axes == std::array<int, 4>{-1, 0, 0, -1})
            return {"hflip", "vflip"};
        if (axes == std::array<int, 4>{0, -1, 1, 0})
            return {"transpose=cclock"};
        if (axes == std::array<int, 4>{0, 1, -1, 0})
            return {"transpose=clock"};
        if (axes == std::array<int, 4>{0, 1, 1, 0})
            return {"transpose=cclock_flip"};
        if (axes == std::array<int, 4>{0, -1, -1, 0})
            return {"transpose=clock_flip"};
        throw probe_failure(LR_UNSUPPORTED, "RAW orientation requires an orthogonal display transform.");
    }
    return {};
}
std::string raw_filter(probe_json &json, json_value *raw, uint32_t preview_size)
{
    const auto crop = raw_crop(json, raw);
    const auto matrix = member(raw, "matrix");
    if (yyjson_mut_arr_size(matrix) != 12)
        throw probe_failure(LR_INVALID_DATA, "RAW calibration requires a 3x4 matrix.");
    std::string coefficients;
    for (size_t i = 0; i < 12; ++i)
    {
        const auto value = yyjson_mut_arr_get(matrix, i);
        const auto numeric = probe_number(json.text(value), NAN);
        if (!std::isfinite(numeric) || std::abs(numeric) > 2)
            throw probe_failure(LR_INVALID_DATA, "Invalid RAW calibration coefficient.");
        if (i > 0)
            coefficients += ':';
        coefficients += "rgb"[i / 4];
        coefficients += "rgba"[i % 4];
        coefficients += '=';
        coefficients += json.text(value);
    }
    const auto crop_filter = "crop=" + std::to_string(int_field(json, raw, "width") - crop[0] - crop[1]) +
                             ":" + std::to_string(int_field(json, raw, "height") - crop[2] - crop[3]) + ":" +
                             std::to_string(crop[0]) + ":" + std::to_string(crop[2]) + ":exact=1";
    std::string result = "format=gbrapf32le,";
    if (preview_size > 0)
    {
        const auto n = std::to_string(preview_size);
        result += crop_filter + ",scale=w='min(iw," + n + ")':h='min(ih," + n +
                  ")':force_original_aspect_ratio=decrease:force_divisible_by=2,";
    }
    result +=
        "sendcmd=f=raw-calibration.cmd,colorchannelmixer@raw=" + coefficients + ",format=gbrpf32le,geq=";
    for (unsigned channel = 0; channel < 3; ++channel)
    {
        if (channel != 0)
            result += ':';
        result += "rgb"[channel];
        result += "='st(0,r(X,Y)*1024);st(1,g(X,Y)*1024);st(2,b(X,Y)*1024);"
                  "st(3,max(0,0.2627*ld(0)+0.6780*ld(1)+0.0593*ld(2)));st(4,min(ld(0),min(ld(1),ld(2))));"
                  "st(5,if(lt(ld(4),0),if(gt(ld(3),0),ld(3)/(ld(3)-ld(4)),0),1));"
                  "st(0,max(0,ld(3)+ld(5)*(ld(0)-ld(3)))*0.2674074074074074);"
                  "st(1,max(0,ld(3)+ld(5)*(ld(1)-ld(3)))*0.2674074074074074);"
                  "st(2,max(0,ld(3)+ld(5)*(ld(2)-ld(3)))*0.2674074074074074);"
                  "st(3,max(ld(0),max(ld(1),ld(2))));"
                  "st(4,if(lte(ld(3),0.45),1,(0.45+0.55*(1-exp(-(ld(3)-0.45)/0.55)))/ld(3)));st(5,ld(";
        result +=
            std::to_string(channel) +
            ")*ld(4));if(lte(ld(5),1/12),sqrt(3*ld(5)),0.17883277*log(12*ld(5)-0.28466892)+0.55991073)'";
    }
    if (preview_size == 0)
        result += ',' + crop_filter;
    return result + ",setparams=range=full:color_primaries=bt2020:color_trc=arib-std-b67:colorspace=gbr,"
                    "zscale=matrix=bt2020nc:range=limited,format=yuv444p16le";
}
} // namespace luma
