import CLumaRecode
import Foundation

struct EngineError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

// Native cancel functions may run alongside the blocking operation. The lock
// ensures a cancellation cannot reach a handle after its owner releases it.
final class JobControl: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false
    private var abort: (() -> Void)?

    func cancel() {
        lock.withLock {
            cancelled = true
            abort?()
        }
    }

    func check() throws {
        if lock.withLock({ cancelled }) { throw CancellationError() }
    }

    func run<T>(_ halt: @escaping () -> Void, _ body: () throws -> T) throws -> T {
        try lock.withLock {
            if cancelled { throw CancellationError() }
            abort = halt
        }
        defer { lock.withLock { abort = nil } }
        return try body()
    }
}

private func nativeError() -> lr_error {
    var error = lr_error()
    lr_error_init(&error, UInt64(MemoryLayout<lr_error>.size))
    return error
}

private func check(_ status: lr_status, _ error: lr_error? = nil, _ fallback: String) throws {
    if status == LR_OK { return }
    if status == LR_CANCELLED { throw CancellationError() }
    let detail = error.map {
        withUnsafeBytes(of: $0.message) { String(decoding: $0.prefix(while: { $0 != 0 }), as: UTF8.self) }
    } ?? ""
    throw EngineError(message: detail.isEmpty ? fallback : detail)
}

// The ABI copies every view during create(). All pointers expire before this
// closure returns. A single backing buffer also covers the process argv array.
private func withViews<T>(_ values: [Data], _ body: ([lr_utf8_view]) throws -> T) rethrows -> T {
    var data = Data()
    let offsets = values.map { value in
        let offset = data.count
        data.append(value)
        return offset
    }
    return try data.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) in
        let base = bytes.bindMemory(to: UInt8.self).baseAddress
        let views = zip(offsets, values).map { offset, value in
            lr_utf8_view(data: value.isEmpty ? nil : base?.advanced(by: offset), size: UInt64(value.count))
        }
        return try body(views)
    }
}

private func utf8(_ value: String) -> Data { Data(value.utf8) }

private func copyBytes(
    limit: UInt64,
    _ copy: (UnsafeMutablePointer<UInt8>?, UInt64, UnsafeMutablePointer<UInt64>) -> lr_status
) throws -> Data {
    var length: UInt64 = 0
    let measured = copy(nil, 0, &length)
    if measured == LR_OK && length == 0 { return Data() }
    if measured != LR_BUFFER_TOO_SMALL { try check(measured, nil, "Cannot measure native result.") }
    guard length > 0, length <= limit else { throw EngineError(message: "Native result exceeds its budget.") }
    var data = Data(count: Int(length))
    let status = data.withUnsafeMutableBytes { (bytes: UnsafeMutableRawBufferPointer) in
        copy(bytes.bindMemory(to: UInt8.self).baseAddress, UInt64(bytes.count), &length)
    }
    try check(status, nil, "Cannot copy native result.")
    guard length == UInt64(data.count) else { throw EngineError(message: "Native result changed size.") }
    return data
}

struct MediaSummary: Decodable, Sendable {
    struct Stream: Decodable, Sendable {
        let codec_name: String?
        let codec_type: String?
        let pix_fmt: String?
        let color_transfer: String?
        let width: Int?
        let height: Int?
        let channels: Int?
    }

    let kind: String
    let profile: String?
    let duration: Double
    let video: Stream
    let streams: [Stream]
    let hdr10plus: Bool
    let hasDolbyVision: Bool

    init(from decoder: Decoder) throws {
        enum Key: String, CodingKey { case schema_version, kind, profile, duration, video, streams, hdr10plus, dolby_vision }
        let values = try decoder.container(keyedBy: Key.self)
        guard try values.decode(Int.self, forKey: .schema_version) == 1 else {
            throw EngineError(message: "Unsupported source snapshot schema.")
        }
        kind = try values.decode(String.self, forKey: .kind)
        profile = try values.decodeIfPresent(String.self, forKey: .profile)
        duration = try values.decode(Double.self, forKey: .duration)
        video = try values.decode(Stream.self, forKey: .video)
        streams = try values.decode([Stream].self, forKey: .streams)
        hdr10plus = try values.decode(Bool.self, forKey: .hdr10plus)
        hasDolbyVision = try !values.decodeNil(forKey: .dolby_vision)
    }
}

