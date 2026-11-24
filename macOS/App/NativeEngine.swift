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
        let time_base: String?
        let start_time: String?
        let _raw: RawCalibration?
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
    let format: lr_source_format
    let canEncodeFlac: Bool
    fileprivate let identity: SourceIdentity
}

struct RawCalibration: Codable, Sendable {
    let width: UInt32
    let height: UInt32
    let crop: [UInt32]
    let matrix: [Double]
    let white_balance_kelvin: UInt32
    let bayer_pattern: UInt32
    var decoder_supported = false
    var decoder_version_supported = false

    static func read(_ file: FileHandle, length: UInt64, offset: UInt64, size: UInt64) throws -> Self {
        guard size >= 86 && size <= length && offset <= length - size else {
            throw EngineError(message: "Invalid ProRes RAW packet bounds.")
        }
        try file.seek(toOffset: offset)
        guard let header = try file.read(upToCount: 86), header.count == 86 else {
            throw EngineError(message: "Incomplete ProRes RAW frame header.")
        }
        var result = lr_raw_calibration()
        try check(header.withUnsafeBytes {
            lr_media_raw_calibration($0.bindMemory(to: UInt8.self).baseAddress, UInt64($0.count), size, &result)
        }, nil, "The ProRes RAW header or calibration is unsupported.")
        return Self(width: result.width, height: result.height,
                    crop: withUnsafeBytes(of: result.crop) { Array($0.bindMemory(to: UInt32.self)) },
                    matrix: withUnsafeBytes(of: result.matrix) { Array($0.bindMemory(to: Double.self)) },
                    white_balance_kelvin: result.white_balance_kelvin, bayer_pattern: result.bayer_pattern)
    }

    var commands: String {
        matrix.enumerated().map { index, value in
            let row = ["r", "g", "b"][index / 4], column = ["r", "g", "b", "a"][index % 4]
            return "colorchannelmixer@raw " + row + column + " " + String(value)
        }.joined(separator: ",")
    }
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

    func timestamp(at index: UInt64) throws -> Int64 {
        var point = lr_frame_point()
        try check(lr_timeline_copy_points(handle, index, 1, &point, UInt64(MemoryLayout<lr_frame_point>.size)),
                  nil, "Cannot read the selected source timestamp.")
        return point.pts
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
    var bitDepth = "auto"
    var sampling = "auto"
    var colorRange = "auto"
    var audio = "auto"

    func inputKind(_ media: MediaSummary) -> String { sourceKind == "auto" ? media.kind : sourceKind }
    func supportsHDRVideo(_ media: MediaSummary) -> Bool { ["hlg", "pq", "log", "raw"].contains(inputKind(media)) }
    func supportsHDRPhoto(_ media: MediaSummary) -> Bool { supportsHDRVideo(media) }

    func formatChoices(for media: MediaSource) -> lr_format_choices? {
        var source = media.format
        let purpose = encoder == .videoToolbox ? LR_PURPOSE_SHARE : LR_PURPOSE_ARCHIVE
        let selected = codec == "avc" ? LR_CODEC_AVC : codec == "av1" ? LR_CODEC_AV1 : LR_CODEC_HEVC
        let hdr = output == "hdr" || output == "dolbyvision84" || (output == "auto" && supportsHDRVideo(media.summary))
        var choices = lr_format_choices()
        let size = UInt64(MemoryLayout<lr_format_choices>.size)
        guard lr_format_choices_init(&choices, size) == LR_OK,
              lr_get_format_choices(&source, purpose, selected, hdr ? LR_DYNAMIC_RANGE_HDR : LR_DYNAMIC_RANGE_SDR,
                                    &choices, nil) == LR_OK else { return nil }
        return choices
    }

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
        if media.kind == "raw", media.video._raw?.decoder_supported != true {
            throw EngineError(message: "This FFmpeg build cannot decode the selected ProRes RAW recording.")
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
            "container": container, "cpu_tone_map": "1", "bit_depth": bitDepth,
            "sampling": sampling, "color_range": colorRange, "audio": audio
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
        return try MediaTools(ffmpeg: requireEncoder ? find("ffmpeg", ffmpeg) : try? find("ffmpeg", ffmpeg),
                              ffprobe: find("ffprobe", ffprobe))
    }
}

// Receives FFmpeg's line-oriented progress on the native worker thread. It
// keeps only the partial line and limits that buffer even for a malformed tool.
private final class ProgressReader {
    var pending = [UInt8]()
    var droppingLine = false
    var failure: Error?
    let update: (Double) -> Void
    let receive: ((String) throws -> Void)?
    init(_ update: @escaping (Double) -> Void, receive: ((String) throws -> Void)?) {
        self.update = update
        self.receive = receive
    }

