#ifndef LUMA_TEST_TIMELINE_FIXTURE_HPP
#define LUMA_TEST_TIMELINE_FIXTURE_HPP
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

inline int timeline_fixture(const std::vector<std::string> &arguments)
{
    std::string mode;
    std::ifstream source(
        std::filesystem::path(std::u8string(arguments.back().begin(), arguments.back().end())));
    std::getline(source, mode);
    const bool packets = std::find(arguments.begin(), arguments.end(), "-show_packets") != arguments.end();
    if (mode == "wait")
        while (true)
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
    if (mode == "error")
    {
        std::fputs("timeline-fixture-failure", stderr);
        return 19;
    }
    if (mode == "longline")
        std::fputs(std::string(65537, 'x').c_str(), stdout);
    else if (mode == "empty")
        return 0;
    else if (mode == "overflow")
        std::fputs(packets ? "packet|pts=9223372036854775808\n" : "frame|best_effort_timestamp=N/A\n",
                   stdout);
    else if (mode == "many")
    {
        for (int i = 0; i < 50000; ++i)
            std::printf(packets ? "packet|pts=%d|duration=1|flags=K_\n"
                                : "frame|best_effort_timestamp=%d|duration=1|key_frame=1\n",
                        i);
    }
    else if (packets)
    {
        if (mode == "missing")
            std::fputs("packet|pts=N/A\n", stdout);
        else if (mode == "duplicate")
            std::fputs("packet|pts=0\npacket|pts=0\n", stdout);
        else
            std::fputs("packet|pts=-20|duration=10|flags=KD\r\npacket|pts=30|duration=15|flags=__\n"
                       "packet|pts=0|duration=10|flags=K_\rpacket|pts=10|duration=20|flags=__",
                       stdout);
    }
    else if (mode == "descending")
        std::fputs("frame|best_effort_timestamp=2\nframe|best_effort_timestamp=1\n", stdout);
    else
        std::fputs("frame|best_effort_timestamp=0|duration=10|key_frame=1\n"
                   "frame|best_effort_timestamp=10|duration=20|key_frame=0\n"
                   "frame|best_effort_timestamp=30|duration=15|key_frame=0",
                   stdout);
    return 0;
}
#endif
