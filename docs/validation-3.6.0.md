# 3.6.0 命令行验证 / CLI validation

验证日期：2026-11-12。环境为维护者的 Windows 11 x64、现有 Intel QSV/Vulkan 驱动与 FFmpeg；不是新的编码画质参数扫描。

- **33 个公共 CLI 集成场景通过**：使用实际 `FrameFlow.com` 和打包后的 EXE，包含帮助/版本、中英文、预设/曲线目录、许可状态、工具缺失、探测、计划、配置优先级、非法参数、源文件保护、编码/日志、跳过与原子覆盖、失败保留旧输出、递归批量、部分失败继续、Unicode/空格/`&` 路径，以及 cmd 和 PowerShell 管道/退出码。
- **真实控制台取消**：在独立隐藏控制台中向任务进程组发送 CTRL+BREAK，验证退出码 130、取消事件、无最终输出及临时目录清理。
- **461 项语言/资源检查通过**：211 条英文资源与内嵌程序集资源检查。
- **原 worker 的九组集成流程通过**：包括 SDR、10 位 422、三组 Log→HLG、HLG→SDR、音轨、进度、取消清理与错误协议。
- **GUI 实测通过**：原生 WinUI 窗口启动、读取 SDR 测试片、完成 HEVC 转换并显示 100% 和输出校验完成状态。
- **Pocket 3 三种模式实拍探测/计划通过**：D-Log M 自动选 Pocket 3 LUT→HLG；HLG 保持 HDR；普通模式保持 SDR。这里只探测与预演，不改写原片。
- **发布包检查**：ZIP 仅含 `FrameFlow.exe` / `FrameFlow.com`；原生 COM 为 5120 字节，导入表仅含系统 KERNEL32.dll，无 CRT/libgcc DLL。
- **原始数据未改变**：编码预设与三张 LUT 的字节相对上一版一致；第三方原始许可保留，自主项目仍为 MIT。

These are functional integration results on the maintainer's machine. The public console entry, JSON/exit contracts, native shell behavior, cancellation, atomic replacement and existing GUI/worker flows were exercised. They do not calibrate camera color science or repeat historical quality benchmarks, and do not establish compatibility with every GPU/driver/FFmpeg build.

复现 / Reproduce:

```powershell
./build.ps1
python qa/CliChecks/check_cli.py --exe dist/SelfContained/FrameFlow.com
dotnet run --project qa/LanguageChecks/LanguageChecks.csproj -c Release -- App/bin/x64/Release/net11.0-windows10.0.26100.0/win-x64/FrameFlow.dll
```

worker 集成检查沿用 `qa/RuntimeChecks/check_worker.py --deployment <extracted-app-folder>`，其中解压目录内包含 `Payload/Runtime` 和 `Payload/Worker`。检查 CTRL+BREAK 的测试辅助进程使用真实但隐藏的控制台，不操作用户的终端窗口。

构建保留已有 NETSDK1244 提示：单文件的磁盘提取模式用于 WinUI 资源及 Python 子进程，详见[依赖说明](dependencies.md)。
