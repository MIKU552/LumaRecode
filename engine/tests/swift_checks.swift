import CLumaRecode
import Foundation

func require(_ condition: Bool, _ message: String) {
    guard condition else {
        FileHandle.standardError.write(Data((message + "\n").utf8))
        exit(1)
    }
}

require(lr_abi_version() == LR_ABI_VERSION, "Swift/native ABI version differs")
var source = lr_source_format()
var options = lr_encode_options()
var result = lr_resolved_format()
var error = lr_error()
require(lr_source_format_init(&source, UInt64(MemoryLayout<lr_source_format>.size)) == LR_OK, "Source init")
require(lr_encode_options_init(&options, UInt64(MemoryLayout<lr_encode_options>.size)) == LR_OK, "Options init")
require(lr_resolved_format_init(&result, UInt64(MemoryLayout<lr_resolved_format>.size)) == LR_OK, "Result init")
require(lr_error_init(&error, UInt64(MemoryLayout<lr_error>.size)) == LR_OK, "Error init")
source.bit_depth = 8
source.sampling = LR_SAMPLING_420
source.color_range = LR_RANGE_FULL
source.dynamic_range = LR_DYNAMIC_RANGE_SDR
options.purpose = LR_PURPOSE_ARCHIVE
require(lr_resolve_format(&source, &options, &result, &error) == LR_OK, "Archive policy")
require(result.bit_depth == 8 && result.sampling == LR_SAMPLING_420 && result.color_range == LR_RANGE_FULL,
        "Archive must retain source precision and range")
options.purpose = LR_PURPOSE_SHARE
require(lr_resolve_format(&source, &options, &result, &error) == LR_OK, "Sharing policy")
require(result.color_range == LR_RANGE_LIMITED, "Sharing must default to limited range")
require(MemoryLayout<lr_hdr10plus_metadata>.size == 236, "Swift HDR layout differs")
var metadata = lr_hdr10plus_metadata()
require(lr_hdr10plus_metadata_init(&metadata, 236) == LR_OK, "HDR metadata init")
let payload: [UInt8] = [0xb5, 0x00, 0x3c, 0x00, 0x01, 0x04, 0x01, 0x40, 0x00, 0x00,
    0x00, 0x07, 0xd0, 0x07, 0xd0, 0x05, 0xdc, 0x01, 0x77, 0x08, 0x08, 0x07,
    0xd3, 0x20, 0x2e, 0xe0, 0x00, 0x00]
let parsed = payload.withUnsafeBufferPointer { bytes in
    lr_hdr10plus_parse_t35(bytes.baseAddress, UInt64(bytes.count), &metadata, &error)
}
require(parsed == LR_OK && metadata.average_rgb == 1500 && metadata.max_scl.2 == 3000,
        "Swift/C++/Rust HDR integer codes differ")
print("Swift/C/C++/Rust ABI, output-policy and HDR checks passed.")

require(MemoryLayout<lr_frame_point>.size == 24 && MemoryLayout<lr_frame_window>.size == 56 &&
        MemoryLayout<lr_timeline_options>.size == 96 && MemoryLayout<lr_timeline_snapshot>.size == 64,
        "Swift timeline layout differs")
var framePoints = [lr_frame_point(pts: 0, duration: 10, key_frame: 1, reserved: 0),
                   lr_frame_point(pts: 10, duration: 20, key_frame: 0, reserved: 0),
                   lr_frame_point(pts: 30, duration: 15, key_frame: 1, reserved: 0)]
var frameTimeline: OpaquePointer?
let timelineImported = framePoints.withUnsafeBufferPointer { points in
    lr_timeline_from_points(points.baseAddress, UInt64(points.count), 1, 100, LR_TIMELINE_PACKETS,
                            &frameTimeline, &error)
}
require(timelineImported == LR_OK, "Swift timeline import failed")
var frameWindow = lr_frame_window()
require(lr_frame_window_init(&frameWindow, 56) == LR_OK, "Swift frame window init")
require(lr_timeline_resolve(frameTimeline, 1, UInt64.max, &frameWindow) == LR_OK &&
        frameWindow.seek_frame == 0 && frameWindow.start_seconds == 0.1 && frameWindow.end_seconds == 0.45,
        "Swift VFR selection differs")
