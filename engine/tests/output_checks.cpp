#include "lumarecode/output.h"

#include <barrier>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <random>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace fs = std::filesystem;
namespace
{
unsigned checks;
void check(bool condition, const char *message)
{
    ++checks;
    if (!condition)
        throw std::runtime_error(message);
}
std::string utf8(const fs::path &path)
{
    const auto text = path.u8string();
    return {text.begin(), text.end()};
}
lr_utf8_view view(const std::string &text)
{
    return {reinterpret_cast<const uint8_t *>(text.data()), text.size()};
}
void write(const fs::path &path, const std::string &text)
{
    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    file << text;
    file.close();
    check(!file.fail(), "Could not write test fixture.");
}
std::string read(const fs::path &path)
{
    std::ifstream file(path, std::ios::binary);
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}
struct fixture
{
    fs::path parent = fs::canonical(fs::temp_directory_path());
    fs::path root = parent / ("LumaRecode-output-" +
                              std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()) +
                              "-" + std::to_string(std::random_device{}()));
    fs::path source = root / u8"原片 日本語.dat";
    fs::path destination = root / u8"输出 🌈.mkv";
    fixture()
    {
        check(fs::create_directory(root), "Could not reserve fixture directory.");
        write(source, "source bytes");
    }
    ~fixture()
    {
        // Test fixtures are generated exclusively here; production cleanup
        // never recursively enumerates a workspace or a user directory.
        if (root.is_absolute() && root.parent_path() == parent &&
            root.filename().string().starts_with("LumaRecode-output-"))
        {
            std::error_code ignored;
            fs::remove_all(root, ignored);
        }
    }
};
struct transaction
{
    lr_output_transaction *handle = nullptr;
    lr_error error{};
    transaction()
    {
        lr_error_init(&error, sizeof(error));
    }
    ~transaction()
    {
        lr_output_release(handle);
    }
    transaction(const transaction &) = delete;
    transaction &operator=(const transaction &) = delete;
    lr_status create(const fs::path &source, const fs::path &target, bool overwrite = false)
    {
        const auto first = utf8(source), second = utf8(target);
        lr_output_options options{};
        check(lr_output_options_init(&options, sizeof(options)) == LR_OK, "Initialize transaction options.");
        options.source = view(first);
        options.destination = view(second);
        options.overwrite = overwrite;
        return lr_output_create(&options, &handle, &error);
    }
    fs::path path(uint32_t kind)
    {
        uint64_t size = 0;
        check(lr_output_copy_path(handle, kind, nullptr, 0, &size) == LR_BUFFER_TOO_SMALL,
              "Path size query.");
        std::vector<uint8_t> bytes(size);
        check(lr_output_copy_path(handle, kind, bytes.data(), bytes.size(), &size) == LR_OK,
              "Copy UTF-8 path.");
        return fs::path(std::u8string(bytes.begin(), bytes.end()));
    }
    lr_output_snapshot snapshot()
    {
        lr_output_snapshot value{};
        check(lr_output_snapshot_init(&value, sizeof(value)) == LR_OK, "Initialize snapshot.");
        check(lr_output_get_snapshot(handle, &value) == LR_OK, "Get snapshot.");
        return value;
    }
    lr_status seal(bool confirmed = true)
    {
        return lr_output_seal(handle, confirmed, &error);
    }
    lr_status commit()
    {
        return lr_output_commit(handle, &error);
    }
    lr_status cleanup(bool confirmed = true)
    {
        return lr_output_cleanup(handle, confirmed, &error);
    }
};