struct MediaSource: Sendable {
    let url: URL
    let snapshot: Data
    let summary: MediaSummary
    fileprivate let identity: SourceIdentity
}

fileprivate struct SourceIdentity: Equatable, Sendable {
    let device: UInt64
    let inode: UInt64
    let size: UInt64
    let modified: Date

    init(_ url: URL) throws {
        let values = try FileManager.default.attributesOfItem(atPath: url.resolvingSymlinksInPath().path)
        guard values[.type] as? FileAttributeType == .typeRegular,
              let device = values[.systemNumber] as? NSNumber,
              let inode = values[.systemFileNumber] as? NSNumber,
              let size = values[.size] as? NSNumber,
              let modified = values[.modificationDate] as? Date else {
            throw EngineError(message: "The source must be an accessible regular file.")
        }
        self.device = device.uint64Value
        self.inode = inode.uint64Value
        self.size = size.uint64Value
        self.modified = modified
    }

    func check(_ url: URL) throws {
        guard try Self(url) == self else {
            throw EngineError(message: "The source changed. Open it again before continuing.")
        }
    }
}

struct FrameSelection: Sendable {
    // The user-facing selection is one-based with an inclusive end.
    let first: UInt64
    let last: UInt64?
}

final class FrameIndex: @unchecked Sendable {
    private let handle: OpaquePointer
    let count: UInt64

    fileprivate init(handle: OpaquePointer, count: UInt64) {
        self.handle = handle
        self.count = count
    }

    deinit { lr_timeline_release(handle) }

    fileprivate func resolve(_ selection: FrameSelection) throws -> lr_frame_window {
        let last = selection.last ?? count
        guard selection.first > 0, selection.first <= last, last <= count else {
            throw EngineError(message: "Select a frame range within 1…\(count).")
        }
        var window = lr_frame_window()
        try check(lr_frame_window_init(&window, UInt64(MemoryLayout<lr_frame_window>.size)), nil,
                  "Cannot initialize the frame range.")
        try check(lr_timeline_resolve(handle, selection.first - 1, last, &window), nil,
                  "Cannot resolve the selected frames.")
        return window
    }
}

struct EncodingOptions: Sendable {
    var codec = "hevc"
    var output = "auto"
    var sourceKind = "auto"
    var profile: String?
    var standardHDR = false

    fileprivate func data(for media: MediaSummary, container: String) throws -> Data {
        let kind = sourceKind == "auto" ? media.kind : sourceKind
        guard kind != "unknown" else {
            throw EngineError(message: "Choose the source color profile.")
        }
        guard !media.hdr10plus && !media.hasDolbyVision || standardHDR || output == "sdr" else {
            throw EngineError(message: "Select Standard HDR or SDR to convert this dynamic HDR source.")
        }
        guard media.kind != "raw" else {
            throw EngineError(message: "ProRes RAW development is not available in this macOS build.")
        }
        let target = output == "auto" ? (kind == "sdr" ? "sdr" : "hdr") : output
        let transform: String
        switch kind {
        case "log": transform = target == "hdr" ? "log-hdr" : "log-sdr"
        case "pq", "hlg": transform = target == "sdr" ? "hdr-sdr" : "none"
        default: transform = "none"
        }
        var fields = [
            "purpose": "archive", "codec": codec, "output": target, "source_kind": sourceKind,
            "transform": transform, "dynamic_hdr": standardHDR ? "standard" : "preserve",
            "container": container
        ]
        if kind == "log" {
            guard let curve = profile ?? media.profile else { throw EngineError(message: "Choose the camera Log curve.") }
            fields["profile"] = curve
        }
        return try JSONEncoder().encode(fields)
    }
}

struct MediaTools: Sendable {
    let ffmpeg: URL?
    let ffprobe: URL

    static func discover(ffmpeg: String? = nil, ffprobe: String? = nil, requireEncoder: Bool = true) throws -> MediaTools {
        let environment = ProcessInfo.processInfo.environment
        let systemPaths = ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]
        let entries = (environment["PATH"] ?? "").split(separator: ":").map(String.init)
        let paths = (entries + systemPaths).filter { $0.hasPrefix("/") }