var timePosition: UInt64 = 99
require(lr_timeline_at_or_after(frameTimeline, 0.11, &timePosition) == LR_OK && timePosition == 2,
        "Swift frame time lookup differs")
lr_timeline_release(frameTimeline)

require(CommandLine.arguments.count == 2, "Expected the native process helper path")
require(MemoryLayout<lr_process_options>.size == 80 && MemoryLayout<lr_process_snapshot>.size == 64,
        "Swift process layout differs")
var processOptions = lr_process_options()
require(lr_process_options_init(&processOptions, 80) == LR_OK, "Process options init")
processOptions.stdout_capacity = 8192
let executable = Array(CommandLine.arguments[1].utf8)
let echo = Array("echo".utf8)
let word = Array("Swift 日本語 🌈".utf8)
var nativeProcess: OpaquePointer?
let created = executable.withUnsafeBufferPointer { path in
    echo.withUnsafeBufferPointer { mode in
        word.withUnsafeBufferPointer { text in
            processOptions.executable = lr_utf8_view(data: path.baseAddress, size: UInt64(path.count))
            let views = [lr_utf8_view(data: mode.baseAddress, size: UInt64(mode.count)),
                         lr_utf8_view(data: text.baseAddress, size: UInt64(text.count))]
            return views.withUnsafeBufferPointer { arguments in
                processOptions.arguments = arguments.baseAddress
                processOptions.argument_count = UInt32(arguments.count)
                return lr_process_create(&processOptions, &nativeProcess, &error)
            }
        }
    }
}
require(created == LR_OK, "Swift process creation")
defer { lr_process_release(nativeProcess) }
// All borrowed pointers are out of scope; create() retained its own copies.
let executed = lr_process_run(nativeProcess, &error)
let diagnostic = withUnsafeBytes(of: error.message) { bytes in
    String(decoding: bytes.prefix(while: { $0 != 0 }), as: UTF8.self)
}
require(executed == LR_OK, "Swift native process execution: \(diagnostic)")
var processState = lr_process_snapshot()
require(lr_process_snapshot_init(&processState, 64) == LR_OK, "Process snapshot init")
require(lr_process_get_snapshot(nativeProcess, &processState) == LR_OK
        && processState.state == LR_PROCESS_FINISHED && processState.exit_code == 0
        && processState.cleanup_state == LR_CLEANUP_CONFIRMED, "Swift process state")
var outputBytes = [UInt8](repeating: 0, count: Int(processState.stdout_retained))
var required: UInt64 = 0
let copied = outputBytes.withUnsafeMutableBufferPointer { bytes in
    lr_process_copy_output(nativeProcess, LR_STREAM_STDOUT, bytes.baseAddress,
                           UInt64(bytes.count), &required)
}
require(copied == LR_OK && required == UInt64(outputBytes.count)
        && String(decoding: outputBytes, as: UTF8.self) == "\(word.count):Swift 日本語 🌈\n",
        "Swift process output bytes differ")
print("Swift native process ownership and UTF-8 invocation checks passed.")

require(MemoryLayout<lr_output_options>.size == 48 && MemoryLayout<lr_output_snapshot>.size == 32,
        "Swift output layout differs")
