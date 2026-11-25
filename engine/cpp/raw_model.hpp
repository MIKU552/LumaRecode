#ifndef LUMA_RAW_MODEL_HPP
#define LUMA_RAW_MODEL_HPP
#include "media_model.hpp"
#include <array>
namespace luma
{
std::array<int64_t, 4> raw_crop(probe_json &json, json_value *raw);
std::vector<std::string> raw_orientation(probe_json &json, json_value *media);
std::string raw_filter(probe_json &json, json_value *raw, uint32_t preview_size);
} // namespace luma
#endif
