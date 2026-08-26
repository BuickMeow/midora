# Midora 本地发布脚本 Requirement Trace

状态：已实施；适用于本地预览，不创建 GitHub Release 或 Git tag

上位规范：《Midora SRS》§21.1、§21.2、§21.6、§21.7，AGENTS.md §3、§4

## 输入与输出

- 输入：`eng/Version.props`、当前 Git commit/worktree 状态、Release 源码、固定 `win-x64` BASS baseline。
- 当前展开目录：`dist/midora/`。
- 历史压缩包：`dist/history/`。
- 当前压缩包：`dist/midora-<semver>-win-x64-<short-commit>[-dirty].zip`，内部固定只有一层 `midora/` 根目录。
- 完整性旁车：与 ZIP 同名的 `.sha256`。

## 发布边界

- `Publish-MidoraLocal.ps1` 只发布 `Release`、`win-x64`、self-contained 产物，不依赖目标计算机预装 .NET 10 Runtime。
- Desktop 使用 single-file apphost，不启用 WPF 不安全的 trimming；Worker 使用正式 Native AOT 单 EXE。
- BASS、BASSMIDI、BASSWASAPI 是 Worker 动态加载且独立授权的原生 DLL，必须与固定 `native-manifest.json` 一起保留，不能合并进 Worker EXE。
- 根 MIT License、第三方 notices 和字体许可证继续作为可见文件随包提供；所有 PDB 在打包前删除。
- dirty worktree 允许用于本地预览，但文件名必须明确记录 `dirty`，不得冒充可复现正式发布；EXE ProductVersion 继续记录完整 commit build metadata。
- Project Format schema/descriptor baseline 只属于源码、测试和开发者材料，不进入终端用户包；用户包也不携带内部 release manifest。
- self-contained publish 必须从 locked restore 的精确 .NET runtime pack 和 Roslyn package 复制许可证/第三方声明到 `licenses/`，不得使用与实际产物版本脱节的通用副本。
- 压缩 single-file Desktop 产物必须能在不暴露 `TRUSTED_PLATFORM_ASSEMBLIES` 文件列表的宿主条件下编译并执行 Batch Edit 受限表达式；Batch Edit 不得要求旁置 reference assembly、动态 Emit DLL 或可回收 ALC。

## 文件操作与失败条件

- 递归删除只允许作用于解析后精确等于仓库 `dist/midora` 的非 reparse-point 目录。
- 只把 `dist` 根目录中匹配 `midora-*.zip` 的旧包及其 SHA-256 旁车移到 `dist/history`；同名历史包使用 UTC 时间后缀保留，不覆盖。
- BASS baseline 校验、locked restore、精确第三方许可材料缺失、任一 publish、必需文件检查、内部 artifact 泄漏、ZIP 单根目录检查或 SHA-256 生成失败时脚本失败。
- 本脚本不代表正式发布门已经通过，不执行 Git push/tag/GitHub Release，也不替代 BASS 分发授权核验和完整自动/人工发布验收。
