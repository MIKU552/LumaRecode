import Darwin
import Foundation

enum CommandLineHost {
    private static let usage = """
    LumaRecode for macOS
      probe INPUT [--ffprobe PATH]
      frames INPUT [--ffprobe PATH]
      convert INPUT -o OUTPUT [--codec hevc|av1|avc] [--range auto|sdr|hdr]
        [--input-kind auto|sdr|hlg|pq|log] [--profile pocket3|samsung|milog|applelog|applelog2]
        [--standard-hdr] [--start-frame N] [--end-frame N] [--overwrite]
        [--ffmpeg PATH] [--ffprobe PATH]
    Frame ranges are one-based with an inclusive end. Use .mkv or .mp4 for OUTPUT.
    """

    static func run(_ arguments: [String]) -> Int32 {
        if arguments == ["--help"] || arguments == ["-h"] {
            print(usage)
            return 0
        }
        if arguments == ["--version"] {
            print(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Development")
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
                                                requireEncoder: parsed.command == "convert")
            let engine = try NativeEngine(tools: tools, assets: NativeEngine.bundledAssets)
            let source = try engine.inspect(parsed.input, control: control)
            switch parsed.command {
            case "probe":
                FileHandle.standardOutput.write(source.snapshot)
                print("")
            case "frames":
                let index = try engine.loadFrames(source, control: control)
                print("{\"frame_count\":\(index.count)}")
            default:
                guard let output = parsed.output else { throw EngineError(message: "Missing output path.") }
                let overwrite = try confirmOverwrite(output, allowed: parsed.flags.contains("--overwrite"))
                try engine.convert(source, to: output, options: parsed.encoding, overwrite: overwrite,
                                   selection: parsed.selection, control: control)
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

    private static func confirmOverwrite(_ url: URL, allowed: Bool) throws -> Bool {
        guard FileManager.default.fileExists(atPath: url.path) else { return false }
        if allowed { return true }
        guard isatty(STDIN_FILENO) != 0 else {
            throw EngineError(message: "Output exists. Use --overwrite to replace it.")
        }
        diagnostic("Replace \(url.lastPathComponent)? [y/N]")
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
        let input: URL
        var values: [String: String] = [:]
        var flags: Set<String> = []
        var output: URL?
        var selection: FrameSelection?
        var encoding = EncodingOptions()

        init(_ words: [String]) throws {
            guard words.count >= 2, ["probe", "frames", "convert"].contains(words[0]), !words[1].hasPrefix("-") else {
                throw EngineError(message: "Specify a command and input path.")
            }
            command = words[0]
            input = URL(fileURLWithPath: words[1]).standardizedFileURL
            let common: Set<String> = ["--ffmpeg", "--ffprobe"]
            let conversion: Set<String> = ["-o", "--codec", "--range", "--input-kind", "--profile", "--start-frame", "--end-frame"]
            var remaining = words.dropFirst(2).makeIterator()
            while let word = remaining.next() {
                if command == "convert", ["--standard-hdr", "--overwrite"].contains(word), flags.insert(word).inserted {
                    continue
                }
                guard common.contains(word) || (command == "convert" && conversion.contains(word)),
                      values[word] == nil, let value = remaining.next(), !value.hasPrefix("--"), !value.isEmpty else {
                    throw EngineError(message: "Invalid option: \(word)")
                }
                values[word] = value
            }
            if command != "convert" { return }
            guard let path = values["-o"] else { throw EngineError(message: "Conversion requires -o OUTPUT.") }
            output = URL(fileURLWithPath: path).standardizedFileURL
            encoding.codec = try choice("--codec", allowed: ["hevc", "av1", "avc"], defaultValue: "hevc")
            encoding.output = try choice("--range", allowed: ["auto", "sdr", "hdr"], defaultValue: "auto")
            encoding.sourceKind = try choice("--input-kind", allowed: ["auto", "sdr", "hlg", "pq", "log"], defaultValue: "auto")
            if values["--profile"] != nil {
                encoding.profile = try choice("--profile", allowed: ["pocket3", "samsung", "milog", "applelog", "applelog2"], defaultValue: "")
            }
            encoding.standardHDR = flags.contains("--standard-hdr")
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
