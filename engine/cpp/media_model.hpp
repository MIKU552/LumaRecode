#ifndef LUMA_MEDIA_MODEL_HPP
#define LUMA_MEDIA_MODEL_HPP
#include "lumarecode/media.h"
#include "probe_json.hpp"
#include <vector>

namespace luma
{
using json_value = yyjson_mut_val;
std::string field(probe_json &json, json_value *object, const char *key, const char *fallback = "");
int64_t int_field(probe_json &json, json_value *object, const char *key, int64_t fallback = 0);
bool flag_field(probe_json &json, json_value *object, const char *key);
std::vector<json_value *> objects(json_value *array);
std::vector<json_value *> side_data(json_value *media);
std::vector<json_value *> audio_streams(probe_json &json, json_value *media);
bool media_rgb(probe_json &json, json_value *media);
bool media_raw(probe_json &json, json_value *media);
lr_source_format source_format(probe_json &json, json_value *media,
                               lr_color_range profile_range = LR_RANGE_AUTO);
uint32_t flac_depth(probe_json &json, json_value *stream);
std::string sampling_name(lr_sampling sampling);
} // namespace luma
#endif
