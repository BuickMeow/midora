# Midora New Project 非 UI 工作流 Requirement Trace

状态：已实现并完成自动化验证

日期：2026-08-06

上位规范：《Midora SRS》§3.2～3.6、§4.1.2、§6.4～6.5、§16.1.1、§19.1、INV-001、INV-008、INV-012、INV-032。

## 1. 输入与正式输出

- 输入：TPQ、用户 Project Metadata、Create Unsaved / Create and Save 模式、可选的 Embedded / External Relative SF2 选择、Create and Save 目标及独立覆盖授权。
- 正式候选输出：完整 `MidoraProject`、Unsaved/Persisted origin、可选 current path/file information、已验证的 effective SF2 path、是否使用唯一 ignore-case 路径回退、Embedded runtime resource ownership，以及 package 诊断。
- 默认 Project 固定含 tick 0 的 120 BPM 与 4/4；不含 Key Signature、Marker、End Marker、Event Instrument、Logical Track、Segment 或 SoundFont。
- 创建构建不进入 Undo History；调用方只在本协调器成功返回完整候选后通过 Project Switch Guard 提交到主窗口。

## 2. 边界与失败条件

- TPQ 必须大于 0，默认 192；创建后由领域模型只读，不能通过编辑命令修改。
- Create Unsaved 不接受目标路径/覆盖授权，返回无 current path 的普通 Project；即使没有后续编辑，`ProjectDocumentSession.NeedsSaveBeforeClose` 仍为真。
- Create and Save 需要完全限定目标路径；已有目标只有在单独冻结覆盖授权后才允许替换。package 自校验与原子发布成功前不返回候选。
- External Relative SF2 仅允许 Create and Save，且必须位于目标 `.midora` 同目录或其直属 `soundfonts/`；绑定、完整 hash、后端可加载性验证和提交前复核全部成功后才写入 Project。
- Embedded SF2 先复制为协调器拥有的运行时快照，再执行后端可加载性验证；创建/保存失败时释放快照，成功后由返回结果持有直至 Project 会话关闭。
- 任一输入、资源、验证、竞态、序列化、I/O、自校验或发布失败都不返回候选，因此调用方保留原 Project；Create and Save 失败不发布半创建目标。

## 3. 时间、持久化与运行时归属

- `createdAtUtc` 在构造候选 Project 时由注入的 UTC 时钟写入；Create and Save 成功时，package 按首次普通 Save 规则提交 `modifiedAtUtc` 和 manifest file information。
- 候选验证、SF2 hash/loadability 与首次 package 写出期间不创建 `ProjectEditingTimeSession`，因此不累计工程总耗时；调用方把成功候选提交为当前 Project 时才构造 `ProjectCompilationSession` 并开始单调累计。
- External absolute path、Embedded 解包/导入快照和验证状态只属运行时；`.midora` 只保存相对 External reference 或 Embedded resource 与源 Project 数据。
- New Project 协调器不替代通用 Project Switch Guard；Stop、Function Draft、当前未保存修改和最终主窗口替换仍由 `ApplicationTaskCoordinator.ExecuteProjectSwitchAsync` 串联。

## 4. 明确非目标

- 不实现 New Project Dialog、Arrangement Workspace 导航、文件选择器、版权提醒文本或其他 WPF 行为。
- 不创建默认 Event Instrument、Track、Segment、Key Signature、Marker、End Marker 或 BASSMIDI stream。
- 不实现模板、跨 Project 导入、后台创建队列、传统 Save As 或 Q-NUI-002 暂停的旧格式迁移。

## 5. 自动化验证门

- 默认领域图、Metadata、Conductor、空集合、无 SF2、稳定初始时间及空 History。
- Unsaved/Persisted origin 与关闭前保存语义、首次 package 严格重开、覆盖授权和失败原子性。
- 非法 TPQ/Metadata/路径/模式组合在资源验证与 I/O 前拒绝。
- External 允许位置、相对引用、hash/loadability、内容竞态和越界目录矩阵。
- Embedded 的 Unsaved/Create and Save、package 重开、运行时资源所有权、验证失败清理。
- 创建候选期间不累计工程总耗时；成功提交后由单调会话开始累计。