let temporaryParent = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
let outputRoot = temporaryParent.appendingPathComponent("LumaRecode-swift-output-" + UUID().uuidString)
try FileManager.default.createDirectory(at: outputRoot, withIntermediateDirectories: false)
defer { try? FileManager.default.removeItem(at: outputRoot) }
let sourceURL = outputRoot.appendingPathComponent("原片.dat")
let destinationURL = outputRoot.appendingPathComponent("日本語.mkv")
try Data("source".utf8).write(to: sourceURL)
let sourcePath = Array(sourceURL.path.utf8), destinationPath = Array(destinationURL.path.utf8)
var outputOptions = lr_output_options()
require(lr_output_options_init(&outputOptions, 48) == LR_OK, "Output options init")
var transaction: OpaquePointer?
let outputCreated = sourcePath.withUnsafeBufferPointer { first in
    destinationPath.withUnsafeBufferPointer { second in
        outputOptions.source = lr_utf8_view(data: first.baseAddress, size: UInt64(first.count))
        outputOptions.destination = lr_utf8_view(data: second.baseAddress, size: UInt64(second.count))
        return lr_output_create(&outputOptions, &transaction, &error)
    }
}
require(outputCreated == LR_OK, "Swift output transaction creation")
defer { lr_output_release(transaction) }
var pathSize: UInt64 = 0
require(lr_output_copy_path(transaction, LR_OUTPUT_PATH_STAGING, nil, 0, &pathSize) == LR_BUFFER_TOO_SMALL,
        "Swift output path size")
var pathBytes = [UInt8](repeating: 0, count: Int(pathSize))
require(pathBytes.withUnsafeMutableBufferPointer { bytes in
    lr_output_copy_path(transaction, LR_OUTPUT_PATH_STAGING, bytes.baseAddress, UInt64(bytes.count), &pathSize)
} == LR_OK, "Swift output path copy")
let stagingURL = URL(fileURLWithPath: String(decoding: pathBytes, as: UTF8.self))
try Data("verified output".utf8).write(to: stagingURL)
require(lr_output_seal(transaction, 1, &error) == LR_OK, "Swift output seal")
require(lr_output_commit(transaction, &error) == LR_OK, "Swift output publication")
var outputState = lr_output_snapshot()
require(lr_output_snapshot_init(&outputState, 32) == LR_OK, "Output snapshot init")
require(lr_output_get_snapshot(transaction, &outputState) == LR_OK
        && outputState.state == LR_OUTPUT_COMMITTED && outputState.flags == 0 && outputState.output_size == 15,
        "Swift committed output state")
let publishedData = try Data(contentsOf: destinationURL), retainedSource = try Data(contentsOf: sourceURL)
require(publishedData == Data("verified output".utf8) && retainedSource == Data("source".utf8),
        "Swift publication changed bytes")
print("Swift file transaction ownership and publication checks passed.")

require(MemoryLayout<lr_classify_input>.size == 88 && MemoryLayout<lr_classification>.size == 24
        && MemoryLayout<lr_dji_color>.size == 24, "Swift media layout differs")
var classifyInput = lr_classify_input(), classification = lr_classification()
require(lr_classify_input_init(&classifyInput, 88) == LR_OK, "Classification input init")
require(lr_classification_init(&classification, 24) == LR_OK, "Classification result init")
classifyInput.flags = LR_CLASSIFY_HAS_DJI
classifyInput.dji_mode = 19
let djiSchema = Array("dvtm_PP-101.proto".utf8), transfer = Array("bt709".utf8)
let classified = djiSchema.withUnsafeBufferPointer { schema in
    transfer.withUnsafeBufferPointer { curve in
        classifyInput.dji_schema = lr_utf8_view(data: schema.baseAddress, size: UInt64(schema.count))
        classifyInput.transfer = lr_utf8_view(data: curve.baseAddress, size: UInt64(curve.count))
        return lr_media_classify(&classifyInput, &classification, &error)
    }
}
require(classified == LR_OK && classification.kind == LR_MEDIA_LOG
        && classification.profile == LR_PROFILE_DJI_POCKET3 && classification.evidence == LR_EVIDENCE_DJI_DLOG_M,
        "Swift camera evidence precedence differs")
