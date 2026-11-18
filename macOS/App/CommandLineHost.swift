import Darwin
import Foundation

enum CommandLineHost {
    private static let usage = """
    LumaRecode for macOS
      probe INPUT [--ffprobe PATH]
      frames INPUT [--ffprobe PATH]
      doctor [--ffmpeg PATH] [--ffprobe PATH]
      image INPUT -o OUTPUT --frame N [--format sdr|hdr|adaptive] [--overwrite]
        [--input-kind auto|sdr|hlg|pq|log] [--profile pocket3|samsung|milog|applelog|applelog2]
        [--live-photo [--motion-first N --motion-last N]]
        [--ffmpeg PATH] [--ffprobe PATH]
      convert INPUT -o OUTPUT [--codec hevc|av1|avc] [--range auto|sdr|hdr|dolbyvision84]
        [--encoder software|auto|videotoolbox]
        [--input-kind auto|sdr|hlg|pq|log] [--profile pocket3|samsung|milog|applelog|applelog2]
        [--standard-hdr] [--start-frame N] [--end-frame N] [--overwrite]
        [--ffmpeg PATH] [--ffprobe PATH]
    Frame ranges are one-based with an inclusive end. Use .mkv or .mp4 for OUTPUT.
    """

    static func run(_ arguments: [String]) async -> Int32 {
        if arguments == ["--help"] || arguments == ["-h"] {
            print(usage)
            return 0
        }
        if arguments == ["--version"] {
            print(Bundle.main.object(forInfoDictionaryKey: "LumaRecodeDisplayVersion") as? String
                  ?? Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Development")
            return 0
        }
        let parsed: Options
        do {
            parsed = try Options(arguments)
        } catch {
            diagnostic(error.localizedDescription + "\n" + usage)
            return 2
        }

        let control = JobControl()
        // Ignore default signal termination so the native owner can reap
        // FFmpeg and confirm cleanup before returning to the shell.
        signal(SIGINT, SIG_IGN)
        let interrupt = DispatchSource.makeSignalSource(signal: SIGINT, queue: .global(qos: .userInitiated))
        interrupt.setEventHandler { control.cancel() }
        interrupt.resume()
        defer { interrupt.cancel() }
        do {
            let tools = try MediaTools.discover(ffmpeg: parsed.values["--ffmpeg"], ffprobe: parsed.values["--ffprobe"],
                                                requireEncoder: ["convert", "doctor", "image"].contains(parsed.command))
            let engine = try NativeEngine(tools: tools, assets: NativeEngine.bundledAssets)
            if parsed.command == "doctor" {
                let trials = try engine.videoToolboxTrials(control: control)
                FileHandle.standardOutput.write(try JSONEncoder().encode(trials))
                print("")
                return 0
            }
            guard let input = parsed.input else { throw EngineError(message: "Missing input path.") }
            let source = try engine.inspect(input, control: control)
            switch parsed.command {
            case "probe":
                FileHandle.standardOutput.write(source.snapshot)
                print("")
            case "frames":
                let index = try engine.loadFrames(source, control: control)
                print("{\"frame_count\":\(index.count)}")
            default:
                guard let output = parsed.output else { throw EngineError(message: "Missing output path.") }
                if let photo = parsed.photo, parsed.flags.contains("--live-photo") {
                    try await engine.exportLivePhoto(source, to: output, format: photo.format, frame: photo.frame,
                                                     options: parsed.encoding, range: parsed.selection, control: control)
                } else if let photo = parsed.photo {
                    let overwrite = try confirmOverwrite(output, allowed: parsed.flags.contains("--overwrite"), control: control)
                    try engine.exportPhoto(source, to: output, format: photo.format, frame: photo.frame,
                                           options: parsed.encoding, overwrite: overwrite, control: control)
                } else {
                    let overwrite = try confirmOverwrite(output, allowed: parsed.flags.contains("--overwrite"), control: control)
                    try engine.convert(source, to: output, options: parsed.encoding, overwrite: overwrite,
                                       selection: parsed.selection, control: control)
                }
                print(output.path)
            }
            return 0
        } catch is CancellationError {
            return 130
        } catch {
            diagnostic(error.localizedDescription)
            return 1
        }
    }

    private static func confirmOverwrite(_ url: URL, allowed: Bool, control: JobControl) throws -> Bool {
        guard FileManager.default.fileExists(atPath: url.path) else { return false }
        if allowed { return true }
        guard isatty(STDIN_FILENO) != 0 else {
            throw EngineError(message: "Output exists. Use --overwrite to replace it.")
        }
        diagnostic("Replace \(url.lastPathComponent)? [y/N]")
        // An interactive prompt is cancellable even while the terminal has
        // not delivered a complete input line.
        var input = pollfd(fd: STDIN_FILENO, events: Int16(POLLIN), revents: 0)
        while true {
            try control.check()
            let ready = poll(&input, 1, 100)
            if ready > 0 { break }
            if ready < 0 && errno != EINTR { throw EngineError(message: "Cannot read overwrite confirmation.") }
        }
        try control.check()
        guard let answer = readLine()?.lowercased(), ["y", "yes"].contains(answer) else {
            throw EngineError(message: "Existing output kept.")
        }
        return true
    }

    private static func diagnostic(_ message: String) {
        FileHandle.standardError.write(Data((message + "\n").utf8))
    }

    private struct Options {
        let command: String
        let input: URL?
        var values: [String: String] = [:]
        var flags: Set<String> = []
        var output: URL?
        var selection: FrameSelection?
        var photo: (format: PhotoFormat, frame: UInt64)?
        var encoding = EncodingOptions()

        init(_ words: [String]) throws {
            guard let command = words.first, ["probe", "frames", "convert", "doctor", "image"].contains(command) else {
                throw EngineError(message: "Specify a command.")
            }
            self.command = command
            if command == "doctor" {
                input = nil
            } else {
                guard words.count >= 2, !words[1].hasPrefix("-") else { throw EngineError(message: "Specify an input path.") }
                input = URL(fileURLWithPath: words[1]).standardizedFileURL
            }
            var accepted: Set<String> = ["--ffprobe"]
            var switches: Set<String> = []
            if command == "doctor" { accepted.insert("--ffmpeg") }
            if command == "convert" || command == "image" {
                accepted.formUnion(["-o", "--ffmpeg", "--input-kind", "--profile"])
                switches.insert("--overwrite")
            }
            if command == "convert" {
                accepted.formUnion(["--codec", "--encoder", "--range", "--start-frame", "--end-frame"])
                switches.insert("--standard-hdr")
            }
            if command == "image" {
                accepted.formUnion(["--frame", "--format", "--motion-first", "--motion-last"])
                switches.insert("--live-photo")
            }
            var remaining = words.dropFirst(input == nil ? 1 : 2).makeIterator()
            while let word = remaining.next() {
                if switches.contains(word), flags.insert(word).inserted {
                    continue
                }
                guard accepted.contains(word),
                      values[word] == nil, let value = remaining.next(), !value.hasPrefix("--"), !value.isEmpty else {
                    throw EngineError(message: "Invalid option: \(word)")
                }
                values[word] = value
            }
            guard command == "convert" || command == "image" else { return }
            guard let path = values["-o"] else { throw EngineError(message: "Export requires -o OUTPUT.") }
            output = URL(fileURLWithPath: path).standardizedFileURL
            encoding.sourceKind = try choice("--input-kind", allowed: ["auto", "sdr", "hlg", "pq", "log"], defaultValue: "auto")
            if values["--profile"] != nil {
                encoding.profile = try choice("--profile", allowed: ["pocket3", "samsung", "milog", "applelog", "applelog2"], defaultValue: "")
            }
            if command == "image" {
                guard let format = PhotoFormat(rawValue: values["--format"] ?? "sdr") else {
                    throw EngineError(message: "Invalid value for --format.")
                }
                guard let selected = try frame("--frame") else { throw EngineError(message: "Specify --frame N.") }
                let first = try frame("--motion-first")
                let last = try frame("--motion-last")
                let live = flags.contains("--live-photo")
                if first != nil || last != nil {
                    guard live, let first, let last else {
                        throw EngineError(message: "Specify both motion frames with --live-photo.")
                    }
                    selection = FrameSelection(first: first, last: last)
                }
                if live && flags.contains("--overwrite") {
                    throw EngineError(message: "Use a new output folder for the Live Photo pair.")
                }
                photo = (format, selected)
                return
            }
            encoding.codec = try choice("--codec", allowed: ["hevc", "av1", "avc"], defaultValue: "hevc")
            if let value = values["--encoder"] {
                guard let mode = EncoderMode(rawValue: value) else { throw EngineError(message: "Invalid value for --encoder.") }
                encoding.encoder = mode
            }
            encoding.output = try choice("--range", allowed: ["auto", "sdr", "hdr", "dolbyvision84"], defaultValue: "auto")
            encoding.standardHDR = flags.contains("--standard-hdr")
            if encoding.output == "dolbyvision84" && (values["--encoder"] == "software" || encoding.standardHDR) {
                throw EngineError(message: "Dolby Vision 8.4 requires VideoToolbox and its own output mode.")
            }
            let first = try frame("--start-frame")
            let last = try frame("--end-frame")
            if first != nil || last != nil { selection = FrameSelection(first: first ?? 1, last: last) }
        }

        private func choice(_ flag: String, allowed: Set<String>, defaultValue: String) throws -> String {
            let result = values[flag] ?? defaultValue
            guard allowed.contains(result) else { throw EngineError(message: "Invalid value for \(flag).") }
            return result
        }

        private func frame(_ flag: String) throws -> UInt64? {
            guard let value = values[flag] else { return nil }
            guard let index = UInt64(value), index > 0 else { throw EngineError(message: "\(flag) requires a positive integer.") }
            return index
        }
    }
}
