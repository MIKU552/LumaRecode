#include "process_internal.hpp"

#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <spawn.h>
#include <sys/wait.h>
#include <unistd.h>

#include <array>
#include <cerrno>
#include <utility>

#ifdef __APPLE__
#include <crt_externs.h>
#include <libproc.h>
#include <sys/proc.h>
#else
extern char **environ;
#endif

namespace
{
class descriptor
{
  public:
    descriptor() = default;
    explicit descriptor(int value) : value(value) {}
    ~descriptor()
    {
        reset();
    }
    descriptor(const descriptor &) = delete;
    descriptor &operator=(const descriptor &) = delete;
    int get() const
    {
        return value;
    }
    explicit operator bool() const
    {
        return value != -1;
    }
    void reset(int replacement = -1)
    {
        if (*this)
        {
            close(value);
        }
        value = replacement;
    }

  private:
    int value = -1;
};

void checked(int status, const char *operation)
{
    if (status != 0)
    {
        luma::fail_os(operation, static_cast<unsigned long>(status));
    }
}

#ifdef __APPLE__
bool group_has_no_live_members(pid_t group, int observed_leader_exit)
{
    // Darwin excludes zombies when delivering a group signal and can return
    // EPERM when only zombies remain. Confirm quiescence before accepting that
    // result; an actual permission failure on a live member remains an error.
    std::array<pid_t, 256> members{};
    errno = 0;
    // This convenience API returns a PID count; proc_listpids returns bytes.
    const auto member_count = proc_listpgrppids(group, members.data(), static_cast<int>(sizeof(members)));
    if (member_count < 0 || member_count >= static_cast<int>(members.size()) ||
        (member_count == 0 && errno != 0 && errno != ESRCH))
    {
        return false;
    }
    for (size_t index = 0; index < static_cast<size_t>(member_count); ++index)
    {
        const auto pid = members[index];
        if (pid <= 0 || (pid == group && observed_leader_exit >= 0))
        {
            continue;
        }
        proc_bsdinfo information{};
        errno = 0;
        const auto count =
            proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &information, static_cast<int>(sizeof(information)));
        if (count == 0 && errno == ESRCH)
        {
            continue;
        }
        if (count != static_cast<int>(sizeof(information)) ||
            (information.pbi_pgid == static_cast<uint32_t>(group) && information.pbi_status != SZOMB))
        {
            return false;
        }
    }
    return true;
}
#endif

struct pipe_pair
{
    descriptor read;
    descriptor write;
    pipe_pair()
    {
        int values[2];
        if (pipe(values) == -1)
        {
            luma::fail_os("pipe", errno);
        }
        read.reset(values[0]);
        write.reset(values[1]);
        for (int value : values)
        {
            if (fcntl(value, F_SETFD, FD_CLOEXEC) == -1)
            {
                luma::fail_os("fcntl(FD_CLOEXEC)", errno);
            }
        }
        if (fcntl(read.get(), F_SETFL, O_NONBLOCK) == -1)
        {
            luma::fail_os("fcntl(O_NONBLOCK)", errno);
        }
    }
};

struct spawn_settings
{
    spawn_settings()
    {
        checked(posix_spawn_file_actions_init(&actions), "posix_spawn_file_actions_init");
        const auto code = posix_spawnattr_init(&attributes);
        if (code != 0)
        {
            posix_spawn_file_actions_destroy(&actions);
            checked(code, "posix_spawnattr_init");
        }
    }
    ~spawn_settings()
    {
        posix_spawnattr_destroy(&attributes);
        posix_spawn_file_actions_destroy(&actions);
    }
    posix_spawn_file_actions_t actions;
    posix_spawnattr_t attributes;
};

/* Keep the leader waitable until after group termination. WNOWAIT pins its PID
 * and prevents a recycled PID/process group from being targeted during cleanup.
 * The host must not install SIGCHLD=SIG_IGN or reap children it does not own. */
class child_group
{
  public:
    ~child_group()
    {
        if (pid > 0)
        {
            kill(-pid, SIGKILL);
            int status = 0;
            while (waitpid(pid, &status, 0) == -1 && errno == EINTR)
            {
            }
        }
    }
    int stop_and_join()
    {
        if (kill(-pid, SIGKILL) == -1)
        {
            const auto code = errno;
            bool quiescent = code == ESRCH;
#ifdef __APPLE__
            quiescent = quiescent || (code == EPERM && group_has_no_live_members(pid, observed_exit));
#endif
            if (!quiescent)
            {
                luma::fail_os("kill(process group)", code);
            }
        }
        int status = 0;
        pid_t waited;
        do
        {
            waited = waitpid(pid, &status, 0);
        } while (waited == -1 && errno == EINTR);
        if (waited == -1)
        {
            const auto code = errno;
            if (code == ECHILD)
            {
                pid = -1;
            }
            luma::fail_os("waitpid", code);
        }
        pid = -1;
        return WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
    }
    bool has_exited()
    {
        siginfo_t information{};
        if (waitid(P_PID, static_cast<id_t>(pid), &information, WEXITED | WNOHANG | WNOWAIT) == -1)
        {
            const auto code = errno;
            if (code == EINTR)
            {
                return false;
            }
            if (code == ECHILD)
            {
                pid = -1;
            }
            luma::fail_os("waitid", code);
        }
        // Darwin may report a stopped child even when WSTOPPED was omitted.
        // Only a terminal CLD_* event authorizes cleanup of this group.
        if (information.si_pid == pid &&
            (information.si_code == CLD_EXITED || information.si_code == CLD_KILLED ||
             information.si_code == CLD_DUMPED))
        {
            observed_exit =
                information.si_code == CLD_EXITED ? information.si_status : 128 + information.si_status;
            return true;
        }
        return false;
    }
    pid_t pid = -1;
    int observed_exit = -1;
};

