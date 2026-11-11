# 贡献与开发记录 · Contributors

[简体中文 README](README.md) · [English README](README.en.md)

## 人员与 AI 工具 / People and AI tooling

| 参与者 / Participant | 分工 / Role |
| --- | --- |
| [MIKU552](https://github.com/MIKU552) | 项目维护者；提出需求、提供设备与素材、选择方案，并亲自进行实机使用和结果核验。Project maintainer: requirements, hardware and footage, design decisions, hands-on validation. |
| Codex for Windows — GPT-6.1 Sol, 极高 / Extra high (`xhigh`) | AI 开发工具；完成本项目自主代码、界面、构建、文档与仓库维护操作。AI development tool used for the original project implementation, UI, builds, documentation, and repository operations. |

本项目自主代码与界面实现全部通过上述 Codex 工作流完成。维护者对开发环境、需求和核验工作的说明记录于此。FFmpeg、WinUI 3 及 LUT 等第三方组件不属于本项目重新创作的部分；其作者与许可见 [THIRD_PARTY.md](Worker/assets/THIRD_PARTY.md)。

All original implementation and UI work was produced through the Codex workflow above. This records the maintainer's development setup and validation role. Third-party components retain their own authorship and licenses.

## Git 署名 / Git attribution

2026-11-11，经维护者明确要求，已有提交的作者与提交者统一改为 Codex；对应的版本标签同步更新。此次仅修正署名，保留历史代码树、提交说明、时间和先后关系。提交与标签对象的哈希因此改变。早期源码快照的补录方式见 [HISTORY.md](HISTORY.md)。

On 2026-11-11, at the maintainer's explicit request, existing commit authors and committers were changed to Codex and version tags were updated accordingly. Historical code trees, messages, timestamps, and ordering were preserved; commit and tag object hashes changed. Early history consists of imported source snapshots as documented in HISTORY.md.

署名改写会使原签名失效，因此移除了 4 个旧提交、3 个旧标签上的签名；完整原件保留在维护者本地 Git bundle 备份中。新对象不冒用原签名。The rewrite invalidates the previous signatures, so signatures on four old commits and three old tags were removed. Complete originals are retained in the maintainer's local Git bundle backup; rewritten objects do not reuse those signatures.

Codex 实际编写与执行的提交使用以下作者和提交者身份：

Commits written and executed by Codex use an explicit AI author and committer identity:

```text
Author: Codex (AI agent) <codex@frameflow.invalid>
Committer: Codex (AI agent) <codex@frameflow.invalid>
```

`codex@frameflow.invalid` 是本项目用于标识工具的非投递地址，不是个人邮箱、GitHub 账号或 OpenAI 员工身份。提交正文可附 `AI-Assistant` / `AI-Model` 信息；实际共同参与内容的人员可以用 `Co-authored-by` 标注。不会将尚未发生的人类审阅写成 `Reviewed-by`。

The `.invalid` address is a non-deliverable, project-local tool identity, not a person, GitHub account, or OpenAI employee identity. Commit messages may include `AI-Assistant` / `AI-Model` trailers and appropriate human co-authorship. A `Reviewed-by` trailer requires an actual review.

GitHub 的侧栏或 Insights 贡献者列表根据提交及关联账号邮箱生成，不是通过本文件手工增加。因此，Codex 在这里作为 AI 工具获得明确署名，不保证以独立账号出现在 GitHub 的自动贡献者列表中。[GitHub 贡献者规则](https://docs.github.com/en/repositories/viewing-activity-and-data-for-your-repository/viewing-a-projects-contributors) · [共同作者说明](https://docs.github.com/en/pull-requests/how-tos/commit-changes/creating-a-commit-with-multiple-authors)

GitHub's automatic contributor views depend on commits and account-linked email addresses. This file credits Codex as AI tooling; it does not create an independent GitHub account or guarantee an entry in those views.
