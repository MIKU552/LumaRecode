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
