# 3.6.3 实机验证 / Live validation

验证日期：2026-11-12。维护者恢复实机测试后，复查 3.6.2 的延迟创建控件改动，并验证 3.6.3 的界面修复。

**结论：功能回归通过；没有测得 3.6.2 相比 3.6.1 的明显启动内存下降。** 既有约 161 MiB 的已使用会话与新启动会话状态不同，不能把两者差值当作优化收益。

## 环境与范围

- Windows 11 x64，系统 build 26300，Intel Core Ultra X7 358H；使用维护者现有 Intel QSV / Vulkan 驱动。
- 实机界面测试保持 Windows HDR 开启，使用原生 Mica；系统深色主题，并检查应用浅色和跟随系统切换。
- .NET SDK 11.0.100 / runtime 11.0.0、Windows App SDK 2.6.5、内置 Python 3.15.2。
- PowerShell 7.6.9；用户提供的 FFmpeg 9.0.2 full build。
- GUI 使用构建后的单个 `FrameFlow.exe`；CLI 也通过同一个发布形式的 EXE 测试。编码质量预设和 LUT 数值没有重新标定。

## 功能结果

| 范围 | 结果 |
| --- | --- |
| 首次许可 | 中文、English、System 切换即时更新正文、按钮及主界面；同意后保存状态，退出时不接受条款。语言标签及辅助功能名称固定为“界面语言 / Language”，两种界面语言均实测。 |
| 外观面板 | 首次打开创建内容；展开自定义颜色后才出现 ColorPicker。可修改十六进制颜色；未创建 ColorPicker 时恢复默认外观无异常。 |
| 主题与强调色 | 自定义绿色切回 Windows 紫色后，主界面和弹出层开关颜色一致；浅色与跟随系统切换正常。 |
| 切换语言后重建 | 打开过 ColorPicker 后切换到 English，重新打开面板显示英文，ColorPicker 再次按需创建，保留当前颜色。 |
| HDR / Mica | 稳定展开后的弹出层未观察到先前的白色亮带，主界面保留原生 Mica；沿用既有独立弹出层、背景、阴影和动画配置。静态观察不等于逐帧时序测量。 |
| 关于与工具路径 | 英文关于窗口正确显示图标、3.6.3 版本及说明；工具路径窗口无重复许可链接，已有路径保留。 |
| GUI 转码 | 3 秒、640×360、30 fps SDR 测试片完成 HEVC / Intel QSV 转码；界面显示 100%、00:03 / 00:03、562 fps 与输出校验完成。ffprobe 确认 HEVC、yuv420p、BT.709、3.0 秒。此短片速度不是通用性能基准。 |
| CLI | **33 个集成场景通过**，覆盖实际编码、PowerShell 7 管道和退出码、中文路径与代码页、批量、取消、原子覆盖以及源文件保护。 |
| Worker | **9 组集成流程通过**，覆盖 SDR、软件 10 位 4:2:2、Mi-Log / Pocket 3 / Samsung Log→HLG、Vulkan/libplacebo HLG→SDR、音轨、进度、取消清理与错误协议。 |
| 语言与资源 | **481 项检查通过**，221 条翻译资源和 XAML 覆盖完整。 |

实机发现的缺陷是独立弹出层缓存了旧强调色：切回系统色后，主界面已刷新，面板开关仍为自定义绿色。3.6.3 对弹出层自己的主题资源作用域同步刷新；没有移除 Mica 或原生阴影来规避问题。

完整 CLI / worker / 语言回归在该修复构建上通过；其后的唯一界面代码变更为固定双语语言标签，已重新构建并对中英文首次许可窗口实测。最终发布重新构建以写入提交版本信息，不重复编码参数扫描。测试结束后关闭测试实例，并逐字节恢复维护者原始偏好设置。

## 内存对照方法

比较未打开配色面板的 3.6.1 和 3.6.2：相同设置、默认窗口、HDR 开启、系统深色主题和 Mica。每个版本两次新进程启动，交替测试；每次等待进程年龄约 30 秒，再采样三次，间隔约 1 秒。基线采样前不获取 UI Automation 界面树、不执行编码任务，不强制 GC，不清空工作集。

这是已经预热的桌面会话，**不是重启系统后的冷缓存基准**。测量采用 Windows 进程性能计数器 `WorkingSetPrivate`、`PrivateBytes` 和 `WorkingSet`，单位为 MiB（2²⁰ 字节）。私有工作集是当前驻留的私有页面；私有提交与包含共享映射的总工作集是不同口径，不能互换。

## 内存结果

下表每行列出该次运行的首个样本；各组三次私有工作集和私有提交读数稳定，总工作集最多变化 0.008 MiB。

| 版本 / 运行 | 私有工作集 MiB | 私有提交 MiB | 总工作集 MiB |
| --- | ---: | ---: | ---: |
| 3.6.1 / 1 | 122.047 | 151.410 | 257.465 |
| 3.6.1 / 2 | 121.867 | 150.289 | 257.809 |
| 3.6.2 / 1 | 126.902 | 156.000 | 264.637 |
| 3.6.2 / 2 | 122.602 | 151.105 | 257.047 |

两次运行的私有工作集均值分别为 **121.957 / 124.752 MiB**。这组小样本没有显示新版明显节省内存，也不足以把约 2.8 MiB 的均值差异归因到某项代码改动。最初用于预热工具的样本没有统一进程年龄，未混入表中。

同一个 3.6.2 测试进程随后打开配色面板，私有工作集约 **137.6 MiB**；展开 ColorPicker 后约 **147.5 MiB**。此时已进行 UI Automation、截图及交互，差值同时包含这些操作和渲染分配，不能全部归因于 ColorPicker。延迟创建让未使用的功能保持未创建状态，但本次测试没有证明它带来显著的启动内存节省。

空闲时未发现随应用常驻的 Python / FFmpeg / ffprobe 子进程。此前约 161 MiB 的已使用会话本身不足以认定泄漏；本次短时间功能测试也不足以排除长期泄漏。没有采用定时强制 GC、工作集裁剪或禁用 Mica 来降低显示数字。

## 复现与限制

自动检查入口沿用 [CLI 验证文档](validation-3.6.0.md)；源码中的 `qa/CliChecks/check_cli.py`、`qa/RuntimeChecks/check_worker.py` 和 `qa/LanguageChecks` 分别执行实际 CLI、worker 和资源检查。构建保留既有 NETSDK1244 单文件磁盘提取提示，原因见[依赖说明](dependencies.md)。

复测内存时应使用同一窗口状态和设置，确保没有残留编码任务；先统一进程年龄，再记录三种口径。打开面板后的交互样本应与启动基线分开。更多进程重复、长时间循环及 ETW 分配追踪仍可用于进一步诊断，本报告没有把它们标为已完成。

These are functional results on the maintainer's Windows 11 machine, with HDR and native Mica enabled. The bilingual first-run language entry, appearance controls, theme/accent refresh, About, Tool paths and a real GUI encode passed. So did 33 CLI scenarios, 9 worker groups and 481 language/resource checks. Original preferences were restored. The 3.6.1 / 3.6.2 memory comparison **does not demonstrate a material startup memory reduction**; interaction samples include automation and rendering overhead. These short tests neither establish a memory leak nor rule out a long-term one, and do not validate every GPU, driver or FFmpeg build.
