# 安全政策 / Security policy

[简体中文](#简体中文) · [English](#english)

## 简体中文

### 私密报告

使用 [GitHub 私密漏洞报告](https://github.com/MIKU552/LumaRecode/security/advisories/new)，或发邮件到 <lumarecode@miku552.top>。请说明应用版本、平台、影响和触发条件。需要共享日志或素材时，请先移除私人信息并确认分享权限。安全修复以最新稳定版为目标。

### 审查范围

LumaRecode 是处理本地素材的 Windows 和 macOS 桌面应用。两端 UI、CLI、共享核心、FFmpeg 调用以及项目的构建与分发流程都在审查范围内。视频、容器与元数据、文件路径、项目选项及构建时获取的第三方文件都可能包含不可信数据。

重点检查边界上的解析与长度验证、临时文件和输出路径、子进程的工具选择和参数、取消时资源回收，以及发布包与凭据的访问控制。能导致跨越用户授权读写文件、启动非预期程序、泄露数据、破坏分发完整性或低成本消耗大量资源的问题，都可以报告。影响取决于触发条件和实际后果。

仅在上游可复现的漏洞可先向该组件维护者报告。若项目分发了受影响版本，或调用方式扩大了影响，也请同时告知本项目。

## English

### Private reporting

Use [GitHub private vulnerability reporting](https://github.com/MIKU552/LumaRecode/security/advisories/new) or email <lumarecode@miku552.top>. Include the app version, platform, impact and triggering conditions. Remove private information from logs or media and confirm you have permission to share them. Security fixes target the latest stable release.

### Review scope

LumaRecode is a Windows and macOS desktop app that processes local media. Review covers both user interfaces, the CLI, the shared core, FFmpeg invocation, and the project's build and distribution workflows. Media, containers and metadata, paths, job options and third-party files fetched during builds can be untrusted.

Review parsing and length validation at those boundaries, temporary and destination paths, executable selection and child-process arguments, resource cleanup after cancellation, and access to release artifacts and credentials. Report unauthorized file reads or writes, unintended process execution, data disclosure, loss of release integrity, and disproportionate resource exhaustion. Severity depends on practical triggering conditions and impact.

Report vulnerabilities that reproduce only upstream to the component maintainer first. Also tell this project if it distributes an affected version or its integration increases the impact.
