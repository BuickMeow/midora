# Midora Project History、Modified 与 Undo/Redo Requirement Trace

状态：基础框架与九批不分配稳定 ID 的领域对象命令已实现；分配稳定 ID 的创建/复制/分割命令等待 Q-NUI-005

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

### 2.1 已接入的领域命令矩阵

- Logical Track：单行短文本重命名、Event Instrument 绑定/改绑/取消绑定、手动排序、带非空确认的删除；删除与撤销同步恢复 Audio Render 显式 Track 选择。
- Event Instrument：单行短文本和大小写不敏感唯一性重命名、手动排序、移动到 Folder/Unfiled、带引用确认的删除；删除与撤销保留 Track 内容并精确恢复绑定 ID、原索引和 Last Known Name。
- Library Folder：重命名、手动排序、删除；删除只移除 Folder 并把内容移至 Unfiled，撤销恢复 Folder 原索引及成员关系。
- Damaged Placeholder：Event Instrument 与 Logical Track 的可撤销删除；撤销恢复占位对象、原始排序、Track 绑定和 Audio Render 显式选择。
- Segment：同 Track/跨 Track 整体移动、裁剪窗口更新、带非空确认的删除、同 Track 非重叠连接；移动/裁剪阻止负 tick、非正长度、Int64 溢出及同 Track 重叠，连接保留绝对内容位置并采用右侧同 tick 参数点覆盖规则。
- Conductor：在不创建新 ID 的范围内修改/移动/删除 Tempo、Time Signature、Key Signature、Marker 和现有 Project End Marker；tick 0 Tempo/Time Signature 不可移动或删除，同类型事件阻止同 tick 冲突，Tempo 输入必须经一次 `AwayFromZero` 取整后可由 24-bit MIDI Set Tempo 表示。
- Project Settings：原子修改 Playback Master Volume/Limiter/Stop Cursor Behavior，以及 Audio Render 模式、范围、Track 选择、采样率和离线 sample voice 上限；设置在 Prepare 阶段按现行 schema 值域、范围一致性和 live Track ID 集合校验。它们保存进 Project 并进入 History/Modified，但不改变 canonical MIDI 编译结果。
- Logical Note：原子修改 local start、length、pitch、velocity，允许内容留在 Segment 当前裁剪区外，但阻止负 tick、非正长度、Int64 end 溢出和 MIDI 值域错误；删除与 Undo 恢复原对象、原索引和稳定 ID。
- Logical Parameter Lane/Point：删除 Lane 需要显式确认；Point 更新阻止负 tick、NaN/Infinity、同 tick 冲突、越界、非整数 Integer 和非 Step/未定义 Enum 值；断裂 Lane 只允许删除或显式重绑定，不允许在缺少定义值域时继续普通点编辑。
- Lane 重绑定：目标只允许当前 Track 绑定 Instrument 的稳定 Parameter ID，禁止同 Segment 重复 Parameter Lane；调用方显式选择 Clamp 或 Discard，目标为 Enum 时还必须确认整数兼容不代表语义兼容的警告。转换保留 Lane/Point ID，Undo 恢复原 Parameter ID、原 Point 对象和顺序。Q-NUI-007 待确认的局部实现采用 AwayFromZero，并把目标 Enum 的保留点转为 Step。
- Event Instrument 基础属性：Description 按 65,536 Unicode scalar、允许 Tab/LF/CR 但拒绝 NUL/其他控制字符的持久化契约原样保存；Color 与 Description 进入 History/Modified 但不失效 canonical；Root Note 限 0～127、失效相关 Instrument 编译，且不改写 SubVoice override 或模板 Note。
- Event Instrument 生命周期属性：Template Length 只能缩短到仍覆盖 Note end、瞬时事件/Curve Point 半开边界和 Loop End 的长度；Isolation 关闭保留已有 Loop/Envelope/Mapping 等不兼容数据；Loop 只可在 Isolation 开启时启用/编辑，但受限制状态仍可显式禁用；Overlap、Scope 与短/长音策略按已定义枚举原子更新，Let Overlap 只能在 Isolation 开启时主动选择。
- SubVoice：可选短文本名称、Root Note inherit/override、手动排序和删除均进入 Instrument 级编译失效；名称/Root 修改不重写模板 Note。最后一条 SubVoice 不可删除；非空删除需要确认，并同时移除指向该 SubVoice 的 Logical Parameter Mapping；Undo 恢复 SubVoice、全部内部数据、外部 Mapping、原对象、原索引和稳定 ID。
- Template Event：Note、CC、Bank、Program、Pitch Bend、RPN、NRPN 与 Pitch Bend Range 的既有对象可原子更新时间和值；基础 MIDI 值域、CC91/CC93/Channel Mode 禁止项、Int64 end 及事件类型在 Prepare 阶段阻止非法提交。Note end 或瞬时事件的半开边界超过当前 Template Length 时自动延长而不裁剪；同 SubVoice/tick/目标的状态冲突以被编辑对象替换旧对象，Pitch Bend Range 与 RPN 0 按统一内部语义冲突。Bank 只允许移除没有活动 Mapping Step 的已有 MSB/LSB 组件；编辑保留事件、Mapping Chain、Step、Target Settings 及全部稳定 ID，删除/Undo 精确恢复原对象与索引。
- Value Curve：既有 Curve 的 Target Rounding/Overflow、Point tick/value/interpolation、Point 删除与整条 Curve 删除进入 History。Point 更新阻止负 tick、Int64 半开边界溢出、NaN/Infinity、未定义插值和同 tick 重复；合法目标的 Fail/Clamp 策略在提交时生效，移动到当前 Template Length 外会自动延长。Point 更新以同稳定 ID 的不可变记录替换，Undo 恢复原 Point 对象；删除 Curve 不删除同目标的用户离散事件。
- Initial State / Reset Defaults：Project Initial、Project Reset、Event Instrument Initial 与 SubVoice Initial 通过统一 `MidiValueTarget` 更新或删除单个固定值；支持 CC、Bank MSB/LSB、Program、Pitch Bend、RPN/NRPN、Pitch Bend Range semitone/cents，阻止 CC91/CC93、Channel Mode、非法目标身份和全部值域越界。三层 Initial State 按 SubVoice > Instrument > Project > 内置默认合并，Reset 只属于 Project；这些编辑不影响 Template Length、不分配 ID，并保持各 `MidiInitialState` 容器对象身份。对缺失 override 再写 null 是无操作，删除已存在 override 可精确 Undo。
- Envelope Preset：既有 ADSR-like Envelope 的可选名称、Delay/Attack/Hold/Decay/Release tick 与 Start/Peak/Sustain/End 归一化值可原子更新；名称执行通用短文本规则，时长非负，值必须有限且在 0～1。Isolation 关闭时 Envelope 数据保留且不可编辑，但删除仍可作为显式修复；删除被任何启用或禁用 Mapping Step 引用的 Envelope 前必须确认，删除后保留断裂 Envelope ID，Undo 恢复原 Envelope 对象与索引。
- C# Mapping Function：既有 ABI v1 Function 的唯一名称、函数体精确文本和声明 Context 字段集合可原子更新，函数/Step ID 与 ABI 不变；名称及字段执行短文本/唯一性规则，函数体执行有效 Unicode 与 1,048,576 scalar 上限但允许多行自由 C#。源码编译错误是可保存且参与使用时失败的状态。删除被任何 Step 持有 Function ID 的函数前必须确认；删除保留断裂 ID，Undo 恢复原函数对象、源码、声明、索引与引用。
- 以上命令 Prepare 不修改 Project；删除、移动、连接和撤销复用原对象/稳定 ID，不回滚或推进 `nextStableId`。每项结构编辑测试均以 Full Compile 为 oracle 核对当前 Incremental Compile 的语义和形式等价。
- Logical Track 名称可空/重复；Event Instrument 与 Folder 名称必填且分别在规定范围内唯一。所有上述名称先拒绝非法 Unicode、换行/NUL/控制字符，再 Trim，并统一执行 schema 的 256 Unicode scalar 上限，不静默截断。

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
- 本切片提供通用属性命令及上述不分配稳定 ID 的具体领域命令。创建 Event Instrument/Folder/Track/Segment/Note/Lane/Point、复制 Event Instrument/Track/Segment、Segment Split 和创建 Conductor/End Marker 事件等 ID 分配命令的 Undo/Modified 语义由 Q-NUI-005 决定后接入；Q-NUI-003 决定前不扩展 MIDI Export Settings v2。