        func find(_ name: String, _ selected: String?) throws -> URL {
            let configured = selected ?? environment["LUMARECODE_" + name.uppercased()]
            let candidates = configured.flatMap { $0.isEmpty ? nil : [$0] }
                ?? paths.map { URL(fileURLWithPath: $0, isDirectory: true).appendingPathComponent(name).path }
            for path in candidates {
                let url = URL(fileURLWithPath: path).standardizedFileURL
                if FileManager.default.isExecutableFile(atPath: url.path),
                   (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == false {
                    return url
                }
            }
            throw EngineError(message: "Could not find an executable \(name). Check the tool paths in Settings.")
        }
        return try MediaTools(ffmpeg: requireEncoder ? find("ffmpeg", ffmpeg) : nil, ffprobe: find("ffprobe", ffprobe))
    }
}

// Receives FFmpeg's line-oriented progress on the native worker thread. It
// keeps only the partial line and limits that buffer even for a malformed tool.
private final class ProgressReader {
    var pending = [UInt8]()
    var droppingLine = false
    let update: (Double) -> Void
    init(_ update: @escaping (Double) -> Void) { self.update = update }

    func feed(_ bytes: UnsafeBufferPointer<UInt8>) {
        for byte in bytes {
            if byte == 10 {
                if !droppingLine, pending.starts(with: "out_time_us=".utf8),
                   let micros = Int64(String(decoding: pending.dropFirst(12), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)) {
                    update(max(0, Double(micros) / 1_000_000))
                }
                pending.removeAll(keepingCapacity: true)
                droppingLine = false
            } else if pending.count < 4096 && !droppingLine {
                pending.append(byte)
            } else {
                droppingLine = true
            }
        }
    }
}

struct NativeEngine: Sendable {
    let tools: MediaTools
    let assets: URL
    private let presets: Data
    private let motionPresets: Data

    init(tools: MediaTools, assets: URL) throws {
        self.tools = tools
        self.assets = assets
        presets = try Data(contentsOf: assets.appendingPathComponent("recommended-presets.json"))
        motionPresets = try Data(contentsOf: assets.appendingPathComponent("motion-photo-presets.json"))
    }

    static var bundledAssets: URL {
        // An installed .app always has a Resources directory. The fallback
        // also lets the CLI report a missing resource from an incomplete copy.
        (Bundle.main.resourceURL ?? Bundle.main.bundleURL).appendingPathComponent("Media", isDirectory: true)
    }

    func inspect(_ source: URL, control: JobControl) throws -> MediaSource {
        var cleanup = true
        return try inspect(source, control: control, cleanup: &cleanup)
    }

    private func inspect(_ source: URL, control: JobControl, cleanup: inout Bool) throws -> MediaSource {
        let url = source.standardizedFileURL
        let identity = try SourceIdentity(url)
        var options = lr_probe_options()
        try check(lr_probe_options_init(&options, UInt64(MemoryLayout<lr_probe_options>.size)), nil,
                  "Cannot initialize source inspection.")
        var handle: OpaquePointer?
        var error = nativeError()
        let status = withViews([utf8(tools.ffprobe.path), utf8(url.path)]) { views in
            options.ffprobe = views[0]
            options.source = views[1]
            return lr_probe_create(&options, &handle, &error)
        }
        try check(status, error, "Cannot create source inspection.")
        guard let handle else { throw EngineError(message: "Missing source inspection handle.") }
        defer { lr_probe_release(handle) }
        try control.run({ _ = lr_probe_cancel(handle) }) {
            let executed = lr_probe_run(handle, &error)
            var state = lr_probe_snapshot()
            lr_probe_snapshot_init(&state, UInt64(MemoryLayout<lr_probe_snapshot>.size))
            cleanup = lr_probe_get_snapshot(handle, &state) == LR_OK && state.cleanup_state == LR_CLEANUP_CONFIRMED
            try check(executed, error, "Source inspection failed.")
        }
        let data = try copyBytes(limit: 64 * 1024 * 1024) { lr_probe_copy_json(handle, $0, $1, $2) }
        try identity.check(url)
        return try MediaSource(url: url, snapshot: data, summary: JSONDecoder().decode(MediaSummary.self, from: data),
                               identity: identity)
    }

    func loadFrames(_ source: MediaSource, control: JobControl) throws -> FrameIndex {
        var cleanup = true
        return try loadFrames(source, control: control, cleanup: &cleanup)
    }