    func feed(_ bytes: UnsafeBufferPointer<UInt8>) throws {
        for byte in bytes {
            if byte == 10 {
                if let receive {
                    try receive(String(decoding: pending, as: UTF8.self))
                } else if !droppingLine, pending.starts(with: "out_time_us=".utf8),
                   let micros = Int64(String(decoding: pending.dropFirst(12), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)) {
                    update(max(0, Double(micros) / 1_000_000))
                }
                pending.removeAll(keepingCapacity: true)
                droppingLine = false
            } else if pending.count < (receive == nil ? 4096 : 65_536) && !droppingLine {
                pending.append(byte)
            } else if receive != nil {
                throw EngineError(message: "A media metadata line exceeded 64 KiB.")
            } else {
                droppingLine = true
            }
        }
    }
}

// x265 reads one record per display frame. Retain only the current frame and
// emit the same integer code values as the Windows metadata writer.
private struct HDR10PlusWriter {
    private let fields: Set<String> = ["application version", "num_windows", "targeted_system_display_maximum_luminance",
        "maxscl", "average_maxrgb", "num_distribution_maxrgb_percentiles", "distribution_maxrgb_percentage",
        "distribution_maxrgb_percentile", "fraction_bright_pixels", "knee_point_x", "knee_point_y",
        "num_bezier_curve_anchors", "bezier_curve_anchors"]
    private let output: FileHandle
    private let window: lr_frame_window?
    private var inputIndex: UInt64 = 0
    private var sequence: UInt64 = 0
    private var scene = -1
    private var sceneStart: UInt64 = 0
    private var profile: String?
    private var previous: Data?

    init(_ output: FileHandle, window: lr_frame_window?) throws {
        self.output = output
        self.window = window
        try output.write(contentsOf: Data("{\"SceneInfo\":[".utf8))
    }

    private func invalid(_ field: String) -> EngineError {
        EngineError(message: "Cannot preserve HDR10+ metadata (\(field)). Choose Standard HDR.")
    }

