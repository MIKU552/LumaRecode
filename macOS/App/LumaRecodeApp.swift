import AppKit
import SwiftUI
import UniformTypeIdentifiers

private func initialVideo() -> URL? {
    let arguments = CommandLine.arguments
    guard arguments.count == 3, arguments[1] == "--open" else { return nil }
    return URL(fileURLWithPath: arguments[2]).standardizedFileURL
}

@main
enum LumaRecodeMain {
    @MainActor static func main() async {
        let arguments = Array(CommandLine.arguments.dropFirst())
        if initialVideo() == nil, let command = arguments.first, !command.hasPrefix("-psn_"),
           !command.hasPrefix("-NS"), !command.hasPrefix("-Apple") {
            exit(await CommandLineHost.run(arguments))
        }
        LumaRecodeApp.main()
    }
}

@MainActor
final class ApplicationDelegate: NSObject, NSApplicationDelegate {
    weak var workspace: Workspace?

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let task = workspace?.prepareToTerminate() else { return .terminateNow }
        Task {
            await task.value
            sender.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }
}

struct LumaRecodeApp: App {
    @NSApplicationDelegateAdaptor(ApplicationDelegate.self) private var delegate
    @State private var workspace = Workspace()
    @State private var launchVideo = initialVideo()

    var body: some Scene {
        Window("LumaRecode", id: "workspace") {
            WorkspaceView(model: workspace)
                .frame(minWidth: 720, minHeight: 490)
                .task {
                    delegate.workspace = workspace
                    if let launchVideo {
                        self.launchVideo = nil
                        workspace.open(launchVideo)
                    }
                }
                .onOpenURL { workspace.open($0) }
        }
        .defaultSize(width: 1060, height: 690)
        .commands {
            CommandGroup(after: .appInfo) {
                Button("Licenses") {
                    if let resources = Bundle.main.resourceURL {
                        NSWorkspace.shared.open(resources.appendingPathComponent("Licenses", isDirectory: true))
                    }
                }
            }
        }

        Settings { ToolSettings() }
    }
}

private struct ToolSettings: View {
    @AppStorage("ffmpeg") private var ffmpeg = ""
    @AppStorage("ffprobe") private var ffprobe = ""

    var body: some View {
        Form {
            row("FFmpeg", path: $ffmpeg)
            row("ffprobe", path: $ffprobe)
            Text("Leave the paths blank to use the tools found in PATH or Homebrew.")
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .padding(20)
        .frame(width: 520)
    }

    private func row(_ name: String, path: Binding<String>) -> some View {
        LabeledContent(name) {
            HStack {
                TextField(name, text: path)
                    .labelsHidden()
                Button("Browse…") {
                    let panel = NSOpenPanel()
                    panel.canChooseDirectories = false
                    if panel.runModal() == .OK, let url = panel.url { path.wrappedValue = url.path }
                }
            }
        }
    }
}
