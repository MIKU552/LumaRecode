#include "raw_model.hpp"
#include <cmath>
#include <regex>

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