    mutating func append(_ line: String) throws {
        guard line == "frame" || line.hasPrefix("frame|") else { return }
        let index = inputIndex
        inputIndex += 1
        if let window, index < window.start || index >= window.end { return }
        var values = [String: [Substring]]()
        var selected = false, found = false
        for field in line.split(separator: "|") {
            let pair = field.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            guard pair.count == 2, let key = pair[0].split(separator: ":").last else { continue }
            if key == "side_data_type" {
                selected = pair[1] == "HDR Dynamic Metadata SMPTE2094-40 (HDR10+)"
                if selected && found { throw invalid("multiple records on a frame") }
                found = found || selected
            } else if selected {
                let fieldName = String(key)
                guard fields.contains(fieldName) else { throw invalid(fieldName) }
                values[fieldName, default: []].append(pair[1])
            }
        }
        guard found else { throw invalid("frame without HDR10+") }
        func many(_ key: String, _ scale: UInt64, _ maximum: UInt64) throws -> [UInt64] {
            try (values[key] ?? []).map { text in
                let parts = text.split(separator: "/", omittingEmptySubsequences: false)
                guard parts.count == 1 || parts.count == 2, let numerator = UInt64(parts[0]),
                      let denominator = parts.count == 1 ? 1 : UInt64(parts[1]), denominator > 0 else {
                    throw invalid(key)
                }
                let value = numerator.multipliedReportingOverflow(by: scale)
                guard !value.overflow, value.partialValue % denominator == 0,
                      value.partialValue / denominator <= maximum else { throw invalid(key) }
                return value.partialValue / denominator
            }
        }
        func one(_ key: String, _ scale: UInt64 = 1, _ maximum: UInt64 = 100_000) throws -> UInt64 {
            let entries = try many(key, scale, maximum)
            guard entries.count == 1 else { throw invalid(key) }
            return entries[0]
        }
        guard try one("application version") == 1, try one("num_windows") == 1,
              try one("fraction_bright_pixels", 1000, 1000) == 0 else { throw invalid("processing window") }
        let target = try one("targeted_system_display_maximum_luminance", 1, 10_000)
        let maxScl = try many("maxscl", 100_000, 100_000)
        let distributionIndices = try many("distribution_maxrgb_percentage", 1, 100)
        let distributionValues = try many("distribution_maxrgb_percentile", 100_000, 100_000)
        let count = try one("num_distribution_maxrgb_percentiles", 1, 15)
        guard maxScl.count == 3, distributionIndices.count == Int(count), distributionValues.count == Int(count),
              distributionIndices == Set(distributionIndices).sorted() else { throw invalid("luminance distribution") }
        let luminance: [String: Any] = ["AverageRGB": try one("average_maxrgb", 100_000), "MaxScl": maxScl,
            "LuminanceDistributions": ["DistributionIndex": distributionIndices, "DistributionValues": distributionValues]]
        var metadata: [String: Any] = ["LuminanceParameters": luminance, "NumberOfWindows": 1,
                                       "TargetedSystemDisplayMaximumLuminance": target]
        let curve = values["knee_point_x"] != nil
        if curve {
            let anchors = try many("bezier_curve_anchors", 1023, 1023)
            let count = try one("num_bezier_curve_anchors", 1, 15)
            guard target > 0, count > 0, anchors.count == Int(count) else { throw invalid("Bezier curve") }
            metadata["BezierCurveData"] = ["Anchors": anchors, "KneePointX": try one("knee_point_x", 4095, 4095),
                                           "KneePointY": try one("knee_point_y", 4095, 4095)] as [String: Any]
        } else if target != 0 || ["knee_point_y", "num_bezier_curve_anchors", "bezier_curve_anchors"].contains(where: { values[$0] != nil }) {
            throw invalid("incomplete tone-mapping curve")
        }
        let currentProfile = curve ? "B" : "A"
        guard profile == nil || profile == currentProfile else { throw invalid("mixed profiles") }
        profile = currentProfile
        let current = try JSONSerialization.data(withJSONObject: metadata, options: .sortedKeys)
        if current != previous {
            scene += 1
            sceneStart = sequence
            previous = current
        }
        metadata["SceneId"] = scene
        metadata["SceneFrameIndex"] = sequence - sceneStart
        metadata["SequenceFrameIndex"] = sequence
        if sequence > 0 { try output.write(contentsOf: Data([44])) }
        try output.write(contentsOf: JSONSerialization.data(withJSONObject: metadata))
        sequence += 1
    }