let spsBytes: [UInt8] = [0, 0, 1, 0x67, 100, 0, 40, 0xc0]
var monochrome: UInt32 = 0
require(spsBytes.withUnsafeBufferPointer { bytes in
    lr_avc_is_monochrome(bytes.baseAddress, UInt64(bytes.count), &monochrome, &error)
} == LR_OK && monochrome == 1, "Swift AVC metadata reader")
let hexDump = Array("00000000: 0000 0167 6400 28c0  ABCDEF is ASCII".utf8)
var decoded = [UInt8](repeating: 0, count: 8)
var decodedSize: UInt64 = 0
let decodedStatus = hexDump.withUnsafeBufferPointer { dump in
    decoded.withUnsafeMutableBufferPointer { bytes in
        lr_probe_decode_hex(lr_utf8_view(data: dump.baseAddress, size: UInt64(dump.count)),
                            bytes.baseAddress, UInt64(bytes.count), &decodedSize)
    }
}
require(decodedStatus == LR_OK && decodedSize == 8 && decoded == spsBytes, "Swift C hex dump decoder")
print("Swift classification and C/Rust camera metadata checks passed.")

func cameraField(_ number: UInt8, _ payload: [UInt8]) -> [UInt8] {
    require(payload.count < 128, "Swift camera fixture length")
    return [number << 3 | 2, UInt8(payload.count)] + payload
}
let djiPacket = cameraField(1, cameraField(1, cameraField(1, djiSchema)))
    + cameraField(2, cameraField(4, [8, 19]))
var djiColor = lr_dji_color()
require(lr_dji_color_init(&djiColor, 24) == LR_OK, "Swift DJI output init")
require(djiPacket.withUnsafeBufferPointer { bytes in
    lr_dji_parse_color(bytes.baseAddress, UInt64(bytes.count), &djiColor, &error)
} == LR_OK && djiColor.schema == LR_DJI_POCKET3 && djiColor.mode == 19, "Swift DJI packet parsing")

require(MemoryLayout<lr_probe_options>.size == 64 && MemoryLayout<lr_probe_snapshot>.size == 64,
        "Swift probe layout differs")
var inspectionOptions = lr_probe_options()
require(lr_probe_options_init(&inspectionOptions, 64) == LR_OK, "Swift probe options init")
var inspection: OpaquePointer?
let inspectionCreated = executable.withUnsafeBufferPointer { tool in
    sourcePath.withUnsafeBufferPointer { source in
        inspectionOptions.ffprobe = lr_utf8_view(data: tool.baseAddress, size: UInt64(tool.count))
        inspectionOptions.source = lr_utf8_view(data: source.baseAddress, size: UInt64(source.count))
        return lr_probe_create(&inspectionOptions, &inspection, &error)
    }
}
require(inspectionCreated == LR_OK, "Swift probe creation")
defer { lr_probe_release(inspection) }
require(lr_probe_run(inspection, &error) == LR_OK, "Swift probe execution")
var inspectionState = lr_probe_snapshot()
require(lr_probe_snapshot_init(&inspectionState, 64) == LR_OK, "Swift probe snapshot init")
require(lr_probe_get_snapshot(inspection, &inspectionState) == LR_OK
        && inspectionState.state == LR_PROBE_FINISHED && inspectionState.cleanup_state == LR_CLEANUP_CONFIRMED
        && inspectionState.video_index == 1 && inspectionState.audio_count == 2 && inspectionState.duration == 2,
        "Swift probe terminal state")
var inspectionData = [UInt8](repeating: 0, count: Int(inspectionState.json_size))
var inspectionSize: UInt64 = 0
require(inspectionData.withUnsafeMutableBufferPointer { bytes in
    lr_probe_copy_json(inspection, bytes.baseAddress, UInt64(bytes.count), &inspectionSize)
} == LR_OK && inspectionSize == inspectionState.json_size, "Swift probe result copy")
let inspectedSource = try JSONSerialization.jsonObject(with: Data(inspectionData)) as? [String: Any]
require(inspectedSource?["kind"] as? String == "hlg", "Swift probe classification differs")
print("Swift native source inspection and immutable JSON snapshot checks passed.")

