import AppKit
import AVKit
import CLumaRecode
import Observation
import SwiftUI
import UniformTypeIdentifiers

@MainActor @Observable
final class Workspace {
    enum Activity { case inspect, index, checkEncoders, encode, photo, photoLibrary }
    private typealias EncoderCache = (signature: MediaTools.Signature, trials: [VideoToolboxTrial])

    var media: MediaSource?
    var player: AVPlayer?
    var fallbackPreview: NSImage?
    var frames: FrameIndex?
    var options = EncodingOptions()
    var container = "mkv"
    var limitFrames = false
    var firstFrame = "1"
    var lastFrame = ""
    var showPhotoControls = false
    var photoFrame = "1"
    var photoFormat = PhotoFormat.sdr
    var includeLivePhoto = false
    var motionFirst = ""
    var motionLast = ""
    var activity: Activity?
    var progress = 0.0
    var output: URL? {
        didSet { livePhotoFormat = nil }
    }
    var livePhotoFormat: PhotoFormat?
    var failure: String?
    private var encoderCache: EncoderCache?
    private var control: JobControl?
    private var terminating = false
    private var previewLoad: Task<Void, Never>?
    private var previewSeek: Task<Void, Never>?
    private var pendingSeek: CMTime?
    private var pendingStill: lr_frame_window?
    private var stillRequested = false
    private var stillControl: JobControl?
    private var task: Task<Void, Never>?

    var busy: Bool { activity != nil }
    var isLog: Bool { options.sourceKind == "log" || (options.sourceKind == "auto" && media?.summary.kind == "log") }
    var isDynamicHDR: Bool { media?.summary.hdr10plus == true || media?.summary.hasDolbyVision == true }
    var supportsHDRPhoto: Bool { media.map { options.supportsHDRPhoto($0.summary) } ?? false }
    var supportsHDRVideo: Bool { media.map { options.supportsHDRVideo($0.summary) } ?? false }
    var canExportVideo: Bool {
        guard let media, media.summary.kind != "raw" else { return false }
        return !media.summary.hdr10plus || options.output == "sdr" || options.standardHDR
    }
    var isDolbyVisionExport: Bool { options.output == "dolbyvision84" }
    var canOfferDolbyVision: Bool {
        guard let media, options.canGenerateDolbyVision(media.summary) else { return false }
        return encoderCache?.trials.contains { $0.available && $0.format.signal == "dolbyvision84" } ?? false
    }
    var hardwareCodecs: [String] {
        guard let media, let signal = options.videoToolboxSignal(media.summary) else { return [] }
        return (encoderCache?.trials ?? []).filter { $0.available && $0.format.signal == signal }.map(\.format.codec)
    }