    func finish() throws {
        guard let profile else { throw invalid("no HDR10+ frames") }
        if let window, sequence != window.end - window.start { throw invalid("frame count") }
        try output.write(contentsOf: Data("],\"JSONInfo\":{\"HDR10plusProfile\":\"\(profile)\",\"Version\":\"1.0\"}}".utf8))
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
        var data = try copyBytes(limit: 64 * 1024 * 1024) { lr_probe_copy_json(handle, $0, $1, $2) }
        var summary = try JSONDecoder().decode(MediaSummary.self, from: data)
        if summary.kind == "raw", let stream = summary.video.index, tools.ffmpeg != nil {
            data = try calibrateRAW(data, source: url, stream: stream, control: control, cleanup: &cleanup)
            summary = try JSONDecoder().decode(MediaSummary.self, from: data)
        }
        var format = lr_source_format()
        try check(lr_source_format_init(&format, UInt64(MemoryLayout<lr_source_format>.size)), nil,
                  "Cannot initialize source format.")
        let formatStatus = withViews([data]) { lr_media_source_format($0[0], LR_RANGE_AUTO, &format, &error) }
        try check(formatStatus, error, "Cannot determine source precision.")
        let canEncodeFlac = try Self.canEncodeFlac(data, summary: summary)
        try identity.check(url)
        return MediaSource(url: url, snapshot: data, summary: summary, format: format, canEncodeFlac: canEncodeFlac,
                           identity: identity)
    }

    private static func canEncodeFlac(_ snapshot: Data, summary: MediaSummary) throws -> Bool {
        guard summary.streams.contains(where: { $0.codec_type == "audio" }),
              let object = try JSONSerialization.jsonObject(with: snapshot) as? [String: Any],
              let streams = object["streams"] as? [[String: Any]] else { return false }
        for stream in streams where stream["codec_type"] as? String == "audio" {
            let data = try JSONSerialization.data(withJSONObject: stream)
            var depth: UInt32 = 0
            if withViews([data], { lr_media_flac_depth($0[0], &depth, nil) }) != LR_OK { return false }
        }
        return true
    }

    private static func object(_ json: Data) throws -> [String: Any] {
        guard let result = try JSONSerialization.jsonObject(with: json) as? [String: Any] else {
            throw EngineError(message: "Expected media metadata as a JSON object.")
        }
        return result
    }

    private func captureTool(_ executable: URL, _ arguments: [String], control: JobControl,
                             cleanup: inout Bool) throws -> Data {
        let command = PlannedCommand(arguments: [executable.path] + arguments, working_directory: nil, sidecars: [], colors: nil)
        return try runEncoder(command, control: control, cleanup: &cleanup, timeout: 30_000, captureOutput: true) { _ in }
    }

    private func calibrateRAW(_ snapshot: Data, source: URL, stream: Int,
                              control: JobControl, cleanup: inout Bool) throws -> Data {
        guard let ffmpeg = tools.ffmpeg else { return snapshot }
        let data = try captureTool(tools.ffprobe, ["-v", "error", "-select_streams", String(stream),
            "-read_intervals", "%+#1", "-show_packets", "-show_entries", "packet=pos,size", "-of", "json", source.path],
            control: control, cleanup: &cleanup)
        let packets = try Self.object(data)["packets"] as? [[String: Any]]
        guard let packet = packets?.first,
              let offset = Self.unsigned(packet["pos"]), let size = Self.unsigned(packet["size"]) else {
            throw EngineError(message: "Cannot locate the first ProRes RAW packet.")
        }
        let file = try FileHandle(forReadingFrom: source)
        defer { try? file.close() }
        var raw = try RawCalibration.read(file, length: file.seekToEnd(), offset: offset, size: size)
        let version = try captureTool(ffmpeg, ["-version"], control: control, cleanup: &cleanup)
        let versionText = String(decoding: version, as: UTF8.self)
        raw.decoder_version_supported = versionText.range(of: #"libavcodec\s+(6[3-9]|[7-9][0-9]|[0-9]{3,})\."#, options: .regularExpression) != nil
        if raw.decoder_version_supported {
            do {
                _ = try captureTool(ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-xerror",
                    "-threads:v", "2", "-i", source.path, "-map", "0:\(stream)", "-frames:v", "1",
                    "-an", "-sn", "-dn", "-f", "null", "-"], control: control, cleanup: &cleanup)
                raw.decoder_supported = true
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                if !cleanup { throw error }
            }
        }
        var result = try Self.object(snapshot)
        var video = result["video"] as? [String: Any] ?? [:]
        video["_raw"] = try Self.object(JSONEncoder().encode(raw))
        result["video"] = video
        return try JSONSerialization.data(withJSONObject: result)
    }

