@preconcurrency import AVFoundation
import Foundation
import Photos

enum LivePhoto {
    static let stillImageTime = AVMetadataIdentifier(rawValue: "mdta/com.apple.quicktime.still-image-time")

    // A writer and reader can move compressed samples directly between
    // containers. Separate track pumps keep the writer's interleaving bounded.
    private final class TrackPump: @unchecked Sendable {
        let source: AVAssetReaderOutput.Provider<CMReadySampleBuffer<CMSampleBuffer.DynamicContent>>
        let destination: AVAssetWriterInput.SampleBufferReceiver

        init(reader: AVAssetReader, writer: AVAssetWriter, output: AVAssetReaderTrackOutput, input: AVAssetWriterInput) {
            source = reader.outputProvider(for: output)
            destination = writer.inputReceiver(for: input)
        }

        func copy() async throws {
            defer { destination.finish() }
            while let sample = try await source.next() {
                try Task.checkCancellation()
                try await destination.append(sample)
            }
        }
    }

    static func writeMovie(
        from clip: URL, to movie: URL, identifier: String, cover: CMTimeRange, control: JobControl
    ) async throws {
        let asset = AVURLAsset(url: clip)
        guard let video = try await asset.loadTracks(withMediaType: .video).first else {
            throw EngineError(message: "The Live Photo clip has no video track.")
        }
        let audio = try await asset.loadTracks(withMediaType: .audio).prefix(1)
        let reader = try AVAssetReader(asset: asset)
        let writer = try AVAssetWriter(outputURL: movie, fileType: .mov)
        var tracks: [TrackPump] = []
        for track in [video] + audio {
            let settings = try await track.load(.formatDescriptions)
            let output = AVAssetReaderTrackOutput(track: track, outputSettings: nil)
            let input = AVAssetWriterInput(mediaType: track.mediaType, outputSettings: nil, sourceFormatHint: settings.first)
            guard reader.canAdd(output), writer.canAdd(input) else {
                throw EngineError(message: "The Live Photo movie cannot contain this track.")
            }
            tracks.append(TrackPump(reader: reader, writer: writer, output: output, input: input))
        }
        let assetID = AVMutableMetadataItem()
        assetID.identifier = .quickTimeMetadataContentIdentifier
        assetID.dataType = kCMMetadataBaseDataType_UTF8 as String
        assetID.value = identifier as NSString
        writer.metadata = [assetID]

        let dataType = kCMMetadataBaseDataType_SInt8 as String
        let specifications = [[kCMMetadataFormatDescriptionMetadataSpecificationKey_Identifier as String: stillImageTime.rawValue,
                               kCMMetadataFormatDescriptionMetadataSpecificationKey_DataType as String: dataType]]
        var description: CMMetadataFormatDescription?
        let status = CMMetadataFormatDescriptionCreateWithMetadataSpecifications(
            allocator: nil, metadataType: kCMMetadataFormatType_Boxed,
            metadataSpecifications: specifications as CFArray, formatDescriptionOut: &description
        )
        guard status == noErr, let description else { throw EngineError(message: "Cannot describe the Live Photo marker.") }
        let input = AVAssetWriterInput(mediaType: .metadata, outputSettings: nil, sourceFormatHint: description)
        guard writer.canAdd(input) else { throw EngineError(message: "Cannot add the Live Photo marker track.") }
        let marker = writer.inputMetadataReceiver(for: input)
        let item = AVMutableMetadataItem()
        item.identifier = stillImageTime
        item.dataType = dataType
        item.value = NSNumber(value: Int8(-1))
        let sample = AVTimedMetadataGroup(items: [item], timeRange: cover)
        try await control.runAsync({ reader.cancelReading(); writer.cancelWriting() }) {
            try reader.start()
            try writer.start()
            writer.startSession(atSourceTime: .zero)
            do {
                try await withThrowingTaskGroup(of: Void.self) { group in
                    for track in tracks { group.addTask { try await track.copy() } }
                    try await marker.append(sample)
                    marker.finish()
                    try await group.waitForAll()
                }
                await withCheckedContinuation { done in writer.finishWriting { done.resume() } }
                try control.check()
                guard writer.status == .completed else {
                    throw writer.error ?? EngineError(message: "The Live Photo movie could not be finalized.")
                }
            } catch {
                reader.cancelReading()
                writer.cancelWriting()
                try control.check()
                throw error
            }
        }
    }

    static func verify(still: URL, movie: URL, control: JobControl) async throws {
        // Validate the two generated resources with the same framework that
        // loads Live Photos for playback. This operation needs no library access.
        let request = PhotoRequest()
        try await control.runAsync({ request.cancel() }) {
            try await withCheckedThrowingContinuation { (completion: CheckedContinuation<Void, Error>) in
                let id = PHLivePhoto.request(withResourceFileURLs: [still, movie], placeholderImage: nil, targetSize: .zero,
                                             contentMode: .aspectFit) { photo, info in
                    if info[PHLivePhotoInfoIsDegradedKey] as? Bool == true { return }
                    if let error = info[PHLivePhotoInfoErrorKey] as? Error { completion.resume(throwing: error) }
                    else if photo != nil { completion.resume() }
                    else { completion.resume(throwing: EngineError(message: "Photos could not open the Live Photo pair.")) }
                }
                request.set(id)
            }
        }
    }

    private final class PhotoRequest: @unchecked Sendable {
        private let lock = NSLock()
        private var id: PHLivePhotoRequestID?
        private var cancelled = false

        func set(_ id: PHLivePhotoRequestID) {
            let stop = lock.withLock { self.id = id; return cancelled }
            if stop { PHLivePhoto.cancelRequest(withRequestID: id) }
        }

        func cancel() {
            let active = lock.withLock { cancelled = true; return id }
            if let active { PHLivePhoto.cancelRequest(withRequestID: active) }
        }
    }
}
