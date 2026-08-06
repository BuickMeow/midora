# Midora Project Save / Save Copy 应用事务 Requirement Trace

状态：已实现并完成自动化验证

日期：2026-08-06

上位规范：《Midora SRS》§3.9～3.10、§16.21～16.23、§19.2～19.4、INV-012、INV-013、INV-032。

## 1. 输入与正式输出

- 输入：当前 `ProjectDocumentSession`、其 `ProjectCompilationSession` 持有的工程时间会话、`.midora` package 服务、当前 Project 路径与文件版本信息、Embedded SF2 运行时资源，以及明确的目标路径/覆盖授权。
- Save Project 正式输出：安全发布后的当前 `.midora` 文件、更新后的 current path/file information、成功保存时间，以及移动到当前 History state 的保存基线。
- Save Copy 正式输出：独立副本文件及该副本的 file information；当前 path、Modified、Undo/Redo History、内存 modified time 和当前 file information 全部保持不变。
- 两种操作都在构建 package 内容前从打开会话的单调工程时间源取得快照；该自动累计不单独标记 Modified。

## 2. 边界与失败原子性

- 未持久化 Project 的首次 Save 必须显式提供完全限定目标；目标已存在时必须单独给出覆盖授权。成功前仍保持 Unsaved，失败/取消不提交 path、file information 或保存基线。
- 已持久化 Project 的 Save 固定覆盖当前路径，不接受新路径；初版不存在 Save As。当前文件的普通 Save 不重复询问覆盖授权。
- Save Copy 目标若等于当前 Project 路径则拒绝并要求使用 Save Project；这是为维持 SRS 已固定的“Save Copy 不改变当前状态”语义所采用的小决定 Q-NUI-015。
- Damaged Event Instrument / Logical Track Placeholder 存在时，协调层在 package staging 前返回结构化不可保存原因；UI 后续不得展示注定失败的 Save 选项。
- Embedded SF2 必须由当前运行时资源 provider 提供且与 Project reference 一致；最终一致性仍由 package 层在写入前和流式复制时复核。
- 同一协调器同时只允许一个 Save/Save Copy；第二项操作直接拒绝，不排队。无论参数错误、取消、I/O、序列化、自校验、发布或清理失败，操作槽都在 `finally` 中释放。

## 3. 诊断、持久化与运行时归属

- 路径/模式误用由应用层 `InvalidOperationException` 或 `ProjectPersistenceUnavailableException` 报告；package 阶段错误继续保留 `MidoraPackageExceptionV1.Stage`、目标/临时/备份路径和原始原因。
- current path、file information、operation-active 和 Embedded runtime resource provider 只属于当前打开会话，不写入 Project/canonical/Undo History。
- Project Metadata、工程时间及正式源对象仍由 package 从当前内存 Project 完整重建；未知和孤立 package 文件不保留。
- 本协调层不拥有或释放 Project/Compiler/Embedded resource，只在任务开始点取得调用方提供的当前资源快照。

## 4. 明确非目标

- 不实现传统 Save As、后台保存队列、autosave、crash recovery、WPF Progress/Dialog、Recent Projects 或跨 Project 合并。
- 不绕过 `ApplicationTaskCoordinator` 的播放 Stop、Project Edit Lock 和全局任务 admission；WPF composition 后续必须通过该入口调用本协调层。
- 不实现 Q-NUI-002 暂停的旧格式成功迁移，也不改变已发布 `.midora` v1 schema。

## 5. 自动化验证门

- 首存路径/file information/保存基线提交与严格重开。
- 首存缺路径、已有目标无授权、普通 Save 改路径、Save Copy 指向当前路径全部拒绝且不改变状态。
- Save Copy 保留 current path、Modified、History、内存 modified time 与当前 file information，同时副本包含当前源内容和副本完成时间。
- 工程会话时间在 Save/Copy 前快照且不单独标记 Modified。
- Damaged Placeholder、缺失 Embedded resource、可用 Embedded resource、取消和并发操作的完整失败/恢复矩阵。
