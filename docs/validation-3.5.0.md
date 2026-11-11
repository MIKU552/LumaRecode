# 3.5.0 升级验证 / Upgrade verification

验证日期：2026-11-12；维护者的 Windows 11 x64 电脑，HDR 开启，使用本机已有 Intel QSV / Vulkan 驱动与外部 FFmpeg。此记录是运行时升级的功能回归检查，不是新的编码画质基准或相机色彩标定。

| 检查 / Check | 结果 / Result |
| --- | --- |
| .NET SDK 11.0.100 / runtime 11.0.0 + WinUI 2.6.5 | 自包含 EXE 与 framework-dependent ZIP 均完成 Release 构建 |
| 语言 / 资源 / 程序内嵌英文资源 | 459 项通过，210 条翻译，XAML 覆盖完整 |
| 默认 EXE 的 Python 文件 | 从实际 EXE 解压目录核对，39/39 文件与官方 3.15.2 ZIP 哈希一致 |
| 许可输出 | 实际解压的 `licenses/manifest.json` 与源码构建清单逐字节一致 |
| 真实素材导入 | Pocket 3 4K MP4 可读取，识别 DJI 专用普通模式元数据 |
| 原生界面 | HDR Mica、独立配色弹出层、最大化居中布局、向下语言菜单、中英文切换检查通过 |
| 界面发起转换 | 3 秒 SDR 测试片 → HEVC，100% 完成，帧率/时间/输出检查状态正常 |
| 独立 worker 协议 | 实际打包 Python 的隔离模式，Unicode 路径、视频位深与音轨探测通过 |
| SDR 快速分享 | HEVC、8 位 4:2:0、Rec.709，音轨与进度事件通过 |
| 软件归档 | HEVC、10 位 4:2:2、Rec.709、音轨保留通过 |
| 三组 Log → HLG | Mi-Log / Pocket 3 / Samsung LUT 均完成真实编码，核对 10 位 4:2:0、BT.2020 / HLG / BT.2020nc 及音轨 |
| HLG → SDR | Vulkan/libplacebo 链路通过，核对 Rec.709 8 位输出 |
| 取消 / 缺失文件 | 取消事件、临时目录清理、无最终输出；缺失输入返回结构化错误 |
| 原预设与 LUT | 编码预设和三张 LUT 相对旧提交无改动；项目生成的两张表重新生成后哈希一致 |

`qa/RuntimeChecks/check_worker.py` 使用合成的高位深输入验证转换链路和输出标签。它不证明相机曲线的物理准确度、传感器动态范围、HDR 屏幕亮度，或所有机器/编码器组合兼容性。历史性能数据仍以原[编码测试报告](benchmarks/encoding-benchmark.zh-CN.md)为准。

These are integration checks on the maintainer's Windows 11 system, not new encoder benchmarks or camera calibration. The worker tests use synthetic high-bit-depth footage to validate execution, audio, output tags, progress, and cancellation. Device-specific HDR appearance and other GPU/driver combinations may differ.

复现 / Reproduce:

```powershell
./build.ps1
dotnet run --project qa/LanguageChecks/LanguageChecks.csproj -c Release -- App/bin/x64/Release/net11.0-windows10.0.26100.0/win-x64/FrameFlow.dll
```

启动 EXE 后，可把它的 .NET 临时解压目录作为 `--deployment` 传入 worker 集成检查（该目录内有 `Payload/Worker` 和 `Payload/Runtime`）：

```powershell
python qa/RuntimeChecks/check_worker.py --deployment <extracted-app-folder>
python Worker/tools/generate_hlg_luts.py --check
```

构建保留一条 NETSDK1244 提示：WinUI 资源和 Python 子进程需要磁盘文件，因此有意使用单文件完整提取模式，原因见[依赖说明](dependencies.md)。没有通过隐藏该提示来宣称零警告。