    private static func unsigned(_ value: Any?) -> UInt64? {
        if let text = value as? String { return UInt64(text) }
        if let number = value as? NSNumber { return UInt64(number.stringValue) }
        return nil
    }

    private func writeRawCommands(_ source: MediaSource, to sidecar: URL, window: lr_frame_window?,
                                  control: JobControl, cleanup: inout Bool) throws {
        guard let raw = source.summary.video._raw, raw.decoder_supported,
              let stream = source.summary.video.index else {
            throw EngineError(message: "Missing ProRes RAW calibration.")
        }
        let timeBase = (source.summary.video.time_base ?? "").split(separator: "/").compactMap { Double($0) }
        guard timeBase.count == 2, timeBase[0] > 0, timeBase[1] > 0 else {
            throw EngineError(message: "Invalid ProRes RAW time base.")
        }
        let origin = window.flatMap { $0.seek_frame > 0 ? $0.seek_seconds : nil }
        let start = origin ?? Double(source.summary.video.start_time ?? "") ?? 0
        let input = try FileHandle(forReadingFrom: source.url)
        defer { try? input.close() }
        let length = try input.seekToEnd()
        guard FileManager.default.createFile(atPath: sidecar.path, contents: nil) else {
            throw EngineError(message: "Cannot create the RAW calibration sidecar.")
        }
        let output = try FileHandle(forWritingTo: sidecar)
        defer { try? output.close() }
        var args = ["-v", "error", "-select_streams", String(stream), "-show_packets",
                    "-show_entries", "packet=pos,size,pts", "-of", "compact=p=0:nk=0"]
        if let window {
            args += ["-read_intervals", "\(window.seek_frame > 0 ? window.seek_seconds : 0)%\(window.end_seconds + 0.1)"]
        }
        let command = PlannedCommand(arguments: [tools.ffprobe.path] + args + [source.url.path],
                                     working_directory: nil, sidecars: [], colors: nil)
        var previous: String?
        try runEncoder(command, control: control, cleanup: &cleanup, receive: { line in
            if line.isEmpty { return }
            let values = Dictionary(line.split(separator: "|").compactMap { field -> (Substring, Substring)? in
                let pair = field.split(separator: "=", maxSplits: 1)
                return pair.count == 2 ? (pair[0], pair[1]) : nil
            }, uniquingKeysWith: { _, last in last })
            guard let offset = values["pos"].flatMap(UInt64.init), let size = values["size"].flatMap(UInt64.init),
                  let pts = values["pts"].flatMap(Int64.init) else {
                throw EngineError(message: "Missing ProRes RAW packet coordinates.")
            }
            let calibration = try RawCalibration.read(input, length: length, offset: offset, size: size)
            guard calibration.width == raw.width, calibration.height == raw.height, calibration.crop == raw.crop else {
                throw EngineError(message: "The ProRes RAW crop changes during the recording.")
            }
            let next = calibration.commands
            if next != previous {
                let time = previous == nil ? 0 : max(0, Double(pts) * timeBase[0] / timeBase[1] - start)
                let timestamp = String(format: "%.12f", locale: Locale(identifier: "en_US_POSIX"), time)
                try output.write(contentsOf: Data("\(timestamp) \(next);\n".utf8))
                previous = next
            }
        }) { _ in }
        guard previous != nil else { throw EngineError(message: "No ProRes RAW calibration packets were read.") }
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
        let trim = try selection.map { selection in
            let resolved = try index ?? loadFrames(source, control: control)
            return (index: resolved, window: try resolved.resolve(selection))
        }
        let window = trim?.window
        try withOutput(source, to: destination, overwrite: overwrite, control: control) { stagedURL, cleanup in
            var verificationSource = source.snapshot
            if (source.summary.hasDolbyVision || source.summary.hdr10plus) && !options.standardHDR && options.output != "sdr",
               let trim, trim.window.start != 0 {
                verificationSource = try firstSelectedFrame(source, index: trim.index, window: trim.window,
                                                            control: control, cleanup: &cleanup)
            }
            let plan = try makePlan(source: source.snapshot, choices: choices, staging: utf8(stagedURL.path), window: window)
            defer { lr_plan_release(plan) }
            let command = try plannedCommand(plan)
            let folder = stagedURL.deletingLastPathComponent()
            defer { if cleanup { self.removeSidecars(command, from: folder) } }
            try prepareSidecars(command, source: source, in: folder, window: window, control: control, cleanup: &cleanup)
            let total = window.map { $0.end_seconds - $0.start_seconds } ?? source.summary.duration
            try encodeVideo(command, to: stagedURL, dolbyVision: generateDolbyVision, control: control, cleanup: &cleanup) { position in
                if total > 0 { progress(min(0.99, position / total)) }
            }
            let checked = try inspect(stagedURL, control: control, cleanup: &cleanup)
            try control.check()
            try verify(plan, source: verificationSource, encoded: checked.snapshot)
            if let window {
                let encodedFrames = try loadFrames(checked, control: control, cleanup: &cleanup)
                guard encodedFrames.count == window.end - window.start else {
                    throw EngineError(message: "The encoded frame count differs from the selected range.")
                }
            }
        }
        progress(1)
    }

