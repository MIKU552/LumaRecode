#include "../cpp/process_internal.hpp"

#include <cstdio>
#include <filesystem>
#include <memory>
#include <new>

/* A platform-driver substitute exercises outcomes that cannot reliably be
 * induced on hosted machines: launch failures and incomplete OS cleanup. Real
 * process, pipe and descendant behavior is tested by process_contracts. */
void luma::execute_child(const process_request &request, process_sink &sink)
{
    const auto &mode = request.arguments.at(0);
    if (mode == "launch-failure")
    {
        throw process_failure(LR_IO_ERROR, "Synthetic launch failure.");
    }
    sink.started();
    if (mode == "unclean-cancel")
    {
        throw process_failure(LR_CANCELLED, "Synthetic cancellation.");
    }
    if (mode == "unclean-memory")
    {
        throw std::bad_alloc();
    }
    sink.exited(mode == "nonzero" ? 17 : 0);
    if (mode == "unclean-success")
    {
        return;
    }
    sink.cleaned();
    if (mode == "cancelled")
    {
        throw process_failure(LR_CANCELLED, "Synthetic cancellation.");
    }
}

int main()
{
    struct expected_case
    {
        const char *mode;
        lr_status status;
        uint32_t cleanup;
    };
    const expected_case cases[] = {{"launch-failure", LR_IO_ERROR, LR_CLEANUP_CONFIRMED},
                                   {"unclean-success", LR_IO_ERROR, LR_CLEANUP_FAILED},
                                   {"unclean-cancel", LR_IO_ERROR, LR_CLEANUP_FAILED},
                                   {"unclean-memory", LR_IO_ERROR, LR_CLEANUP_FAILED},
                                   {"nonzero", LR_IO_ERROR, LR_CLEANUP_CONFIRMED},
                                   {"cancelled", LR_CANCELLED, LR_CLEANUP_CONFIRMED},
                                   {"success", LR_OK, LR_CLEANUP_CONFIRMED}};
    const auto path = std::filesystem::absolute("synthetic-process-driver").u8string();
    for (const auto &expected : cases)
    {
        lr_process_options options;
        lr_process_options_init(&options, sizeof(options));
        options.executable = {reinterpret_cast<const uint8_t *>(path.data()), path.size()};
        lr_utf8_view argument{reinterpret_cast<const uint8_t *>(expected.mode),
                              std::char_traits<char>::length(expected.mode)};
        options.arguments = &argument;
        options.argument_count = 1;
        lr_process *raw = nullptr;
        lr_error error;
        lr_error_init(&error, sizeof(error));
        if (lr_process_create(&options, &raw, &error) != LR_OK)
        {
            return 1;
        }
        const std::unique_ptr<lr_process, decltype(&lr_process_release)> process(raw, lr_process_release);
        const auto status = lr_process_run(raw, &error);
        lr_process_snapshot state;
        lr_process_snapshot_init(&state, sizeof(state));
        if (lr_process_get_snapshot(raw, &state) != LR_OK || state.state != LR_PROCESS_FINISHED ||
            status != expected.status || state.status != expected.status ||
            state.cleanup_state != expected.cleanup)
        {
            std::fprintf(stderr, "Lifecycle contract failed: %s, status=%d, cleanup=%u\n", expected.mode,
                         status, state.cleanup_state);
            return 1;
        }
    }
    std::puts("Process launch, terminal-state and cleanup-failure contracts passed.");
    return 0;
}