void identities()
{
    fixture f;
    const auto alias = f.root / "hard-link.dat";
    fs::create_hard_link(f.source, alias);
    auto a = utf8(f.source), b = utf8(alias), c = utf8(f.destination);
    uint32_t same = 42;
    check(lr_files_same(view(a), view(b), &same, nullptr) == LR_OK && same == 1,
          "Hard links need OS identity.");
    same = 42;
    check(lr_files_same(view(a), view(c), &same, nullptr) == LR_NOT_FOUND && same == 42,
          "Missing path changed result.");
    write(f.destination, "unrelated");
    check(lr_files_same(view(a), view(c), &same, nullptr) == LR_OK && same == 0, "Distinct file identities.");
    transaction t;
    check(t.create(f.source, alias, true) == LR_INVALID_ARGUMENT && t.handle == nullptr,
          "Source hard-link output accepted.");
    check(t.create(f.source, f.source, true) == LR_INVALID_ARGUMENT, "Source overwrite accepted.");
    fs::create_directory(f.root / "directory");
    check(t.create(f.source, f.root / "directory", true) == LR_INVALID_ARGUMENT,
          "Directory output accepted.");
    check(t.create(f.source, "relative.mkv") == LR_INVALID_ARGUMENT, "Relative output accepted.");
    check(t.create(f.source, f.root / "missing" / ".." / "file.mkv") == LR_INVALID_ARGUMENT,
          "Dot component accepted.");
}
void publication()
{
    fixture f;
    transaction t;
    check(t.create(f.source, f.destination) == LR_OK, t.error.message);
    const auto stage = t.path(LR_OUTPUT_PATH_STAGING), work = t.path(LR_OUTPUT_PATH_WORKSPACE);
    check(fs::is_directory(work) && !fs::exists(f.destination), "Create changed final output.");
    check(t.snapshot().source_size == 12 && t.snapshot().state == LR_OUTPUT_CREATED, "Initial snapshot.");
    uint8_t sentinel = 0xa5;
    uint64_t needed = 0;
    check(lr_output_copy_path(t.handle, LR_OUTPUT_PATH_STAGING, &sentinel, 1, &needed) ==
                  LR_BUFFER_TOO_SMALL &&
              sentinel == 0xa5 && needed > 1,
          "Short path buffer was partially written.");
    check(t.commit() == LR_INVALID_ARGUMENT, "Unsealed output committed.");
    write(stage, "encoded result");
    check(t.seal(false) == LR_INVALID_ARGUMENT, "Unconfirmed process cleanup sealed output.");
    check(t.seal() == LR_OK, t.error.message);
    check(t.snapshot().state == LR_OUTPUT_SEALED && t.snapshot().output_size == 14, "Sealed snapshot.");
    check(t.commit() == LR_OK, t.error.message);
    check(read(f.destination) == "encoded result" && read(f.source) == "source bytes",
          "Publication changed bytes.");
    check(!fs::exists(work) && t.snapshot().flags == 0 && t.snapshot().state == LR_OUTPUT_COMMITTED,
          "Committed workspace retained.");
    check(t.commit() == LR_INVALID_ARGUMENT && t.cleanup() == LR_OK, "Terminal operation contract.");
}
void overwrite_policy()
{
    fixture f;
    write(f.destination, "old target");
    transaction refused;
    check(refused.create(f.source, f.destination) == LR_ALREADY_EXISTS, "Overwrite needed no consent.");
    {
        transaction t;
        check(t.create(f.source, f.destination, true) == LR_OK, t.error.message);
        write(t.path(LR_OUTPUT_PATH_STAGING), "replacement");
        check(read(f.destination) == "old target", "Original target changed before verification.");
        check(t.seal() == LR_OK && t.commit() == LR_OK, t.error.message);
        check(read(f.destination) == "replacement", "Explicit overwrite failed.");
    }
    fs::remove(f.destination);
    transaction race;
    check(race.create(f.source, f.destination) == LR_OK, race.error.message);
    write(race.path(LR_OUTPUT_PATH_STAGING), "candidate");
    check(race.seal() == LR_OK, race.error.message);
    write(f.destination, "created meanwhile");
    check(race.commit() == LR_ALREADY_EXISTS, "Destination race overwrote without consent.");
    check(read(f.destination) == "created meanwhile", "Destination race lost original bytes.");
    check(race.cleanup() == LR_OK, race.error.message);
}
void retained_workspaces()
{
    fixture f;
    transaction t;
    check(t.create(f.source, f.destination) == LR_OK, t.error.message);
    const auto stage = t.path(LR_OUTPUT_PATH_STAGING), work = t.path(LR_OUTPUT_PATH_WORKSPACE);
    write(stage, "partial");
    check(t.cleanup(false) == LR_IO_ERROR && fs::exists(stage) && t.snapshot().state == LR_OUTPUT_RETAINED,
          "Unconfirmed cleanup removed output.");
    write(work / "extra.dat", "retain me");
    check(t.cleanup() == LR_IO_ERROR && read(work / "extra.dat") == "retain me",
          "Cleanup traversed unknown entries.");
    fs::remove(work / "extra.dat");
    check(t.cleanup() == LR_OK && !fs::exists(work) && t.snapshot().state == LR_OUTPUT_ABORTED,
          "Retry cleanup failed.");
    {
        transaction released;
        check(released.create(f.source, f.destination) == LR_OK, released.error.message);
        write(released.path(LR_OUTPUT_PATH_STAGING), "leave on release");
        const auto saved = released.path(LR_OUTPUT_PATH_WORKSPACE);
        lr_output_release(released.handle);
        released.handle = nullptr;
        check(fs::is_directory(saved), "Release implicitly deleted task files.");
    }
    transaction committed;
    check(committed.create(f.source, f.destination) == LR_OK, committed.error.message);
    const auto pending = committed.path(LR_OUTPUT_PATH_WORKSPACE);
    write(committed.path(LR_OUTPUT_PATH_STAGING), "complete");
    write(pending / "extra.dat", "unknown");
    check(committed.seal() == LR_OK && committed.commit() == LR_OK, committed.error.message);
    check(committed.snapshot().state == LR_OUTPUT_COMMITTED &&
              committed.snapshot().flags == LR_OUTPUT_WORKSPACE_EXISTS && read(f.destination) == "complete",
          "Post-publication cleanup turned success into failure.");
    fs::remove(pending / "extra.dat");
    check(committed.cleanup() == LR_OK && committed.snapshot().state == LR_OUTPUT_COMMITTED,
          "Committed cleanup retry.");
}
void competing_publishers()
{
    fixture f;
    transaction first, second;
    check(first.create(f.source, f.destination) == LR_OK && second.create(f.source, f.destination) == LR_OK,
          "Concurrent transaction creation failed.");
    write(first.path(LR_OUTPUT_PATH_STAGING), "first");
    write(second.path(LR_OUTPUT_PATH_STAGING), "second");
    check(first.seal() == LR_OK && second.seal() == LR_OK, "Concurrent seal failed.");
    std::barrier start(3);
    lr_status a = LR_INTERNAL_ERROR, b = LR_INTERNAL_ERROR;
    std::thread left(
        [&]
        {
            start.arrive_and_wait();
            a = first.commit();
        });
    std::thread right(
        [&]
        {
            start.arrive_and_wait();
            b = second.commit();
        });
    start.arrive_and_wait();
    left.join();
    right.join();
    check((a == LR_OK && b == LR_ALREADY_EXISTS) || (b == LR_OK && a == LR_ALREADY_EXISTS),
          "Competing no-overwrite publishers did not choose exactly one winner.");
    check(read(f.destination) == (a == LR_OK ? "first" : "second"), "Losing publisher changed winner bytes.");
    check(first.cleanup() == LR_OK && second.cleanup() == LR_OK, "Competing publisher cleanup failed.");
}
void invalid_staging()
{
    fixture f;
    transaction t;
    check(t.create(f.source, f.destination) == LR_OK, t.error.message);
    const auto stage = t.path(LR_OUTPUT_PATH_STAGING);
    write(stage, "");
    check(t.seal() == LR_INVALID_DATA, "Empty staging accepted.");
    fs::remove(stage);
    write(f.root / "other.dat", "unrelated");
    fs::create_hard_link(f.root / "other.dat", stage);
    check(t.seal() == LR_INVALID_DATA, "Multi-link staging accepted.");
    check(t.cleanup() == LR_OK && read(f.root / "other.dat") == "unrelated",
          "Hard-link cleanup changed other file.");

    transaction linked;
    check(linked.create(f.source, f.destination) == LR_OK, linked.error.message);
    const auto link = linked.path(LR_OUTPUT_PATH_STAGING);
    std::error_code error;
    fs::create_symlink(f.source, link, error);
    if (!error)
    {
        check(linked.seal() != LR_OK, "Symlink staging accepted.");
        check(linked.cleanup() == LR_OK && read(f.source) == "source bytes", "Cleanup followed a symlink.");
    }
    else
    {
        std::cout << "Symlink creation unavailable; symlink fixture not run.\n";
        check(linked.cleanup() == LR_OK, linked.error.message);
    }
}
void mutations()
{
    fixture f;
    transaction t;
    check(t.create(f.source, f.destination) == LR_OK, t.error.message);
    const auto stage = t.path(LR_OUTPUT_PATH_STAGING);
    write(stage, "candidate");
#ifdef _WIN32
    std::ofstream source_writer(f.source, std::ios::binary | std::ios::trunc);
    check(!source_writer, "Windows source pin allowed a writer.");
    source_writer.close();
    check(t.seal() == LR_OK, t.error.message);
    std::ofstream stage_writer(stage, std::ios::binary | std::ios::trunc);
    check(!stage_writer, "Windows seal allowed a writer.");
    stage_writer.close();
    check(t.commit() == LR_OK, t.error.message);
#else
    write(f.source, "modified source bytes");
    check(t.seal() == LR_SOURCE_CHANGED, "Source revision change was ignored.");
    check(t.cleanup() == LR_OK, t.error.message);
    transaction changed;
    check(changed.create(f.source, f.destination) == LR_OK, changed.error.message);
    const auto next = changed.path(LR_OUTPUT_PATH_STAGING);
    write(next, "candidate");
    check(changed.seal() == LR_OK, changed.error.message);
    write(next, "changed sealed output");
    check(changed.commit() == LR_INVALID_DATA && !fs::exists(f.destination),
          "Changed sealed output committed.");
    check(changed.cleanup() == LR_OK, changed.error.message);
    transaction rebound;
    check(rebound.create(f.source, f.destination) == LR_OK, rebound.error.message);
    const auto work = rebound.path(LR_OUTPUT_PATH_WORKSPACE), moved = f.root / "moved";
    fs::rename(work, moved);
    fs::create_directory(work);
    write(work / "foreign.dat", "foreign bytes");
    check(rebound.cleanup() == LR_IO_ERROR && read(work / "foreign.dat") == "foreign bytes",
          "Cleanup used rebound directory.");
    fs::remove(work / "foreign.dat");
    fs::remove(work);
    fs::rename(moved, work);
    check(rebound.cleanup() == LR_OK, rebound.error.message);
#endif
}
} // namespace

int main()
{
    try
    {
        identities();
        publication();
        overwrite_policy();
        retained_workspaces();
        competing_publishers();
        invalid_staging();
        mutations();
        std::cout << checks << " native output transaction checks passed.\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << "Output contract failure after " << checks << " checks: " << error.what() << '\n';
        return 1;
    }
}
