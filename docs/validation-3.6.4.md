# 3.6.4 内存测量与构建验证 / Memory and build validation

日期：2026-11-12。本版将 WinUI 前端改为 Native AOT，保留单文件 EXE 下载、原生 Mica、HDR 独立弹出层、阴影与动画。FFmpeg 仍由用户提供。

**结论：同一台电脑上的两次独立启动，原版私有工作集为 121.8–122.9 MiB，单文件 AOT 版为 116.0–117.3 MiB，约节省 5–6 MiB。** 这组小样本支持本机启动占用有所下降，不代表所有机器都会下降相同幅度。维护者另行手动观察到约 110 MB 启动、所有菜单使用一次后约 130 MB；两类数据分别记录。

## 测量条件

- Windows 11 x64 build 26300，Intel Core Ultra X7 358H，Windows HDR 开启、系统深色主题、原生 Mica、默认窗口尺寸。
- 相同设置，.NET SDK 11.0.100 / runtime 11.0.0、Windows App SDK 2.6.5，内置官方 Python 3.15.2。未执行编码任务，空闲 GUI 没有常驻 Python / FFmpeg / ffprobe。
- 每次只运行一个待测 FrameFlow。等待进程年龄至少 30 秒后，使用只读进程性能计数器采样三次，间隔约 1 秒。启动采样前不获取界面树或截图；不强制 GC、不裁剪工作集、不清理系统缓存、不修改系统输入法。
- 已经使用过的桌面会话，不是重启后的冷缓存实验；短期重复结果不是长时间泄漏检测或启动耗时基准。
- 单位 MiB = 2²⁰ 字节。私有工作集是驻留私有页面；私有提交与包含共享映射的总工作集另列，不能互换。

原始计数器样本保存在 [memory-3.6.4.json](data/memory-3.6.4.json)，不含进程 ID、用户路径或偏好文件。复现脚本见 [qa/MemoryChecks](../qa/MemoryChecks/README.md)。

## 启动结果

下表列每次运行的首个样本；本表各运行的三个样本读数保持一致。

| 版本 / 运行 | 私有工作集 MiB | 私有提交 MiB | 总工作集 MiB |
| --- | ---: | ---: | ---: |
| 3.6.3 JIT / 1 | 121.754 | 151.125 | 255.801 |
| 3.6.3 JIT / 2（隔离补测） | 122.855 | 151.816 | 256.805 |
| 3.6.4 AOT 单文件 / 1 | 117.293 | 145.699 | 216.844 |
| 3.6.4 AOT 单文件 / 2（隔离） | 115.992 | 143.230 | 213.660 |

私有工作集均值约 122.3 / 116.6 MiB，相差约 5.7 MiB；私有提交也有所下降。总工作集降低约 40 MiB，其中包含共享映射和驻留差异，**不能说任务管理器中的私有占用下降了 40 MiB**。样本量不足以建立统计置信区间，也不将这些数字归因到某一个控件。

一次原版重复采样时，前一个 AOT 测试窗口意外残留；另一次 AOT 采样因重复进程被脚本拒绝。两者均未纳入表中，随后关闭测试实例并补测。初次启动新构建时的大量映射、R2R、切换 Mica 宿主和临时焦点方案属于探索数据，没有混入最终对照。偏好设置在补测后逐字节核对一致。

## 菜单使用后的观察

维护者手动确认 AOT 对照版的菜单、转码和 CLI 正常，报告任务管理器中启动约 **110 MB**，所有菜单使用一遍后约 **130 MB**；此前报告相应状态约 120 多 / 150 多 MB。该观察发生于用户实际操作，未固定采样时刻、窗口焦点及计数器口径，不用它计算精确百分比。

在维护者要求停止重复功能测试后，本轮补测只处理内存和打包，不重复菜单操作、编码或 CLI 集成场景。不能把 3.6.3 的 33 个 CLI / 9 组 worker 回归记录写成本版重新执行的结果。

## 为什么仍超过 100 MiB

