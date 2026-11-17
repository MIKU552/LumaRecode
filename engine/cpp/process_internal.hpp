#ifndef LUMA_PROCESS_INTERNAL_HPP
#define LUMA_PROCESS_INTERNAL_HPP

#include "lumarecode/process.h"

#include <chrono>
#include <exception>
#include <span>
#include <string>
#include <vector>

namespace luma
{
struct process_request
{
    std::string executable;
    std::string directory;
    std::vector<std::string> arguments;
};

class process_failure final : public std::exception
{
  public:
    process_failure(lr_status status, std::string message) : status(status), message(std::move(message)) {}
    const char *what() const noexcept override
    {
        return message.c_str();
    }
    lr_status status;
    std::string message;
};

/* The platform driver invokes this sink on run()'s thread. The implementation
 * synchronizes snapshots internally; no callback enters a UI or managed host. */
struct process_sink
{
    virtual ~process_sink() = default;
    virtual void started() noexcept = 0;
    virtual void cleaned() noexcept = 0;
    virtual void check_stop() = 0;
    virtual void consume(uint32_t stream, std::span<const uint8_t> bytes, bool cleaning = false) = 0;
    virtual void exited(int32_t code) = 0;
};

void execute_child(const process_request &request, process_sink &sink);
[[noreturn]] inline void fail_os(const char *operation, unsigned long code)
{
    throw process_failure(LR_IO_ERROR, std::string(operation) + " failed (" + std::to_string(code) + ").");
}
} // namespace luma
#endif