    func chooseSource() {
        guard !busy else { return }
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.movie, .video] + ["mkv", "webm", "mxf", "avi", "mts", "m2ts"]
            .compactMap { UTType(filenameExtension: $0) }
        present(panel) { [weak self] url in self?.open(url) }
    }

    func open(_ url: URL) {
        guard !busy, url.isFileURL else { return }
        player?.pause()
        previewLoad?.cancel()
        cancelPreviewSeek()
        player = nil
        fallbackPreview = nil
        media = nil
        frames = nil
        output = nil
        limitFrames = false
        firstFrame = "1"
        lastFrame = ""
        showPhotoControls = false
        photoFrame = "1"
        photoFormat = .sdr
        includeLivePhoto = false
        motionFirst = ""
        motionLast = ""
        options = EncodingOptions()
        container = "mkv"
        run(.inspect) { control in
            try Self.engine(requireEncoder: false).inspect(url, control: control)
        } finished: { media in
            self.show(media)
            self.checkEncoders(force: false, selectDefault: true)
        }
    }

    private func show(_ media: MediaSource) {
        self.media = media
        let asset = AVURLAsset(url: media.url)
        previewLoad = Task { @MainActor [weak self] in
            let playable = (try? await asset.load(.isPlayable)) == true
            guard let self, !Task.isCancelled else { return }
            if playable {
                self.player = AVPlayer(playerItem: AVPlayerItem(asset: asset))
            } else {
                self.previewStill(media, window: nil)
            }
        }
    }

    func indexFrames() {
        guard !busy, let media, frames == nil else { return }
        run(.index) { control in
            try Self.engine(requireEncoder: false).loadFrames(media, control: control)
        } finished: { frames in
            self.frames = frames
            self.lastFrame = String(frames.count)
        }
    }

    func recheckEncoders() {
        checkEncoders(force: true, selectDefault: false)
    }

    private func checkEncoders(force: Bool, selectDefault: Bool) {
        let cached = force ? nil : encoderCache
        run(.checkEncoders) { control in
            try Self.inspectEncoders(cached: cached, control: control)
        } finished: { cache in
            self.encoderCache = cache
            if selectDefault, let codec = self.hardwareCodecs.first {
                self.options.codec = codec
                self.options.encoder = .videoToolbox
                self.container = "mp4"
            } else {
                self.updateEncoderChoice()
            }
        }
    }

    func updateEncoderChoice() {
        if options.output == "hdr", !supportsHDRVideo {
            options.output = "auto"
        }
        if isDolbyVisionExport {
            if canOfferDolbyVision {
                options.codec = "hevc"
                options.encoder = .videoToolbox
                container = "mp4"
                return
            }
            options.output = "hdr"
        }
        guard options.encoder == .videoToolbox else { return }
        if hardwareCodecs.isEmpty {
            options.encoder = .software
        } else if !hardwareCodecs.contains(options.codec), let first = hardwareCodecs.first {
            options.codec = first
        }
    }

    func chooseDestination() {
        guard !busy, canExportVideo, let media else { return }
        let selection: FrameSelection?
        if limitFrames {
            guard let frames, let first = UInt64(firstFrame), let last = UInt64(lastFrame),
                  first > 0, first <= last, last <= frames.count else {
                failure = String(localized: "Enter a valid first and last frame.")
                return
            }
            selection = FrameSelection(first: first, last: last)
        } else {
            selection = nil
        }
        let panel = NSSavePanel()
        panel.canCreateDirectories = true
        panel.directoryURL = media.url.deletingLastPathComponent()
        panel.nameFieldStringValue = media.url.deletingPathExtension().lastPathComponent + "-encoded." + container
        if let type = UTType(filenameExtension: container) { panel.allowedContentTypes = [type] }
        panel.isExtensionHidden = false
        let consent = SaveConsent(fileExtension: container)
        panel.delegate = consent
        present(panel) { [weak self, consent] url in
            self?.convert(media, to: url, overwrite: consent.existing == url.standardizedFileURL, selection: selection)
        }
    }

    func savePhoto() {
        guard !busy, let media, let frames, let frame = UInt64(photoFrame), frame > 0, frame <= frames.count else {
            failure = String(localized: "Enter a valid frame number.")
            return
        }
        let format = photoFormat
        let options = self.options
        let live = includeLivePhoto && format != .hdr
        var motionRange: FrameSelection?
        if live && (!motionFirst.isEmpty || !motionLast.isEmpty) {
            guard let first = UInt64(motionFirst), let last = UInt64(motionLast),
                  first > 0, first <= frame, last >= frame, last <= frames.count else {
                failure = String(localized: "Enter a Live Photo range that contains the cover frame.")
                return
            }
            motionRange = FrameSelection(first: first, last: last)
        }
        let range = motionRange
        let panel = NSSavePanel()
        panel.canCreateDirectories = true
        panel.directoryURL = media.url.deletingLastPathComponent()
        let basename = media.url.deletingPathExtension().lastPathComponent + "-frame-\(frame)"
        panel.nameFieldStringValue = live ? basename + "-live" : basename + "." + format.fileExtension
        if !live { panel.allowedContentTypes = [format.type] }
        panel.isExtensionHidden = false
        let consent = SaveConsent(fileExtension: format.fileExtension)
        if !live { panel.delegate = consent }
        present(panel) { [weak self, consent] url in
            guard let self else { return }
            let overwrite = consent.existing == url.standardizedFileURL
            self.player?.pause()
            self.output = nil
            self.run(.photo) { control in
                let engine = try Self.engine()
                if live {
                    try await engine.exportLivePhoto(media, to: url, format: format, frame: frame, options: options,
                                                     range: range, index: frames, control: control)
                } else {
                    try engine.exportPhoto(media, to: url, format: format, frame: frame, options: options,
                                           overwrite: overwrite, index: frames, control: control)
                }
            } finished: { _ in
                self.output = url
                self.livePhotoFormat = live ? format : nil
            }
        }
    }

    func addLivePhotoToLibrary() {
        guard let folder = output, let format = livePhotoFormat else { return }
        let stem = folder.lastPathComponent
        let still = folder.appendingPathComponent(stem + "." + format.fileExtension)
        let movie = folder.appendingPathComponent(stem + ".mov")
        run(.photoLibrary) { _ in
            try await LivePhoto.addToLibrary(still: still, movie: movie)
        } finished: { _ in self.livePhotoFormat = nil }
    }

    func cancel() { control?.cancel() }

    func prepareToTerminate() -> Task<Void, Never>? {
        terminating = true
        previewLoad?.cancel()
        cancelPreviewSeek()
        cancel()
        return task
    }

    func previewPhotoFrame() {
        guard let frames, let frame = UInt64(photoFrame), frame > 0, frame <= frames.count,
              let target = try? frames.resolve(FrameSelection(first: frame, last: frame)),
              let first = try? frames.resolve(FrameSelection(first: 1, last: 1)) else { return }
        guard let player else {
            if let media { previewStill(media, window: target) }
            return
        }
        let position = max(0, target.start_seconds - first.start_seconds)
        pendingSeek = CMTime(seconds: position, preferredTimescale: 600_000)
        guard previewSeek == nil else { return }
        player.pause()
        // AVPlayer decodes at most one seek at a time. During a drag only the
        // latest pending target survives to the next iteration.
        previewSeek = Task { @MainActor [weak self] in
            while !Task.isCancelled, let self, let target = self.pendingSeek {
                self.pendingSeek = nil
                await player.seek(to: target, toleranceBefore: .zero, toleranceAfter: .zero)
            }
            if !Task.isCancelled { self?.previewSeek = nil }
        }
    }

    private func previewStill(_ media: MediaSource, window: lr_frame_window?) {
        guard media.summary.kind != "raw" else { return }
        // Keep one decoder active and replace a queued slider position with
        // the latest one. Cancelling also waits for the native child to exit.
        pendingStill = window
        stillRequested = true
        stillControl?.cancel()
        guard previewSeek == nil else { return }
        previewSeek = Task { @MainActor [weak self] in
            while !Task.isCancelled, let self, self.stillRequested {
                let target = self.pendingStill
                let options = self.options
                let control = JobControl()
                self.stillRequested = false
                self.stillControl = control
                let data = await Task.detached(priority: .utility) {
                    try? Self.engine().previewStill(media, options: options, window: target, control: control)
                }.value
                if !Task.isCancelled && !self.stillRequested, let data {
                    self.fallbackPreview = NSImage(data: data)
                }
            }
            if !Task.isCancelled {
                self?.stillControl = nil
                self?.previewSeek = nil
            }
        }
    }

    private func cancelPreviewSeek() {
        previewSeek?.cancel()
        player?.currentItem?.cancelPendingSeeks()
        stillControl?.cancel()
        stillControl = nil
        pendingStill = nil
        stillRequested = false
        previewSeek = nil
        pendingSeek = nil
    }

    private func convert(_ media: MediaSource, to url: URL, overwrite: Bool, selection: FrameSelection?) {
        let options = self.options
        let frames = self.frames
        let cached = self.encoderCache
        player?.pause()
        output = nil
        progress = 0
        run(.encode) { [weak self] control in
            let engine = try Self.engine()
            let trials = cached?.signature == engine.tools.signature ? cached?.trials : nil
            try engine.convert(media, to: url, options: options, overwrite: overwrite, index: frames,
                               selection: selection, toolbox: trials, control: control) { fraction in
                Task { @MainActor [weak self] in
                    guard let self, self.control === control else { return }
                    self.progress = fraction
                }
            }
        } finished: { _ in
            self.output = url
        }
    }

    private func run<T: Sendable>(
        _ activity: Activity,
        work: @escaping @Sendable (JobControl) async throws -> T,
        finished: @escaping (T) -> Void
    ) {
        guard !busy, !terminating else { return }
        let control = JobControl()
        self.activity = activity
        self.failure = nil
        self.control = control
        task = Task {
            let result = await Task.detached(priority: .userInitiated) {
                do { return Result<T, Error>.success(try await work(control)) }
                catch { return .failure(error) }
            }.value
            self.activity = nil
            self.control = nil
            self.task = nil
            guard !self.terminating else { return }
            switch result {
            case .success(let value): finished(value)
            case .failure(is CancellationError):
                if activity == .index { limitFrames = false }
            case .failure(let error):
                if activity == .index { limitFrames = false }
                failure = error.localizedDescription
            }
        }
    }

    private nonisolated static func engine(requireEncoder: Bool = true) throws -> NativeEngine {
        let defaults = UserDefaults.standard
        let tools = try MediaTools.discover(ffmpeg: defaults.string(forKey: "ffmpeg"), ffprobe: defaults.string(forKey: "ffprobe"),
                                            requireEncoder: requireEncoder)
        return try NativeEngine(tools: tools, assets: NativeEngine.bundledAssets)
    }

    private nonisolated static func inspectEncoders(cached: EncoderCache?, control: JobControl) throws -> EncoderCache? {
        guard let engine = try? engine(), let signature = engine.tools.signature else { return nil }
        if cached?.signature == signature { return cached }
        let trials = try engine.videoToolboxTrials(control: control)
        return (signature, trials)
    }

    private func present(_ panel: NSSavePanel, selected: @escaping (URL) -> Void) {
        let handler: (NSApplication.ModalResponse) -> Void = { response in
            if response == .OK, let url = panel.url { selected(url) }
        }
        if let window = NSApplication.shared.keyWindow {
            panel.beginSheetModal(for: window, completionHandler: handler)
        } else {
            panel.begin(completionHandler: handler)
        }
    }
}