    private func removeSidecars(_ command: PlannedCommand, from folder: URL) {
        for name in command.sidecars where name == "raw-calibration.cmd" || name == "hdr10plus.json" {
            try? FileManager.default.removeItem(at: folder.appendingPathComponent(name))
        }
    }

    private func prepareSidecars(_ command: PlannedCommand, source: MediaSource, in folder: URL, window: lr_frame_window?,
                                 control: JobControl, cleanup: inout Bool) throws {
        for name in command.sidecars {
            let sidecar = folder.appendingPathComponent(name)
            switch name {
            case "raw-calibration.cmd":
                try writeRawCommands(source, to: sidecar, window: window, control: control, cleanup: &cleanup)
            case "hdr10plus.json":
                guard let stream = source.summary.video.index,
                      FileManager.default.createFile(atPath: sidecar.path, contents: nil) else {
                    throw EngineError(message: "Cannot create the HDR10+ metadata sidecar.")
                }
                let file = try FileHandle(forWritingTo: sidecar)
                defer { try? file.close() }
                var writer = try HDR10PlusWriter(file, window: window)
                var args = [tools.ffprobe.path, "-v", "error", "-select_streams", String(stream)]
                if let window, window.end_seconds > 0 { args += ["-read_intervals", "%\(window.end_seconds + 1)"] }
                args += ["-show_frames", "-show_entries", "frame=side_data_list:frame_side_data", "-of", "compact", source.url.path]
                let probe = PlannedCommand(arguments: args, working_directory: nil, sidecars: [], colors: nil)
                try runEncoder(probe, control: control, cleanup: &cleanup, receive: { try writer.append($0) }) { _ in }
                try writer.finish()
            default:
                throw EngineError(message: "Unsupported media sidecar.")
            }
        }
    }

