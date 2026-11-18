#ifndef LUMA_TEST_PROBE_FIXTURE_HPP
#define LUMA_TEST_PROBE_FIXTURE_HPP

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

/* A shell-free ffprobe substitute for orchestration failures. The input file
 * names a scenario; JSON reports deliberately contain covers and conflicting
 * metadata so tests exercise selection and merge behavior, not just parsing. */
inline int probe_fixture(const std::vector<std::string> &arguments)
{
    const auto has = [&](const char *name)
    { return std::find(arguments.begin(), arguments.end(), name) != arguments.end(); };
    if (has("-show_pixel_formats"))
    {
        const std::string report =
            R"({"pixel_formats":[{"name":"yuv420p10le","nb_components":3,"log2_chroma_w":1,"log2_chroma_h":1,"flags":{"rgb":false,"alpha":false},"components":[{"bit_depth":10},{"bit_depth":10},{"bit_depth":10}]}],"padding":")" +
            std::string(4096, 'p') + "\"}";
        std::fputs(report.c_str(), stdout);
        return 0;
    }
    const auto input = std::find(arguments.begin(), arguments.end(), "-i");
    if (input != arguments.end() && std::next(input) == arguments.end())
        return 92;
    const auto &name = input == arguments.end() ? arguments.back() : *std::next(input);
    const auto file = std::filesystem::path(std::u8string(name.begin(), name.end()));
    std::string mode;
    {
        std::ifstream source(file);
        std::getline(source, mode);
    }
    const auto wait = []
    {
        while (true)
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
    };
    if (has("-show_streams"))
    {
        if (mode == "wait")
            wait();
        if (mode == "invalid_json")
        {
            std::fputs("{invalid", stdout);
            return 0;
        }
        if (mode == "stream_failure")
        {
            std::fputs("fixture-stream-error", stderr);
            return 19;
        }
        if (mode == "large_json")
        {
            std::fputs(("{\"padding\":\"" + std::string(8192, 'x') + "\"}").c_str(), stdout);
            return 0;
        }
        if (mode == "deep_json")
        {
            std::fputs((std::string(70, '[') + "0" + std::string(70, ']')).c_str(), stdout);
            return 0;
        }
        std::string streams =
            R"({"index":0,"codec_type":"video","codec_name":"mjpeg","disposition":{"attached_pic":1}},)";
        streams +=
            R"({"index":3,"codec_type":"audio","codec_name":"pcm_s24le","channels":2,"bits_per_sample":24},)";
        streams += R"({"index":6,"codec_type":"audio","codec_name":"aac","channels":2})";
        if (mode != "no_video")
        {
            streams +=
                R"(,{"index":1,"codec_type":"video","codec_name":"hevc","pix_fmt":"yuv420p10le","width":128,"height":72,"duration":"3.25","color_transfer":)";
            streams += mode == "explicit" || mode == "dovi_video" ? "\"bt709\"" : "\"unknown\"";
            streams +=
                R"(,"color_primaries":"reserved","color_space":"unspecified","color_range":"","tags":)";
            streams +=
                mode == "merge" ? R"({"gamma":"SamsungLog"})" : R"({"shared":"video","model":"fixture"})";
            if (mode == "dovi_video")
                streams +=
                    R"(,"side_data_list":[{"side_data_type":"DOVI configuration record","dv_profile":5,"dv_bl_signal_compatibility_id":0}])";
            streams += "}";
        }
        if (mode == "raw")
            streams +=
                R"(,{"index":4,"codec_type":"video","codec_name":"prores_raw","pix_fmt":"bayer_bggr16le","duration":"3.25"})";
        if (mode == "dji" || mode == "dji_wait")
            streams += R"(,{"index":2,"codec_type":"data","codec_tag_string":"djmd"})";
        const auto duration = mode == "underflow"     ? "1e-999"
                              : mode == "overflow"    ? "1e999"
                              : mode == "double_sign" ? "+-1"
                                                      : "2/1";
        std::string tags = mode == "merge" ? R"({"gamma":"MiLog","log_profile":"DLogM"})"
                                           : R"({"shared":"format","comment":"fixture"})";
        const auto output = "{\"streams\":[" + streams + "],\"format\":{\"duration\":\"" + duration +
                            "\",\"start_time\":\"-0.25\",\"tags\":" + tags + "}}";
        std::fputs(output.c_str(), stdout);
        return 0;
    }
    if (has("-show_frames"))
    {
        if (mode == "frame_wait")
            wait();
        if (mode == "frame_failure" || mode == "raw")
        {
            std::fputs("fixture-frame-error", stderr);
            return 21;
        }
        if (mode == "source_changed")
        {
            std::ofstream changed(file, std::ios::app);
            changed << "changed";
        }
        std::string frame =
            R"({"color_transfer":"arib-std-b67","color_primaries":"bt2020","color_space":"bt2020nc","color_range":"tv")";
        if (mode == "dovi" || mode == "dovi_video")
            frame +=
                R"(,"side_data_list":[{"side_data_type":"DOVI configuration record","dv_profile":8,"dv_bl_signal_compatibility_id":4},{"side_data_type":"HDR10+ Dynamic Metadata","maxscl":1,"maxscl":2,"maxscl":3,"nested":[1],"nested":[2]}])";
        frame += "}";
        std::fputs(("{\"frames\":[" + frame + "]}").c_str(), stdout);
        return 0;
    }
    if (has("-show_packets"))
    {
        if (mode == "dji_wait")
            wait();
        const auto field = [](uint8_t key, const std::vector<uint8_t> &data)
        {
            std::vector<uint8_t> output{static_cast<uint8_t>(key * 8 + 2), static_cast<uint8_t>(data.size())};
            output.insert(output.end(), data.begin(), data.end());
            return output;
        };
        const std::string schema = "dvtm_PP-101.proto";
        auto packet = field(1, field(1, field(1, {schema.begin(), schema.end()})));
        const auto color = field(2, field(4, {8, 19}));
        packet.insert(packet.end(), color.begin(), color.end());
        constexpr char hex[] = "0123456789abcdef";
        std::string data;
        for (auto byte : packet)
        {
            data += hex[byte >> 4];
            data += hex[byte & 15];
        }
        const auto output = "{\"packets\":[{\"size\":" + std::to_string(packet.size()) +
                            ",\"data\":\"00000000: " + data + "  ASCII\"}]}";
        std::fputs(output.c_str(), stdout);
        return 0;
    }
    return 93;
}
#endif
