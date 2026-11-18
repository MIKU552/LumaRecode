import CoreImage
import Foundation
import ImageIO
import UniformTypeIdentifiers

enum PhotoFormat: String, CaseIterable, Identifiable, Sendable {
    case sdr, hdr, adaptive

    var id: Self { self }
    var type: UTType { self == .sdr ? .jpeg : .heic }
    var fileExtension: String { self == .sdr ? "jpg" : "heic" }
    var title: LocalizedStringResource {
        switch self {
        case .sdr: "SDR JPEG"
        case .hdr: "HDR HEIC"
        case .adaptive: "Adaptive HDR HEIC"
        }
    }

    func matchesExtension(_ value: String) -> Bool {
        value.lowercased() == fileExtension || self == .sdr && value.lowercased() == "jpeg"
    }

    func write(to destination: URL, frame: URL, transfer: String, liveIdentifier: String?, control: JobControl) throws {
        let sourceName = transfer == "arib-std-b67" ? CGColorSpace.itur_2100_HLG
            : transfer == "smpte2084" ? CGColorSpace.itur_2100_PQ : CGColorSpace.sRGB
        guard let source = CGColorSpace(name: sourceName), let srgb = CGColorSpace(name: CGColorSpace.sRGB),
              let pq = CGColorSpace(name: CGColorSpace.itur_2100_PQ) else {
            throw EngineError(message: "Cannot initialize the output color spaces.")
        }
        func load(sdr: Bool) throws -> CIImage {
            guard let image = CIImage(contentsOf: frame, options: [.colorSpace: source, .toneMapHDRtoSDR: sdr]) else {
                throw EngineError(message: "Cannot read the extracted frame.")
            }
            guard let liveIdentifier else { return image }
            var properties = image.properties
            properties[kCGImagePropertyMakerAppleDictionary as String] = ["17": liveIdentifier]
            return image.settingProperties(properties)
        }
        let context = CIContext(options: [.cacheIntermediates: false])
        try control.check()
        switch self {
        case .sdr:
            try context.writeJPEGRepresentation(of: load(sdr: true), to: destination, colorSpace: srgb)
        case .hdr:
            try context.writeHEIF10Representation(of: load(sdr: false), to: destination, colorSpace: pq)
        case .adaptive:
            let base = try load(sdr: true)
            let alternate = try load(sdr: false)
            try context.writeHEIFRepresentation(of: base, to: destination, format: .RGBA8, colorSpace: srgb,
                                                options: [.hdrImage: alternate])
        }
        try control.check()
        guard let result = CGImageSourceCreateWithURL(destination as CFURL, nil), CGImageSourceGetCount(result) == 1,
              let encodedType = CGImageSourceGetType(result), UTType(encodedType as String)?.conforms(to: type) == true else {
            throw EngineError(message: "The exported photo could not be verified.")
        }
        if self == .adaptive,
           CGImageSourceCopyAuxiliaryDataInfoAtIndex(result, 0, kCGImageAuxiliaryDataTypeISOGainMap) == nil {
            throw EngineError(message: "The exported photo is missing its HDR gain map.")
        }
        if let liveIdentifier {
            let properties = CGImageSourceCopyPropertiesAtIndex(result, 0, nil) as? [String: Any]
            let makerNote = properties?[kCGImagePropertyMakerAppleDictionary as String] as? [String: Any]
            guard makerNote?["17"] as? String == liveIdentifier else {
                throw EngineError(message: "The exported still is missing its Live Photo identifier.")
            }
        }
        if self == .hdr {
            let properties = CGImageSourceCopyPropertiesAtIndex(result, 0, nil) as? [String: Any]
            guard (properties?[kCGImagePropertyDepth as String] as? Int ?? 0) >= 10,
                  let decoded = CIImage(contentsOf: destination, options: [.expandToHDR: true]),
                  decoded.colorSpace?.isHDR() == true else {
                throw EngineError(message: "The exported photo is missing its HDR color space or bit depth.")
            }
        }
    }
}
