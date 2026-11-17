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
