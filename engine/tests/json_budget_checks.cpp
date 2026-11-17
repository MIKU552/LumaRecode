#include "probe_json.hpp"

#include <cmath>
#include <iostream>
#include <string>

int main()
{
    try
    {
        luma::probe_json limited(4096);
        const std::string large = "[" + std::string(10000, ' ') + "\"" + std::string(8192, 'x') + "\"]";
        bool rejected = false;
        try
        {
            limited.parse({reinterpret_cast<const uint8_t *>(large.data()), large.size()});
        }
        catch (const luma::probe_failure &failure)
        {
            rejected = failure.status == LR_RESOURCE_LIMIT;
        }
        if (!rejected || limited.peak_bytes() > 4096)
            return 1;
        if (luma::probe_number("1e-999", 7) != 0 || luma::probe_number("1e999", 7) != 7 ||
            luma::probe_number("+-1", 7) != 7 || luma::probe_number(" +3 / 2 ", 7) != 1.5 ||
            luma::probe_number("1/0", 7) != 7 || luma::probe_number("NaN", 7) != 7 ||
            !std::signbit(luma::probe_number("-1e-999", 7)))
            return 1;
        std::cout << "JSON allocator budget and finite numeric contracts passed.\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