    private func loadFrames(_ source: MediaSource, control: JobControl, cleanup: inout Bool) throws -> FrameIndex {
        try source.identity.check(source.url)
        var options = lr_timeline_options()
        try check(lr_timeline_options_init(&options, UInt64(MemoryLayout<lr_timeline_options>.size)), nil,
                  "Cannot initialize frame indexing.")
        var handle: OpaquePointer?
        var error = nativeError()
        let status = withViews([utf8(tools.ffprobe.path), source.snapshot]) { views in
            options.ffprobe = views[0]
            return lr_timeline_create_from_probe(&options, views[1], &handle, &error)
        }
        try check(status, error, "Cannot create the frame index.")
        guard let handle else { throw EngineError(message: "Missing frame index handle.") }
        do {
            var result = lr_timeline_snapshot()
            lr_timeline_snapshot_init(&result, UInt64(MemoryLayout<lr_timeline_snapshot>.size))
            try control.run({ _ = lr_timeline_cancel(handle) }) {
                let executed = lr_timeline_run(handle, &error)
                let inspected = lr_timeline_get_snapshot(handle, &result)
                cleanup = inspected == LR_OK && result.cleanup_state == LR_CLEANUP_CONFIRMED
                try check(executed, error, "Cannot read frames.")
                try check(inspected, nil, "Cannot read the frame count.")
            }
            try source.identity.check(source.url)
            return FrameIndex(handle: handle, count: result.count)
        } catch {
            lr_timeline_release(handle)
            throw error
        }
    }

    func convert(
        _ source: MediaSource, to destination: URL, options: EncodingOptions, overwrite: Bool,
        index: FrameIndex? = nil, selection: FrameSelection? = nil, control: JobControl,
        progress: @escaping (Double) -> Void = { _ in }
    ) throws {
        try control.check()
        let destination = destination.standardizedFileURL
        let container = destination.pathExtension.lowercased()
        guard ["mkv", "mp4"].contains(container) else { throw EngineError(message: "Use an .mkv or .mp4 output path.") }
        try source.identity.check(source.url)
        let choices = try options.data(for: source.summary, container: container)
        let window = try selection.map { selection -> lr_frame_window in
            let resolvedIndex = try index ?? loadFrames(source, control: control)
            return try resolvedIndex.resolve(selection)
        }
        var output = lr_output_options()
        try check(lr_output_options_init(&output, UInt64(MemoryLayout<lr_output_options>.size)), nil,
                  "Cannot initialize the output.")
        output.overwrite = overwrite ? 1 : 0
        var transaction: OpaquePointer?
        var error = nativeError()
        let created = withViews([utf8(source.url.path), utf8(destination.path)]) { views in
            output.source = views[0]
            output.destination = views[1]
            return lr_output_create(&output, &transaction, &error)
        }
        try check(created, error, "Cannot create the output workspace.")
        guard let transaction else { throw EngineError(message: "Missing output workspace.") }
        var cleanup = true
        var committed = false
        defer {
            if !committed { lr_output_cleanup(transaction, cleanup ? 1 : 0, nil) }
            lr_output_release(transaction)
        }
        try source.identity.check(source.url)
        let staging = try copyBytes(limit: 500_000) { lr_output_copy_path(transaction, LR_OUTPUT_PATH_STAGING, $0, $1, $2) }
        let plan = try makePlan(source: source.snapshot, choices: choices, staging: staging, window: window)
        defer { lr_plan_release(plan) }
        let planJSON = try copyBytes(limit: 1024 * 1024) { lr_plan_copy_json(plan, $0, $1, $2) }
        let command = try JSONDecoder().decode(PlannedCommand.self, from: planJSON)
        guard command.sidecars.isEmpty else { throw EngineError(message: "This conversion requires an unsupported media sidecar.") }
        let total = window.map { $0.end_seconds - $0.start_seconds } ?? source.summary.duration
        try runEncoder(command, control: control, cleanup: &cleanup) { position in
            if total > 0 { progress(min(0.99, position / total)) }
        }
        let stagedURL = URL(fileURLWithPath: String(decoding: staging, as: UTF8.self))
        let checked = try inspect(stagedURL, control: control, cleanup: &cleanup)
        try control.check()
        let checkedStatus = withViews([source.snapshot, checked.snapshot]) { views in
            lr_plan_verify_output(plan, views[0], views[1], &error)
        }
        try check(checkedStatus, error, "The encoded file failed verification.")
        if let window {
            let encodedFrames = try loadFrames(checked, control: control, cleanup: &cleanup)
            guard encodedFrames.count == window.end - window.start else {
                throw EngineError(message: "The encoded frame count differs from the selected range.")
            }
        }
        try check(lr_output_seal(transaction, cleanup ? 1 : 0, &error), error, "Cannot seal the encoded file.")
        try control.check()
        try check(lr_output_commit(transaction, &error), error, "Cannot publish the encoded file.")
        committed = true
        progress(1)
    }