同一环境中，独立极简 WinUI 对照只保留 Mica、标题和两个文本框，无 FrameFlow 转换逻辑、Python 或 FFmpeg，私有工作集也约 **103.9 MiB**。这说明这台机器上 WinUI、文本输入、字体和桌面合成等基础设施已经有明显成本；它不是所有原生应用或所有 WinUI 应用的内存下限，也不是对各模块堆分配的精确归因。

Native AOT 消除主界面运行时的 JIT 需求，但不会移除 WinUI 控件和系统图形基础设施。[微软 Native AOT 说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)描述的是部署方式与通常收益，具体效果仍以实际应用测量为准。本项目没有为降低显示数字移除 Mica、阴影、动画或中文输入支持。

## 实现与分发

- 将运行时 `XamlReader` 加载改为编译 XAML；本地化使用生成的静态控件映射及类型明确的属性赋值。
- 设置、翻译资源和 worker 请求采用源生成 JSON，保留原设置字段、Unicode 路径及单行 worker 协议。
- 启用当前 WinUI 稳定版提供的样式、图标及上下文菜单延迟初始化选项；缓存主视图控件引用；文件路径和日志关闭不需要的拼写检查与文字预测。
- AOT 主程序使用 `App/packages.aot.lock.json`；普通 JIT 锁文件与 `-Compilation Jit` 回退保留。
- `Bundle/` 使用原 .NET 单文件提取机制。GUI 启动后入口退出，实测只留下缓存中的 AOT 主程序；不是通过忽略一个常驻启动器来获得低数字。CLI 模式入口需等待子进程，其占用应与任务进程一起统计，不能套用 GUI 空闲结果。
- 原有 .NET、Windows App SDK、Python 许可继续保留；依赖收集合并两个构建项目，并增加实际 Native AOT 包的许可与声明。新构建在漏读 WinUI 依赖记录时会失败。仅下载一个 EXE，不要求额外安装运行时。

## 验证范围与限制

Native AOT 及单文件发布构建完成；普通 JIT 回退构建也以零警告、零错误完成。**539 项语言 / 资源检查、32 项设置和 JSON 协议兼容性检查**通过，57 条静态视图映射与 XAML 一致。AOT 构建没有 IL2026 / IL3050 或 WinRT AOT 兼容性警告；保留 .NET 单文件磁盘提取的 NETSDK1244 提示，原因见[依赖说明](dependencies.md)。

前期普通编译候选已进行外观和真实 GUI HEVC / QSV 转码检查；随后 AOT 对照的功能由维护者确认。本轮最终打包层经过构建、代码检查、实际 GUI 启动及进程退出/内存采样，**没有重新执行最终打包层的 CLI 管道与取消集成测试**。这些限制应与维护者的实测确认一同阅读。原编码预设、LUT 数值和 FFmpeg 参数未修改，未重新进行画质或编码速度标定。

## English

On the maintainer's Windows 11 machine, with HDR, native Mica and identical preferences, two isolated startup runs measured 121.8–122.9 MiB private working set for 3.6.3 and 116.0–117.3 MiB for the AOT single-file candidate. This is a modest local reduction of about 5–6 MiB, not a universal guarantee. The larger total-working-set difference includes shared mappings. The maintainer separately reported approximately 110 MB at launch and 130 MB after visiting menus; those manual observations are not a matched counter benchmark.

The standard .NET single-file entry extracts dependencies, starts the AOT UI and exits. It adds no resident GUI supervisor; CLI use retains a waiting entry process and must be measured separately. Native Mica, HDR popup isolation, shadows and animations remain. The minimal WinUI control case was about 104 MiB on this machine, not a universal lower bound.

Release compilation, 539 localization/resource checks and 32 settings/JSON compatibility checks passed. The maintainer confirmed prototype menus, conversion and CLI behavior; at their request no repeated encoding or CLI integration run was performed. The final packaging handoff was checked by actual GUI startup and process counters, without rerunning its CLI pipe/cancellation integration cases. See the sanitized samples and scripts linked above for the scope and exclusions.