    private func firstSelectedFrame(
        _ source: MediaSource, index: FrameIndex, window: lr_frame_window,
        control: JobControl, cleanup: inout Bool
    ) throws -> Data {
        guard let stream = source.summary.video.index else {
            throw EngineError(message: "Missing the source video stream index.")
        }
        let timestamp = try index.timestamp(at: window.start)
        let interval = "\(window.start_seconds)%\(window.start_seconds + 1)"
        let command = PlannedCommand(arguments: [
            tools.ffprobe.path, "-v", "error", "-select_streams", String(stream), "-read_intervals", interval,
            "-show_frames", "-of", "json", source.url.path
        ], working_directory: nil, sidecars: [], colors: nil)
        let data = try runEncoder(command, control: control, cleanup: &cleanup, timeout: 25_000, captureOutput: true) { _ in }
        func object(_ json: Data) throws -> [String: Any] {
            try JSONSerialization.jsonObject(with: json) as? [String: Any] ?? [:]
        }
        let frames = try object(data)["frames"] as? [[String: Any]] ?? []
        let matches = frames.filter { frame in
            (frame["best_effort_timestamp"] as? NSNumber)?.int64Value == timestamp
                || (frame["best_effort_timestamp"] as? String).flatMap(Int64.init) == timestamp
        }
        guard matches.count == 1, let first = matches.first else {
            throw EngineError(message: "Cannot verify the dynamic HDR metadata of the selected start frame.")
        }
        var reference = try object(source.snapshot)
        reference["frames"] = [first]
        return try JSONSerialization.data(withJSONObject: reference)
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
        motion.bitDepth = "auto"
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
        render.bitDepth = "auto"
        render.sampling = "auto"
        render.colorRange = "auto"
        render.audio = "auto"
        let choices = try render.data(for: source.summary, container: "mkv", useVideoToolbox: false)
        let plan = try makePlan(source: source.snapshot, choices: choices, staging: utf8(destination.path), window: window,
                                previewSize: preview ? 960 : 0)
        defer { lr_plan_release(plan) }
        let command = try plannedCommand(plan)
        let folder = destination.deletingLastPathComponent()
        defer { if cleanup { self.removeSidecars(command, from: folder) } }
        try prepareSidecars(command, source: source, in: folder, window: window, control: control, cleanup: &cleanup)
        let args = command.arguments
        guard let input = args.firstIndex(of: "-i"), let map = args.firstIndex(of: "-map"),
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

    @discardableResult private func runEncoder(
        _ command: PlannedCommand, control: JobControl, cleanup: inout Bool, timeout: UInt64 = 0,
        captureOutput: Bool = false, receive: ((String) throws -> Void)? = nil,
        progress: @escaping (Double) -> Void
    ) throws -> Data {
        guard let executable = command.arguments.first else { throw EngineError(message: "The encoding plan is empty.") }
        var options = lr_process_options()
        try check(lr_process_options_init(&options, UInt64(MemoryLayout<lr_process_options>.size)), nil,
                  "Cannot initialize FFmpeg.")
        options.stdout_mode = captureOutput ? LR_CAPTURE_FULL : LR_CAPTURE_DISCARD
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
        let reader = ProgressReader(progress, receive: receive)
        return try control.run({ _ = lr_process_cancel(handle) }) {
            let run = captureOutput ? lr_process_run(handle, &error) : withExtendedLifetime(reader) {
                lr_process_run_streaming(handle, { context, stream, bytes, count in
                    if stream == LR_STREAM_STDOUT, let context {
                        let reader = Unmanaged<ProgressReader>.fromOpaque(context).takeUnretainedValue()
                        do {
                            try reader.feed(UnsafeBufferPointer(start: bytes, count: Int(count)))
                        } catch {
                            reader.failure = error
                            return LR_IO_ERROR
                        }
                    }
                    return LR_OK
                }, Unmanaged.passUnretained(reader).toOpaque(), &error)
            }
            var state = lr_process_snapshot()
            lr_process_snapshot_init(&state, UInt64(MemoryLayout<lr_process_snapshot>.size))
            cleanup = lr_process_get_snapshot(handle, &state) == LR_OK && state.cleanup_state == LR_CLEANUP_CONFIRMED
            if cleanup, let failure = reader.failure { throw failure }
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
            if let receive, !reader.pending.isEmpty {
                try receive(String(decoding: reader.pending, as: UTF8.self))
            }
            return try captureOutput ? copyBytes(limit: 8 * 1024 * 1024) {
                lr_process_copy_output(handle, LR_STREAM_STDOUT, $0, $1, $2)
            } : Data()
        }
    }
}
