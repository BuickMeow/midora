# Midora Project History、Modified 与 Undo/Redo Requirement Trace

状态：基础框架已实现；分配稳定 ID 的创建/复制/分割命令等待 Q-NUI-005

日期：2026-08-06

上位规范：SRS 第 3.9～3.12、16.19.4～16.19.5、16.27、19.1～19.4、19.10、20.8、20.12 节及 INV-002、INV-005、INV-010、INV-037。

## 1. 输入与正式输出

- 输入：当前 `ProjectCompilationSession`、Project 来源状态（未保存/已持久化）、一个有名称的可逆 Project command、保存成功通知，以及迁移/损坏回退等非历史修改原因。
- 正式输出：全 Project 统一线性 History、Undo/Redo 可用性和操作名称、结构化 `IsModified`、`NeedsSaveBeforeClose`、当前 canonical 编译结果及冻结的 `ProjectChangeSet`。
- 新 Project 初始构建不进入 History；未保存新 Project 即使 `IsModified == false`，`NeedsSaveBeforeClose` 仍为 true。
- Save 成功把当前历史状态设为新保存点、建立持久化来源并清除外部 dirty reason；Save Copy 不调用该入口，因此不改变路径归属、Modified 或 History。

## 2. 命令与失败原子性

- command 分为只读 `Prepare` 和已准备的 `Apply/Undo`；准备结果明确 `HasChanges` 和受影响的 `ProjectChangeSet`。
- 无实际变化不编译、不创建 History、不标记 Modified，也不发 History/Compilation change event。
- `ProjectChangeSet` 在首次执行前复制冻结；调用方之后修改原集合不能改变 Undo/Redo 的增量编译范围。
- Apply、Undo、Redo 都通过 `ProjectCompilationSession.ApplyReversibleEdit` 执行：先检查 Project Edit Lock，再变更源 Project，然后执行 Incremental Compile。
- 正式应用编辑只经 `ProjectDocumentSession`；底层无 History 的 `ProjectCompilationSession.ApplyEdit` 已收窄为程序集内部测试/适配入口。Domain 的可变源对象仍供编译器与持久化重建使用，应用层不得绕过 History 直接写入。
- 变更或编译抛出异常时立即执行反向动作并 Full Compile 校验恢复状态；恢复失败以包含原错误和 rollback 错误的 `AggregateException` 报告，不伪称成功。
- 语义验证返回不可消费 canonical 是合法的 Project 编辑结果，仍进入 History；只有基础设施异常触发事务回滚。

## 3. 保存点、分支与外部修改

- 每个成功历史状态使用单调的会话内 state ID；该 ID 只用于 History 身份，不进入 Project 或 `.midora`。
- Undo 回到保存点时清除 Modified；Redo 离开保存点时重新 Modified。
- 在 Undo 后执行新命令会丢弃 redo 分支。若已保存状态位于被丢弃分支，当前分支保持 Modified，直到下一次 Save 成功。
- 迁移、settings/conductor 损坏回退等没有普通 Undo entry 的改变使用稳定 external dirty reason；普通 Undo 不清除它，只有 Save 成功清除。
- History entry 保留已准备的反向数据直到 Project 关闭或 redo 分支被丢弃。SRS 未规定初版条目上限；当前不静默裁掉旧历史，优先完整 Undo 与时间性能。

## 4. 锁、持久化和非目标

- Playback、Preview、Explicit Compile、Save/Export/Render 等持有 Project Edit Lock 时，Execute/Undo/Redo 在任何源变更前拒绝。
- History、state ID、command 反向数据、external dirty reason 和 UI 操作名称均为运行时状态，不进入 `.midora`；Project 源变更及 `nextStableId` 仍服从各自正式持久化规则。
- 本切片不实现 field-local/Draft Undo、WPF focus 路由、gesture 合并、UI 展示、autosave/crash recovery 或命令历史持久化。
- 本切片只提供不分配稳定 ID 的属性/设置类通用原子命令。创建、复制、分割等 ID 分配命令的 Undo/Modified 语义由 Q-NUI-005 决定后接入。
