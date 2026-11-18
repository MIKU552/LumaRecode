import CLumaRecode
import CoreMedia
import Darwin
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
        try begin(halt)
        defer { finish() }
        return try body()
    }

    func runAsync<T>(_ halt: @escaping () -> Void, _ body: () async throws -> T) async throws -> T {
        try begin(halt)
        defer { finish() }
        return try await body()
    }

    private func begin(_ halt: @escaping () -> Void) throws {
        try lock.withLock {
            if cancelled { throw CancellationError() }
            abort = halt
        }
    }

    private func finish() { lock.withLock { abort = nil } }
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
    struct SideData: Decodable, Sendable {
        let side_data_type: String?
    }

    private struct Frame: Decodable {
        let side_data_list: [SideData]?
    }

    private struct PixelFormat: Decodable {
        struct Component: Decodable { let bit_depth: Int }
        let components: [Component]?
    }

    struct Stream: Decodable, Sendable {
        let index: Int?
        let codec_name: String?
        let codec_type: String?
        let pix_fmt: String?
        let color_transfer: String?
        let width: Int?
        let height: Int?
        let channels: Int?
        let side_data_list: [SideData]?
    }

    let kind: String
    let profile: String?
    let duration: Double
    let video: Stream
    let streams: [Stream]
    let hdr10plus: Bool
    let hasDolbyVision: Bool
    let hasStaticHDR: Bool
    let bitDepth: Int

    init(from decoder: Decoder) throws {
        enum Key: String, CodingKey {
            case schema_version, kind, profile, duration, video, streams, frames, hdr10plus, dolby_vision, pixel
        }
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
        bitDepth = try values.decodeIfPresent(PixelFormat.self, forKey: .pixel)?.components?.first?.bit_depth ?? 0
        let frames = try values.decode([Frame].self, forKey: .frames)
        func containsStaticHDR(_ side: [SideData]?) -> Bool {
            side?.contains {
                $0.side_data_type == "Mastering display metadata" || $0.side_data_type == "Content light level metadata"
            } ?? false
        }
        hasStaticHDR = containsStaticHDR(video.side_data_list) || frames.contains { containsStaticHDR($0.side_data_list) }
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

    func resolve(_ selection: FrameSelection) throws -> lr_frame_window {
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

    func liveRange(around frame: UInt64) throws -> FrameSelection {
        let cover = try resolve(FrameSelection(first: frame, last: frame))
        let first = try resolve(FrameSelection(first: 1, last: 1))
        let position = cover.start_seconds - first.start_seconds
        var start: UInt64 = 0
        var end: UInt64 = 0
        try check(lr_timeline_at_or_after(handle, max(0, position - 1.5), &start), nil, "Cannot find the first Live Photo frame.")
        try check(lr_timeline_at_or_after(handle, position + 1.5, &end), nil, "Cannot find the last Live Photo frame.")
        return FrameSelection(first: min(start + 1, frame), last: max(frame, end))
    }
}

enum EncoderMode: String, Sendable {
    case software
    case auto
    case videoToolbox = "videotoolbox"
}

struct VideoToolboxFormat: Equatable, Codable, Sendable {
    let codec: String
    let signal: String

    static let formats = [
        Self(codec: "hevc", signal: "sdr"), Self(codec: "avc", signal: "sdr"),
        Self(codec: "hevc", signal: "hlg"), Self(codec: "hevc", signal: "pq"),
        Self(codec: "hevc", signal: "dolbyvision84")
    ]

    var depth: Int { signal == "sdr" ? 8 : 10 }
}

struct VideoToolboxTrial: Codable, Sendable {
    let format: VideoToolboxFormat
    let available: Bool
    let diagnostic: String?
}

struct EncodingOptions: Sendable {
    var codec = "hevc"
    var output = "auto"
    var sourceKind = "auto"
    var profile: String?
    var standardHDR = false
    var encoder: EncoderMode = .software
    var liveClip = false

    func inputKind(_ media: MediaSummary) -> String { sourceKind == "auto" ? media.kind : sourceKind }
    func supportsHDRVideo(_ media: MediaSummary) -> Bool { ["hlg", "pq", "log"].contains(inputKind(media)) }
    func supportsHDRPhoto(_ media: MediaSummary) -> Bool { ["hlg", "pq", "log"].contains(inputKind(media)) }

    func canGenerateDolbyVision(_ media: MediaSummary) -> Bool {
        inputKind(media) == "hlg" && media.kind == "hlg" && media.video.color_transfer == "arib-std-b67"
            && media.bitDepth >= 10 && !media.hasDolbyVision && !media.hdr10plus
    }

    func videoToolboxSignal(_ media: MediaSummary) -> String? {
        if output == "dolbyvision84" { return canGenerateDolbyVision(media) ? output : nil }
        let kind = inputKind(media)
        if kind == "unknown" || media.kind == "raw" { return nil }
        if output == "sdr" || (output == "auto" && kind == "sdr") { return "sdr" }
        guard kind != "sdr", standardHDR || (!media.hdr10plus && !media.hasDolbyVision) else { return nil }
        // The software planners write the source's static HDR metadata.
        // The VideoToolbox wrapper does not guarantee carrying those fields.
        if media.hasStaticHDR && kind != "log" { return nil }
        return kind == "pq" ? "pq" : "hlg"
    }

    fileprivate func data(for media: MediaSummary, container: String, useVideoToolbox: Bool) throws -> Data {
        let kind = inputKind(media)
        guard kind != "unknown" else {
            throw EngineError(message: "Choose the source color profile.")
        }
        guard !media.hdr10plus || standardHDR || output == "sdr" else {
            throw EngineError(message: "Select Standard HDR or SDR to convert this HDR10+ source.")
        }
        guard media.kind != "raw" else {
            throw EngineError(message: "ProRes RAW development is not available in this macOS build.")
        }
        let target = output == "auto" ? (kind == "sdr" ? "sdr" : "hdr") : output == "dolbyvision84" ? "hdr" : output
        let transform: String
        switch kind {
        case "log": transform = target == "hdr" ? "log-hdr" : "log-sdr"
        case "pq", "hlg": transform = target == "sdr" ? "hdr-sdr" : "none"
        default: transform = "none"
        }
        var fields = [
            "purpose": useVideoToolbox ? "share" : "archive", "codec": codec, "output": target, "source_kind": sourceKind,
            "transform": transform,
            "dynamic_hdr": output == "dolbyvision84" ? "generate_84" : standardHDR ? "standard" : "preserve",
            "container": container, "cpu_tone_map": "1"
        ]
        if useVideoToolbox { fields["hardware"] = EncoderMode.videoToolbox.rawValue }
        if liveClip {
            fields["audio"] = "aac"
            fields["sampling"] = "420"
            fields["color_range"] = "limited"
        }
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

    // Cache only a successful discovery for these exact executable files.
    struct Signature: Equatable, Sendable {
        fileprivate let ffmpeg: URL
        fileprivate let ffprobe: URL
        fileprivate let encoderIdentity: SourceIdentity
        fileprivate let probeIdentity: SourceIdentity
    }

    var signature: Signature? {
        guard let ffmpeg, let encoderIdentity = try? SourceIdentity(ffmpeg),
              let probeIdentity = try? SourceIdentity(ffprobe) else { return nil }
        return Signature(ffmpeg: ffmpeg, ffprobe: ffprobe, encoderIdentity: encoderIdentity, probeIdentity: probeIdentity)
    }

    static func discover(ffmpeg: String? = nil, ffprobe: String? = nil, requireEncoder: Bool = true) throws -> MediaTools {
        let environment = ProcessInfo.processInfo.environment
        let systemPaths = ["/opt/homebrew/opt/ffmpeg-full/bin", "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]
        let entries = (environment["PATH"] ?? "").split(separator: ":").map(String.init)
        let bundled = Bundle.main.resourceURL?.appendingPathComponent("FFmpeg/bin", isDirectory: true).path
        let paths = ([bundled].compactMap { $0 } + entries + systemPaths).filter { $0.hasPrefix("/") }

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
        index: FrameIndex? = nil, selection: FrameSelection? = nil, toolbox: [VideoToolboxTrial]? = nil, control: JobControl,
        progress: @escaping (Double) -> Void = { _ in }
    ) throws {
        try control.check()
        let destination = destination.standardizedFileURL
        let container = destination.pathExtension.lowercased()
        guard ["mkv", "mp4"].contains(container) else { throw EngineError(message: "Use an .mkv or .mp4 output path.") }
        try source.identity.check(source.url)
        let signal = options.videoToolboxSignal(source.summary)
        let format = VideoToolboxFormat.formats.first { $0.codec == options.codec && $0.signal == signal }
        let hardware: Bool
        let generateDolbyVision = options.output == "dolbyvision84"
        if generateDolbyVision && (format == nil || container != "mp4") {
            throw EngineError(message: String(localized: "Dolby Vision 8.4 requires standard HLG, HEVC and MP4."))
        }
        if options.encoder != .software || generateDolbyVision, let format {
            hardware = try (toolbox?.first { $0.format == format } ?? trialVideoToolbox(format, control: control)).available
        } else {
            hardware = false
        }
        if (options.encoder == .videoToolbox || generateDolbyVision) && !hardware {
            throw EngineError(message: "VideoToolbox is unavailable for this codec and output signal.")
        }
        let choices = try options.data(for: source.summary, container: container, useVideoToolbox: hardware)
        let window = try selection.map { selection -> lr_frame_window in
            let resolvedIndex = try index ?? loadFrames(source, control: control)
            return try resolvedIndex.resolve(selection)
        }
        try withOutput(source, to: destination, overwrite: overwrite, control: control) { stagedURL, cleanup in
            // Until selected-frame metadata can be checked against the exact
            // input PTS, preservation can verify only a first-frame start.
            if source.summary.hasDolbyVision && !options.standardHDR && options.output != "sdr",
               let window, window.start != 0 {
                throw EngineError(message: "Preserving Dolby Vision currently requires starting at the first frame. Select Standard HDR or SDR for other start frames.")
            }
            let plan = try makePlan(source: source.snapshot, choices: choices, staging: utf8(stagedURL.path), window: window)
            defer { lr_plan_release(plan) }
            let command = try plannedCommand(plan)
            guard command.sidecars.isEmpty else { throw EngineError(message: "This conversion requires an unsupported media sidecar.") }
            let total = window.map { $0.end_seconds - $0.start_seconds } ?? source.summary.duration
            try encodeVideo(command, to: stagedURL, dolbyVision: generateDolbyVision, control: control, cleanup: &cleanup) { position in
                if total > 0 { progress(min(0.99, position / total)) }
            }
            let checked = try inspect(stagedURL, control: control, cleanup: &cleanup)
            try control.check()
            try verify(plan, source: source.snapshot, encoded: checked.snapshot)
            if let window {
                let encodedFrames = try loadFrames(checked, control: control, cleanup: &cleanup)
                guard encodedFrames.count == window.end - window.start else {
                    throw EngineError(message: "The encoded frame count differs from the selected range.")
                }
            }
        }
        progress(1)
    }

    func exportPhoto(
        _ source: MediaSource, to destination: URL, format: PhotoFormat, frame: UInt64, options: EncodingOptions,
        overwrite: Bool, index: FrameIndex? = nil, liveIdentifier: String? = nil, control: JobControl
    ) throws {
        guard format.matchesExtension(destination.pathExtension) else {
            throw EngineError(message: "The file extension does not match the photo format.")
        }
        guard format == .sdr || options.supportsHDRPhoto(source.summary) else {
            throw EngineError(message: "HDR photos require an HDR or camera Log source.")
        }
        let index = try index ?? loadFrames(source, control: control)
        let window = try index.resolve(FrameSelection(first: frame, last: frame))
        try withOutput(source, to: destination.standardizedFileURL, overwrite: overwrite, control: control) { stagedURL, cleanup in
            let image = stagedURL.deletingLastPathComponent().appendingPathComponent("still.png")
            defer { if cleanup { try? FileManager.default.removeItem(at: image) } }
            let transfer = try renderStill(source, to: image, options: options, window: window, control: control, cleanup: &cleanup)
            try format.write(to: stagedURL, frame: image, transfer: transfer, liveIdentifier: liveIdentifier, control: control)
        }
    }

    func exportLivePhoto(
        _ source: MediaSource, to destination: URL, format: PhotoFormat, frame: UInt64, options: EncodingOptions,
        range: FrameSelection?, index: FrameIndex? = nil, control: JobControl
    ) async throws {
        guard format != .hdr else { throw EngineError(message: "Choose SDR JPEG or Adaptive HDR HEIC for a Live Photo.") }
        try control.check()
        try source.identity.check(source.url)
        let index = try index ?? loadFrames(source, control: control)
        let range = try range ?? index.liveRange(around: frame)
        let window = try index.resolve(range)
        guard frame >= range.first, frame <= window.end else {
            throw EngineError(message: "The cover frame must be inside the Live Photo range.")
        }
        let destination = destination.standardizedFileURL
        guard !FileManager.default.fileExists(atPath: destination.path) else {
            throw EngineError(message: "Use a new output folder for the Live Photo pair.")
        }
        let identifier = UUID().uuidString
        let parent = destination.deletingLastPathComponent()
        let folder = parent.appendingPathComponent(".lumarecode-live-" + identifier, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        let name = destination.lastPathComponent
        let still = folder.appendingPathComponent(name + "." + format.fileExtension)
        let video = folder.appendingPathComponent(name + ".mov")
        let clip = folder.appendingPathComponent("encoded-" + identifier + ".mp4")
        var committed = false
        defer {
            if !committed {
                // A nested transaction keeps its own directory when a child
                // could not be reaped. Only remove the three known files.
                for file in [clip, still, video] { try? FileManager.default.removeItem(at: file) }
                _ = rmdir(folder.path)
            }
        }
        try exportPhoto(source, to: still, format: format, frame: frame, options: options,
                        overwrite: false, index: index, liveIdentifier: identifier, control: control)
        var motion = options
        motion.output = format == .sdr ? "sdr" : "hdr"
        motion.codec = format == .sdr ? "avc" : "hevc"
        motion.standardHDR = true
        motion.liveClip = true
        motion.encoder = .auto
        try convert(source, to: clip, options: motion, overwrite: false, index: index, selection: range, control: control)
        let encoded = try inspect(clip, control: control)
        let clock = try loadFrames(encoded, control: control)
        let coverFrame = frame - range.first + 1
        let cover = try clock.resolve(FrameSelection(first: coverFrame, last: coverFrame))
        let origin = try clock.resolve(FrameSelection(first: 1, last: 1)).start_seconds
        let coverTime = CMTimeRange(start: CMTime(seconds: cover.start_seconds - origin, preferredTimescale: 600_000),
                                    duration: CMTime(seconds: cover.end_seconds - cover.start_seconds, preferredTimescale: 600_000))
        try await LivePhoto.writeMovie(from: clip, to: video, identifier: identifier, cover: coverTime, control: control)
        try await LivePhoto.verify(still: still, movie: video, control: control)
        try FileManager.default.removeItem(at: clip)
        try control.check()
        try source.identity.check(source.url)
        // Publish both Apple resources together. Exclusive rename protects a
        // folder another app created while the photo was being prepared.
        guard renamex_np(folder.path, destination.path, UInt32(RENAME_EXCL)) == 0 else {
            throw EngineError(message: "Cannot publish the Live Photo folder. Choose a new folder name.")
        }
        committed = true
    }

    private func encodeVideo(
        _ command: PlannedCommand, to destination: URL, dolbyVision: Bool, control: JobControl,
        cleanup: inout Bool, timeout: UInt64 = 0, progress: @escaping (Double) -> Void
    ) throws {
        guard dolbyVision else {
            try runEncoder(command, control: control, cleanup: &cleanup, timeout: timeout, progress: progress)
            return
        }
        let intermediate = destination.deletingLastPathComponent().appendingPathComponent("hevc-" + UUID().uuidString + ".mp4")
        defer { if cleanup { try? FileManager.default.removeItem(at: intermediate) } }
        var encode = command
        encode.arguments[encode.arguments.count - 1] = intermediate.path
        try runEncoder(encode, control: control, cleanup: &cleanup, timeout: timeout, progress: progress)
        // The hardware encoder advertises its p010 input format. Reading the
        // coded stream back supplies planar 10-bit HEVC for the BSF's profile
        // check while keeping the original VideoToolbox-generated RPU.
        let remux = PlannedCommand(arguments: [
            encode.arguments[0], "-hide_banner", "-nostdin", "-n", "-xerror", "-loglevel", "warning",
            "-i", intermediate.path, "-map", "0", "-c", "copy",
            "-bsf:v", "dovi_rpu=compression=none", "-strict", "unofficial", "-tag:v", "hvc1",
            "-movflags", "+faststart+write_colr", destination.path
        ], working_directory: command.working_directory, sidecars: [], colors: nil)
        try runEncoder(remux, control: control, cleanup: &cleanup, timeout: timeout) { _ in }
    }

    func previewStill(_ source: MediaSource, options: EncodingOptions, window: lr_frame_window?, control: JobControl) throws -> Data {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("LumaRecode-preview-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        let still = folder.appendingPathComponent("preview.png")
        var cleanup = true
        defer {
            if cleanup {
                try? FileManager.default.removeItem(at: still)
                _ = rmdir(folder.path)
            }
        }
        _ = try renderStill(source, to: still, options: options, window: window,
                            control: control, cleanup: &cleanup, preview: true)
        try source.identity.check(source.url)
        return try Data(contentsOf: still)
    }

    private func renderStill(
        _ source: MediaSource, to destination: URL, options: EncodingOptions, window: lr_frame_window?,
        control: JobControl, cleanup: inout Bool, preview: Bool = false
    ) throws -> String {
        var render = options
        let hdr = !preview && render.supportsHDRPhoto(source.summary)
        render.output = hdr ? "hdr" : "sdr"
        render.codec = "hevc"
        render.standardHDR = true
        let choices = try render.data(for: source.summary, container: "mkv", useVideoToolbox: false)
        let plan = try makePlan(source: source.snapshot, choices: choices, staging: utf8(destination.path), window: window,
                                previewSize: preview ? 960 : 0)
        defer { lr_plan_release(plan) }
        let command = try plannedCommand(plan)
        let args = command.arguments
        guard command.sidecars.isEmpty, let input = args.firstIndex(of: "-i"), let map = args.firstIndex(of: "-map"),
              let filter = args.firstIndex(of: "-vf"), input + 1 < args.count, map + 1 < args.count, filter + 1 < args.count else {
            throw EngineError(message: "Cannot plan the selected still frame.")
        }
        // Use the conversion planner's frame selection, orientation and camera
        // color pipeline, then hand Core Image an RGB intermediate at full precision.
        let rgb = hdr
            ? "zscale=primaries=bt2020:matrix=gbr:range=full"
            : "zscale=primaries=bt709:transfer=iec61966-2-1:matrix=gbr:range=full"
        let still = PlannedCommand(
            arguments: Array(args.prefix(input + 2)) + [
                "-map", args[map + 1], "-an", "-sn", "-dn", "-map_metadata", "-1",
                "-vf", args[filter + 1] + "," + rgb + ",format=gbrp16le,format=rgb48be",
                "-frames:v", "1", "-c:v", "png", "-f", "image2", "-update", "1", destination.path
            ], working_directory: command.working_directory, sidecars: [], colors: nil
        )
        try source.identity.check(source.url)
        try runEncoder(still, control: control, cleanup: &cleanup, timeout: preview ? 20_000 : 0) { _ in }
        return command.colors?.color_transfer ?? "bt709"
    }

    // Video and still export both verify staged files before publishing them.
    // The native transaction also protects against a replaced source or target.
    private func withOutput(
        _ source: MediaSource, to destination: URL, overwrite: Bool, control: JobControl,
        _ render: (URL, inout Bool) throws -> Void
    ) throws {
        try control.check()
        try source.identity.check(source.url)
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
        let stagedURL = URL(fileURLWithPath: String(decoding: staging, as: UTF8.self))
        try render(stagedURL, &cleanup)
        try control.check()
        try source.identity.check(source.url)
        try check(lr_output_seal(transaction, cleanup ? 1 : 0, &error), error, "Cannot seal the encoded file.")
        try control.check()
        try check(lr_output_commit(transaction, &error), error, "Cannot publish the encoded file.")
        committed = true
    }

    func videoToolboxTrials(control: JobControl) throws -> [VideoToolboxTrial] {
        // Sequential trials avoid competing for hardware encoder sessions.
        try VideoToolboxFormat.formats.map { try trialVideoToolbox($0, control: control) }
    }

    private func trialVideoToolbox(_ format: VideoToolboxFormat, control: JobControl) throws -> VideoToolboxTrial {
        try control.check()
        let folder = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("lumarecode-videotoolbox-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        var cleanup = true
        defer { if cleanup { try? FileManager.default.removeItem(at: folder) } }

        let destination = folder.appendingPathComponent("trial.mp4")
        let pixel = format.depth == 8 ? "yuv420p" : "yuv420p10le"
        let primaries = format.depth == 8 ? "bt709" : "bt2020"
        let matrix = format.depth == 8 ? "bt709" : "bt2020nc"
        let sourceSignal = format.signal == "dolbyvision84" ? "hlg" : format.signal
        let transfer = sourceSignal == "sdr" ? "bt709" : sourceSignal == "hlg" ? "arib-std-b67" : "smpte2084"
        // Supply the same source descriptors as ordinary inspection. Only the
        // FFmpeg input is substituted with a generator after native planning.
        let video: [String: Any] = [
            "index": 0, "codec_type": "video", "codec_name": "rawvideo", "pix_fmt": pixel, "width": 320, "height": 192,
            "avg_frame_rate": "30/1", "color_range": "tv", "color_space": matrix, "color_primaries": primaries,
            "color_transfer": transfer
        ]
        let source = try JSONSerialization.data(withJSONObject: [
            "schema_version": 1, "path": folder.appendingPathComponent("source.mkv").path, "video": video,
            "streams": [video], "frames": [], "kind": sourceSignal, "duration": 1.0, "hdr10plus": false,
            "dolby_vision": NSNull(), "pixel": ["components": [["bit_depth": format.depth]]]
        ])
        let summary = try JSONDecoder().decode(MediaSummary.self, from: source)
        let choices = try EncodingOptions(codec: format.codec, output: format.signal == "dolbyvision84" ? format.signal : "auto")
            .data(for: summary, container: "mp4", useVideoToolbox: true)
        let plan = try makePlan(source: source, choices: choices, staging: utf8(destination.path), window: nil)
        defer { lr_plan_release(plan) }
        var command = try plannedCommand(plan)
        guard let input = command.arguments.firstIndex(of: "-i"), input + 1 < command.arguments.count else {
            throw EngineError(message: "The VideoToolbox trial has no input argument.")
        }
        command.arguments.replaceSubrange(input...input + 1, with: [
            "-f", "lavfi", "-i", "testsrc2=size=320x192:rate=30,format=\(pixel)"
        ])
        command.arguments.insert(contentsOf: ["-frames:v", "8"], at: command.arguments.count - 1)
        do {
            try encodeVideo(command, to: destination, dolbyVision: format.signal == "dolbyvision84",
                            control: control, cleanup: &cleanup, timeout: 6000) { _ in }
            let encoded = try inspect(destination, control: control, cleanup: &cleanup)
            try verify(plan, source: source, encoded: encoded.snapshot)
            return VideoToolboxTrial(format: format, available: true, diagnostic: nil)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            // A failed encoder is a normal capability result. An unconfirmed
            // child must instead end probing and keep its working directory.
            if !cleanup { throw error }
            return VideoToolboxTrial(format: format, available: false,
                                     diagnostic: String(error.localizedDescription.prefix(2000)))
        }
    }

    private func verify(_ plan: OpaquePointer, source: Data, encoded: Data) throws {
        var error = nativeError()
        let status = withViews([source, encoded]) { views in lr_plan_verify_output(plan, views[0], views[1], &error) }
        try check(status, error, "The encoded file failed verification.")
    }

    private struct PlannedCommand: Decodable {
        struct Colors: Decodable { let color_transfer: String? }
        var arguments: [String]
        let working_directory: String?
        let sidecars: [String]
        let colors: Colors?
    }

    private func plannedCommand(_ plan: OpaquePointer) throws -> PlannedCommand {
        let json = try copyBytes(limit: 1024 * 1024) { lr_plan_copy_json(plan, $0, $1, $2) }
        return try JSONDecoder().decode(PlannedCommand.self, from: json)
    }

    private func makePlan(source: Data, choices: Data, staging: Data, window: lr_frame_window?, previewSize: UInt32 = 0) throws -> OpaquePointer {
        guard let executable = tools.ffmpeg else { throw EngineError(message: "FFmpeg is required for conversion.") }
        var options = lr_plan_options()
        try check(lr_plan_options_init(&options, UInt64(MemoryLayout<lr_plan_options>.size)), nil,
                  "Cannot initialize encoding options.")
        options.preview_size = previewSize
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
        _ command: PlannedCommand, control: JobControl, cleanup: inout Bool, timeout: UInt64 = 0,
        progress: @escaping (Double) -> Void
    ) throws {
        guard let executable = command.arguments.first else { throw EngineError(message: "The encoding plan is empty.") }
        var options = lr_process_options()
        try check(lr_process_options_init(&options, UInt64(MemoryLayout<lr_process_options>.size)), nil,
                  "Cannot initialize FFmpeg.")
        options.stdout_mode = LR_CAPTURE_DISCARD
        options.timeout_ms = timeout
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
                let message = String(decoding: detail, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
                if message.contains("No such filter: 'libplacebo'") || message.contains("Vulkan device creation failed") {
                    throw EngineError(message: "Dolby Vision rendering needs FFmpeg with a working libplacebo Vulkan backend. Use the FFmpeg Full disk image or configure ffmpeg-full and MoltenVK.\n" + message)
                }
                throw EngineError(message: message)
            }
            try check(run, error, "FFmpeg failed.")
        }
    }
}