bool drain(descriptor &pipe, uint32_t stream, luma::process_sink &sink, bool stopping = false)
{
    bool consumed = false;
    std::array<uint8_t, 8192> bytes;
    for (unsigned i = 0; pipe && i < 16; ++i)
    {
        if (!stopping)
        {
            sink.check_stop();
        }
        const auto count = read(pipe.get(), bytes.data(), bytes.size());
        if (count == 0)
        {
            pipe.reset();
            break;
        }
        if (count < 0)
        {
            if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)
            {
                break;
            }
            luma::fail_os("read(pipe)", errno);
        }
        sink.consume(stream, std::span(bytes.data(), static_cast<size_t>(count)));
        consumed = true;
    }
    return consumed;
}

void wait_for_data(const descriptor &output, const descriptor &errors)
{
    std::array<pollfd, 2> events{{{output.get(), POLLIN, 0}, {errors.get(), POLLIN, 0}}};
    if (poll(events.data(), events.size(), 5) == -1 && errno != EINTR)
    {
        luma::fail_os("poll", errno);
    }
}
} // namespace

void luma::execute_child(const process_request &request, process_sink &sink)
{
    pipe_pair output, errors;
    spawn_settings settings;
    checked(posix_spawn_file_actions_addopen(&settings.actions, STDIN_FILENO, "/dev/null", O_RDONLY, 0),
            "spawn stdin");
    checked(posix_spawn_file_actions_adddup2(&settings.actions, output.write.get(), STDOUT_FILENO),
            "spawn stdout");
    checked(posix_spawn_file_actions_adddup2(&settings.actions, errors.write.get(), STDERR_FILENO),
            "spawn stderr");
    if (!request.directory.empty())
    {
#ifdef __APPLE__
        checked(posix_spawn_file_actions_addchdir(&settings.actions, request.directory.c_str()), "spawn cwd");
#else
        checked(posix_spawn_file_actions_addchdir_np(&settings.actions, request.directory.c_str()),
                "spawn cwd");
#endif
    }
    sigset_t empty, defaults;
    sigemptyset(&empty);
    sigemptyset(&defaults);
    for (int signal : {SIGPIPE, SIGINT, SIGTERM, SIGCHLD})
    {
        sigaddset(&defaults, signal);
    }
    checked(posix_spawnattr_setsigmask(&settings.attributes, &empty), "spawn signal mask");
    checked(posix_spawnattr_setsigdefault(&settings.attributes, &defaults), "spawn signal defaults");
    checked(posix_spawnattr_setpgroup(&settings.attributes, 0), "spawn process group");
    short flags = POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSIGDEF;
#ifdef __APPLE__
    flags |= POSIX_SPAWN_CLOEXEC_DEFAULT;
#endif
    checked(posix_spawnattr_setflags(&settings.attributes, flags), "spawn flags");
    std::vector<char *> arguments;
    arguments.reserve(request.arguments.size() + 2);
    arguments.push_back(const_cast<char *>(request.executable.c_str()));
    for (const auto &argument : request.arguments)
    {
        arguments.push_back(const_cast<char *>(argument.c_str()));
    }
    arguments.push_back(nullptr);
#ifdef __APPLE__
    char **environment = *_NSGetEnviron();
#else
    char **environment = environ;
#endif
    child_group child;
    pid_t spawned = -1;
    sink.check_stop();
    checked(posix_spawn(&spawned, request.executable.c_str(), &settings.actions, &settings.attributes,
                        arguments.data(), environment),
            "posix_spawn");
    child.pid = spawned;
    output.write.reset();
    errors.write.reset();
    try
    {
        while (true)
        {
            sink.check_stop();
            const bool error_data = drain(errors.read, LR_STREAM_STDERR, sink);
            const bool output_data = drain(output.read, LR_STREAM_STDOUT, sink);
            if (child.has_exited())
            {
                break;
            }
            if (!error_data && !output_data)
            {
                wait_for_data(output.read, errors.read);
            }
        }
        // Preserve the terminal event before killing remaining group members.
        // Cleanup and reaping must not reinterpret the completed leader's exit.
        sink.exited(child.observed_exit);
        child.stop_and_join();
        const auto cleanup = std::chrono::steady_clock::now();
        while (output.read || errors.read)
        {
            drain(errors.read, LR_STREAM_STDERR, sink);
            drain(output.read, LR_STREAM_STDOUT, sink);
            if (std::chrono::steady_clock::now() - cleanup > std::chrono::seconds(5))
            {
                throw process_failure(LR_IO_ERROR, "Child pipes remained open after process-group cleanup.");
            }
            if (output.read || errors.read)
            {
                wait_for_data(output.read, errors.read);
            }
        }
    }
    catch (...)
    {
        auto failure = std::current_exception();
        try
        {
            if (child.pid > 0)
            {
                sink.exited(child.stop_and_join());
            }
            // Descendants normally share the group. Bound draining as well in
            // case a third-party tool deliberately starts a different session.
            const auto cleanup = std::chrono::steady_clock::now();
            while (errors.read && std::chrono::steady_clock::now() - cleanup < std::chrono::seconds(5))
            {
                drain(errors.read, LR_STREAM_STDERR, sink, true);
                if (errors.read)
                {
                    pollfd event{errors.read.get(), POLLIN, 0};
                    poll(&event, 1, 5);
                }
            }
        }
        catch (...)
        {
        }
        std::rethrow_exception(failure);
    }
}