@MainActor
private final class SaveConsent: NSObject, NSOpenSavePanelDelegate {
    let fileExtension: String
    var existing: URL?

    init(fileExtension: String) { self.fileExtension = fileExtension }

    func panel(_ sender: Any, userEnteredFilename filename: String, confirmed okFlag: Bool) -> String? {
        if okFlag, let panel = sender as? NSSavePanel, let folder = panel.directoryURL {
            // This callback runs before the system's Replace dialog. A file
            // appearing after Save was pressed cannot acquire consent here.
            let name = filename.lowercased().hasSuffix("." + fileExtension) ? filename : filename + "." + fileExtension
            let url = folder.appendingPathComponent(name).standardizedFileURL
            existing = FileManager.default.fileExists(atPath: url.path) ? url : nil
        }
        return filename
    }
}

struct WorkspaceView: View {
    @Bindable var model: Workspace

    var body: some View {
        Group {
            if let media = model.media {
                HSplitView {
                    preview(media)
                        .frame(minWidth: 360, maxWidth: .infinity, maxHeight: .infinity)
                    settings
                        .frame(minWidth: 290, idealWidth: 320, maxWidth: 380, maxHeight: .infinity)
                }
            } else if model.activity == .inspect {
                ProgressView("Reading footage…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ContentUnavailableView {
                    Label("Open a video", systemImage: "film")
                } description: {
                    Text("Drop a video here or select one to begin.")
                } actions: {
                    Button("Open Video…", action: model.chooseSource)
                        .buttonStyle(.borderedProminent)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .navigationTitle(model.media?.url.lastPathComponent ?? "LumaRecode")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button(action: model.chooseSource) { Label("Open Video…", systemImage: "folder") }
                    .keyboardShortcut("o")
                    .disabled(model.busy)
            }
        }
        .dropDestination(for: URL.self) { urls, _ in
            guard !model.busy, let url = urls.first, url.isFileURL else { return false }
            model.open(url)
            return true
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            if model.media != nil || model.busy { footer }
        }
        .alert("LumaRecode", isPresented: Binding(get: { model.failure != nil }, set: { if !$0 { model.failure = nil } })) {
            Button("OK") { model.failure = nil }
        } message: {
            Text(model.failure ?? "")
        }
    }

    private func preview(_ media: MediaSource) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Group {
                if let player = model.player {
                    VideoPlayer(player: player)
                        .accessibilityLabel("Source preview")
                } else if let image = model.fallbackPreview {
                    Image(nsImage: image).resizable().scaledToFit()
                        .accessibilityLabel("Source preview")
                } else {
                    ContentUnavailableView("Preview unavailable", systemImage: "play.rectangle",
                                           description: Text("You can still convert this file."))
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            VStack(alignment: .leading, spacing: 4) {
                Text(media.url.lastPathComponent)
                    .font(.headline)
                    .lineLimit(1)
                    .truncationMode(.middle)
                let video = media.summary.video
                let size = "\(video.width ?? 0) × \(video.height ?? 0)"
                Text([size, video.codec_name?.uppercased(), video.pix_fmt, media.summary.kind.uppercased()].compactMap { $0 }.joined(separator: " · "))
                let audio = media.summary.streams.filter { $0.codec_type == "audio" }
                Text(audio.isEmpty ? String(localized: "No audio") : audio.compactMap { $0.codec_name?.uppercased() }.joined(separator: " · "))
            }
            .foregroundStyle(.secondary)
            .textSelection(.enabled)
        }
        .padding(20)
    }

    private var settings: some View {
        Form {
            Section("Color") {
                Picker("Source color", selection: $model.options.sourceKind) {
                    Text("Automatic").tag("auto")
                    Text("SDR").tag("sdr")
                    Text("HLG").tag("hlg")
                    Text("PQ").tag("pq")
                    Text("Camera Log").tag("log")
                }
                if model.isLog {
                    Picker("Log curve", selection: $model.options.profile) {
                        Text("Automatic").tag(String?.none)
                        Text("DJI D-Log M").tag(Optional("pocket3"))
                        Text("Samsung Log").tag(Optional("samsung"))
                        Text("Xiaomi Mi-Log").tag(Optional("milog"))
                        Text("Apple Log").tag(Optional("applelog"))
                        Text("Apple Log 2").tag(Optional("applelog2"))
                    }
                }
                Picker("Output range", selection: $model.options.output) {
                    Text("Automatic").tag("auto")
                    Text("SDR").tag("sdr")
                    if model.supportsHDRVideo { Text("HDR").tag("hdr") }
                    if model.canOfferDolbyVision { Text("Dolby Vision 8.4").tag("dolbyvision84") }
                }
                if model.isDynamicHDR && model.options.output != "sdr" {
                    Toggle("Convert to standard HDR", isOn: $model.options.standardHDR)
                }
            }
            Section("Encoding") {
                if !model.hardwareCodecs.isEmpty {
                    Picker("Encoding mode", selection: $model.options.encoder) {
                        Text("Quick share · VideoToolbox").tag(EncoderMode.videoToolbox)
                        Text("Software archive").tag(EncoderMode.software)
                    }
                    .disabled(model.isDolbyVisionExport)
                }
                Picker("Video codec", selection: $model.options.codec) {
                    if model.options.encoder != .videoToolbox || model.hardwareCodecs.contains("hevc") {
                        Text("HEVC").tag("hevc")
                    }
                    if model.options.encoder != .videoToolbox { Text("AV1").tag("av1") }
                    if model.options.encoder != .videoToolbox || model.hardwareCodecs.contains("avc") {
                        Text("H.264").tag("avc")
                    }
                }
                .disabled(model.isDolbyVisionExport)
                Picker("Container", selection: $model.container) {
                    Text("MKV").tag("mkv")
                    Text("MP4").tag("mp4")
                }
                .disabled(model.isDolbyVisionExport)
                Button("Check hardware encoders", action: model.recheckEncoders)
            }
            Section("Trim") {
                Toggle("Select a frame range", isOn: $model.limitFrames)
                    .onChange(of: model.limitFrames) { _, selected in if selected { model.indexFrames() } }
                if model.limitFrames, let frames = model.frames {
                    TextField("First frame", text: $model.firstFrame)
                    TextField("Last frame", text: $model.lastFrame)
                    Text("\(frames.count) frames")
                        .foregroundStyle(.secondary)
                }
            }
            Section {
                DisclosureGroup("Export a frame", isExpanded: $model.showPhotoControls) {
                    if let frames = model.frames {
                        TextField("Frame number", text: $model.photoFrame)
                            .onSubmit(model.previewPhotoFrame)
                        if model.player != nil, frames.count > 1 {
                            Slider(value: Binding(
                                get: { Double(min(frames.count, max(1, UInt64(model.photoFrame) ?? 1))) },
                                set: { model.photoFrame = String(UInt64($0)); model.previewPhotoFrame() }
                            ), in: 1...Double(frames.count), step: 1) {
                                Text("Frame number")
                            }
                        }
                        Text("\(frames.count) frames").foregroundStyle(.secondary)
                        Picker("Photo format", selection: $model.photoFormat) {
                            Text(PhotoFormat.sdr.title).tag(PhotoFormat.sdr)
                            if model.supportsHDRPhoto {
                                Text(PhotoFormat.hdr.title).tag(PhotoFormat.hdr)
                                Text(PhotoFormat.adaptive.title).tag(PhotoFormat.adaptive)
                            }
                        }
                        if model.photoFormat != .hdr {
                            Toggle("Include Live Photo", isOn: $model.includeLivePhoto)
                            if model.includeLivePhoto {
                                TextField("First motion frame", text: $model.motionFirst, prompt: Text("Automatic"))
                                TextField("Last motion frame", text: $model.motionLast, prompt: Text("Automatic"))
                            }
                        }
                        Button("Save Photo…", action: model.savePhoto)
                    } else {
                        Button("Read frame numbers", action: model.indexFrames)
                    }
                }
                .onChange(of: model.showPhotoControls) { _, shown in if shown { model.indexFrames() } }
            }
        }
        .formStyle(.grouped)
        .disabled(model.busy)
        .onChange(of: model.options.encoder) { _, encoder in
            model.container = encoder == .videoToolbox ? "mp4" : "mkv"
            model.updateEncoderChoice()
        }
        .onChange(of: model.hardwareCodecs) { _, _ in model.updateEncoderChoice() }
        .onChange(of: model.options.output) { _, _ in model.updateEncoderChoice() }
        .onChange(of: model.supportsHDRVideo) { _, _ in model.updateEncoderChoice() }
        .onChange(of: model.canOfferDolbyVision) { _, _ in model.updateEncoderChoice() }
        .onChange(of: model.supportsHDRPhoto) { _, supported in
            if !supported { model.photoFormat = .sdr }
        }
    }

    private var footer: some View {
        VStack(spacing: 0) {
            Divider()
            HStack(spacing: 12) {
                switch model.activity {
                case .inspect: ProgressView().controlSize(.small)
                case .index: ProgressView("Indexing frames…")
                case .checkEncoders: ProgressView("Checking hardware encoders…")
                case .encode: ProgressView(value: model.progress) { Text("Encoding video…") }
                case .photo: ProgressView("Exporting photo…")
                case .photoLibrary: ProgressView("Adding to Photos…")
                case nil:
                    if let output = model.output {
                        Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([output]) }
                    }
                    if model.livePhotoFormat != nil {
                        Button("Add to Photos", action: model.addLivePhotoToLibrary)
                    }
                }
                Spacer()
                if model.activity == .photoLibrary {
                    EmptyView()
                } else if model.busy {
                    Button("Cancel", action: model.cancel)
                } else {
                    Button("Export Video…", action: model.chooseDestination)
                        .buttonStyle(.borderedProminent)
                        .disabled(!model.canExportVideo)
                }
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
            .background(.bar)
        }
    }
}