    private struct PlannedCommand: Decodable {
        let arguments: [String]
        let working_directory: String?
        let sidecars: [String]
    }

    private func makePlan(source: Data, choices: Data, staging: Data, window: lr_frame_window?) throws -> OpaquePointer {
        guard let executable = tools.ffmpeg else { throw EngineError(message: "FFmpeg is required for conversion.") }
        var options = lr_plan_options()
        try check(lr_plan_options_init(&options, UInt64(MemoryLayout<lr_plan_options>.size)), nil,
                  "Cannot initialize encoding options.")
        var plan: OpaquePointer?
        var error = nativeError()
        let status = withViews([source, choices, presets, motionPresets, utf8(executable.path), staging, utf8(assets.path)]) { views in
            options.media_json = views[0]
            options.encode_json = views[1]
            options.presets_json = views[2]
            options.motion_presets_json = views[3]
            options.ffmpeg = views[4]
            options.destination = views[5]
            options.assets = views[6]
            if var window {
                return withUnsafePointer(to: &window) {
                    options.window = $0
                    return lr_plan_create(&options, &plan, &error)
                }
            }
            return lr_plan_create(&options, &plan, &error)
        }
        try check(status, error, "Cannot plan encoding.")
        guard let plan else { throw EngineError(message: "Missing encoding plan.") }
        return plan
    }

    private func runEncoder(
        _ command: PlannedCommand, control: JobControl, cleanup: inout Bool, progress: @escaping (Double) -> Void
    ) throws {
        guard let executable = command.arguments.first else { throw EngineError(message: "The encoding plan is empty.") }
        var options = lr_process_options()
        try check(lr_process_options_init(&options, UInt64(MemoryLayout<lr_process_options>.size)), nil,
                  "Cannot initialize FFmpeg.")
        options.stdout_mode = LR_CAPTURE_DISCARD
        options.timeout_ms = 0
        var handle: OpaquePointer?
        var error = nativeError()
        let argv = command.arguments.dropFirst().map(utf8)
        let status = withViews([utf8(executable), utf8(command.working_directory ?? assets.path)] + argv) { views in
            options.executable = views[0]
            options.working_directory = views[1]
            return views.dropFirst(2).withUnsafeBufferPointer { arguments in
                options.arguments = arguments.baseAddress
                options.argument_count = UInt32(arguments.count)
                return lr_process_create(&options, &handle, &error)
            }
        }
        try check(status, error, "Cannot start FFmpeg.")
        guard let handle else { throw EngineError(message: "Missing FFmpeg process handle.") }
        defer { lr_process_release(handle) }
        let reader = ProgressReader(progress)
        try control.run({ _ = lr_process_cancel(handle) }) {
            let run = withExtendedLifetime(reader) {
                lr_process_run_streaming(handle, { context, stream, bytes, count in
                    if stream == LR_STREAM_STDOUT, let context {
                        Unmanaged<ProgressReader>.fromOpaque(context).takeUnretainedValue()
                            .feed(UnsafeBufferPointer(start: bytes, count: Int(count)))
                    }
                    return LR_OK
                }, Unmanaged.passUnretained(reader).toOpaque(), &error)
            }
            var state = lr_process_snapshot()
            lr_process_snapshot_init(&state, UInt64(MemoryLayout<lr_process_snapshot>.size))
            cleanup = lr_process_get_snapshot(handle, &state) == LR_OK && state.cleanup_state == LR_CLEANUP_CONFIRMED
            if run == LR_IO_ERROR, let detail = try? copyBytes(limit: 1024 * 1024, {
                lr_process_copy_output(handle, LR_STREAM_STDERR, $0, $1, $2)
            }), !detail.isEmpty {
                throw EngineError(message: String(decoding: detail, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines))
            }
            try check(run, error, "FFmpeg failed.")
        }
    }
}
