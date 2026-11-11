# 历史源码快照说明

本仓库于 2026-11-11 从本地保留的 11 份源码 ZIP 整理而来。此前开发时没有建立 Git 历史，因此这里的早期提交是对实际源码快照的补录，不是当时实时产生的提交，也没有伪造开发日期。

各快照去掉 ZIP 顶层包装目录后按原始字节导入；早期 Qt 与后期 WinUI 版本使用各自实际目录结构。v3.4.2 / v3.4.3 等兼容性尝试也保留在历史中，不代表推荐使用。没有保存成源码包的临时实验无法完整恢复。

历史 tag 指向对应快照；v3.4.5 tag 在当时的最终源码之上包含仓库维护文件，程序运行代码与已验证的 3.4.5 源码包一致。首次归档仅为 v3.4.5 发布二进制 Release；此后的版本按实际修改正常提交和发布。

2026-11-11，按维护者要求，将原有提交的作者、提交者和版本标签的署名统一修正为 `Codex (AI agent) <codex@frameflow.invalid>`，以反映由 Codex 执行的开发与归档过程。历史代码树、提交说明、时间及版本顺序保持不变；提交和标签哈希随之改变。原 ZIP 的字节与下方 SHA-256 不受影响，Release 二进制也不因署名修正而重新构建。维护者与 AI 工具的分工见 [CONTRIBUTORS.md](CONTRIBUTORS.md)。

改写后的对象移除了不再适用的原签名（4 个提交、3 个标签）；原历史及签名完整保存在本地 Git bundle 备份中。

| 版本 | 主要变化 | 原始源码包 | SHA-256 |
| --- | --- | --- | --- |
| 2.0.0 | PySide6 / Qt 界面；分类编码档、动态范围、采样、自动识别与三组色彩转换。 | `VideoConverter_2.0_Source.zip` | `fbb98ad9b78722402337e81ff88805f026fe4cb47333c7151860fe8609e648e8` |
| 3.0.0 | 迁移到原生 WinUI 3，加入 Mica、主题与系统强调色；完善窗口布局和 fps / 进度 / 剩余时间。 | `VideoConverter_WinUI3_Source.zip` | `139f1b694f2b113133e46a1c46fe5494f942f0747ab5c60fe25c48bbc5d9a92a` |
| 3.1.0 | 独立选择 MP4 / MKV，音轨封装预检；展示位深、采样、音轨及编码器额外参数。 | `VideoConverter_WinUI3_3.1_Source.zip` | `722645783cb405d462b9b29c322427af9294fc980ef678070b24312617997b2d` |
| 3.2.0 | 更名为映流 · FrameFlow，更新窗口、EXE、任务栏与品牌资源。 | `FrameFlow_3.2_Source.zip` | `39134315049a2c4067a06e2c95cbf1d28b0491ed28c9caa809ee1525e0952f54` |
| 3.3.0 | 重新设计现代矢量图标，并提供 PNG / SVG / 多尺寸 ICO。 | `FrameFlow_3.3_Source.zip` | `d3ebab53d0d6265b9508ed2e0cf873b58f4787d3923e62967210b4e6b1453dbc` |
| 3.4.0 | 中英文即时切换、跟随系统及其他系统语言回退英文；增加翻译检查。 | `FrameFlow_3.4_Source.zip` | `62e53acb7b94427506ec18a88cfbdd432e74cfcaeca97e895423732a3e4bc595` |
| 3.4.1 | 将跟随系统 / 浅色 / 深色集中到界面配色面板。 | `FrameFlow_3.4.1_Source.zip` | `dc1fabbf0d99ecee0baae901f8e1e672b6ba5173fa9bba1647379f7b035cb221` |
| 3.4.2 | 尝试修改弹出面板阴影；未解决 HDR 异常，后续版本恢复原生阴影与动画。 | `FrameFlow_3.4.2_Source.zip` | `d3f0315a1c74a1b9022529eb9d46151b53bf29482ad12f9fc9dfbb6fea3d75a3` |
| 3.4.3 | 检测 HDR，使用不透明主背景规避白色亮带；实测有效，但牺牲 Mica 观感。 | `FrameFlow_3.4.3_Source.zip` | `14e60706eba72cc6b33d827029f6bc3aeaf44222cca24c6426e8555bc4437119` |
| 3.4.4 | 升级 Windows App SDK 2.0.1，HDR 使用原生 SystemBackdropElement；Mica 恢复，菜单异常仍在。 | `FrameFlow_3.4.4_Source.zip` | `1de078c422ae83da93d2bb340d9078147bf5cfe210d81d85981997a1a1fa97dc` |
| 3.4.5 | 配色菜单使用独立原生弹出层与不透明主题底色，保留阴影和动画；用户在 HDR 开启时确认亮带及关闭滞后消失。 | `FrameFlow_3.4.5_Source.zip` | `5da27d906cdf5a36533bad6dd8d9fc7a45079281dc29e25827058381a33980f1` |