require(MemoryLayout<lr_plan_options>.size == 160, "Swift plan layout differs")
func withViews<Result>(_ values: [[UInt8]], _ body: ([lr_utf8_view]) -> Result) -> Result {
    func next(_ index: Int, _ views: [lr_utf8_view]) -> Result {
        if index == values.count { return body(views) }
        return values[index].withUnsafeBufferPointer { bytes in
            next(index + 1, views + [lr_utf8_view(data: bytes.baseAddress, size: UInt64(bytes.count))])
        }
    }
    return next(0, [])
}
let planningMedia = Array(try JSONSerialization.data(withJSONObject: [
    "schema_version": 1, "path": String(decoding: sourcePath, as: UTF8.self), "kind": "sdr",
    "video": ["index": 0, "width": 128, "height": 72, "codec_name": "h264", "pix_fmt": "yuv420p",
              "time_base": "1/100", "field_order": "tt"]
] as [String: Any]))
let planningChoices = Array(#"{"purpose":"archive","codec":"avc"}"#.utf8)
let planningPresets = Array(#"{"archive":{"avc":{"encoder":"libx264","preset":"medium","quality":21}},"audio":{"archive_default":"copy"}}"#.utf8)
let planningDestination = Array(outputRoot.appendingPathComponent("planned.mkv").path.utf8)
let planningAssets = Array(outputRoot.path.utf8)
var planningOptions = lr_plan_options()
require(lr_plan_options_init(&planningOptions, 160) == LR_OK, "Swift plan init")
var nativePlan: OpaquePointer?
let planCreated = withViews([planningMedia, planningChoices, planningPresets, executable,
                              planningDestination, planningAssets]) { values in
    planningOptions.media_json = values[0]; planningOptions.encode_json = values[1]
    planningOptions.presets_json = values[2]; planningOptions.ffmpeg = values[3]
    planningOptions.destination = values[4]; planningOptions.assets = values[5]
    return lr_plan_create(&planningOptions, &nativePlan, &error)
}
require(planCreated == LR_OK, "Swift pure plan creation")
defer { lr_plan_release(nativePlan) }
var planningSize: UInt64 = 0
require(lr_plan_copy_json(nativePlan, nil, 0, &planningSize) == LR_BUFFER_TOO_SMALL, "Swift plan size query")
var planningData = [UInt8](repeating: 0, count: Int(planningSize))
require(planningData.withUnsafeMutableBufferPointer { bytes in
    lr_plan_copy_json(nativePlan, bytes.baseAddress, UInt64(bytes.count), &planningSize)
} == LR_OK, "Swift immutable plan copy")
let planned = try JSONSerialization.jsonObject(with: Data(planningData)) as? [String: Any]
require((planned?["arguments"] as? [String])?.contains("libx264") == true &&
        (planned?["options"] as? [String: Any])?["bit_depth"] as? String == "8", "Swift plan choices differ")
var sourceDescription = lr_source_format()
require(lr_source_format_init(&sourceDescription, UInt64(MemoryLayout<lr_source_format>.size)) == LR_OK, "Swift source format init")
require(planningMedia.withUnsafeBufferPointer { bytes in
    lr_media_source_format(lr_utf8_view(data: bytes.baseAddress, size: UInt64(bytes.count)), LR_RANGE_AUTO,
                            &sourceDescription, &error)
} == LR_OK && sourceDescription.bit_depth == 8 && sourceDescription.sampling == LR_SAMPLING_420,
        "Swift source format derivation differs")
print("Swift native timeline, planning and source-format contracts passed.")
var derivedOptions = lr_timeline_options()
require(lr_timeline_options_init(&derivedOptions, 96) == LR_OK, "Swift derived timeline init")
var derivedTimeline: OpaquePointer?
let derivedCreated = withViews([executable, planningMedia]) { values in
    derivedOptions.ffprobe = values[0]
    return lr_timeline_create_from_probe(&derivedOptions, values[1], &derivedTimeline, &error)
}
require(derivedCreated == LR_OK && lr_timeline_run(derivedTimeline, &error) == LR_OK, "Swift probe-derived timeline scan")
var derivedState = lr_timeline_snapshot()
require(lr_timeline_snapshot_init(&derivedState, 64) == LR_OK, "Swift derived timeline snapshot init")
require(lr_timeline_get_snapshot(derivedTimeline, &derivedState) == LR_OK && derivedState.count == 3 &&
        derivedState.flags == 0, "Swift interlaced source used packet indexing")
lr_timeline_release(derivedTimeline)
