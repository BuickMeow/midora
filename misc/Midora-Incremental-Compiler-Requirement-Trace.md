# Midora 增量编译强模型 Requirement Trace

状态：2026-08-06 已实现并通过自动测试；本文是实现追踪，不修改 SRS。

需求依据：SRS 12.21、12.22、12.24，跨系统不变量 INV-009、INV-010、INV-015；实现决策见 ADR-CORE-003。

## 1. 输入与正式输出

- 输入：完整内存 Project、`CompilationRequest`、当前 Project 会话内上一轮 Full/Incremental Compile 生成的运行时检查点，以及调用方提供的 `ProjectChangeSet`。
- 正式输出：与相同输入、相同请求的确定性 Full Compile 逐字段一致的 `CanonicalCompiledResult`。缓存、检查点和 telemetry 都不是正式音乐输出。
- Source 分层：Track 展开上下文 fingerprint 覆盖 TPQ、Global Initial/Reset、Track 稳定 ID/绑定和所绑定 Event Instrument 的全部展开语义；Segment fingerprint 覆盖 Segment 窗口、Logical Note、Logical Parameter Lane/Point/Curve 及稳定 ID。

## 2. Checkpoint、Dirty Range 与收敛

- Full Compile 在每个确定排序的 Segment 入口及 Track 末尾生成检查点。
- Segment 入口状态包含 tick、下一实例 `SourceOrder`、展开上下文 fingerprint 和 state hash；等价判定还逐字段比较原始状态，hash 不是唯一证据。
- 同 Track Segment 不重叠，实例在 Segment End 完整 NoteOff/Reset，普通 Segment 间不继承参数状态；因此入口处活动实例、生命周期、待处理 Reset 和参数继承状态均为空。全局 Channel Unit/Port 分配从不复用，始终完整重算。
- Dirty 起点是新旧有序 Segment source 的首个差异；插入/删除/移动采用新旧候选中的更早 tick。Event Instrument、Global Initial/Reset 或 Track 绑定/身份改变使整个相关 Track 上下文失效。
- 从 Dirty 起点按 Full 规则重编。仅当新旧入口状态完整等价，且后续 Segment source 序列全部相等时复用旧后缀；否则继续到 Track 末尾。
- 范围编译与增量编译分离：缓存的是全上下文原始实例片段，`[startTick,endTick)`、Track/SubVoice 选择、范围起点状态恢复和范围终点硬裁剪每次重新执行。

## 3. 边界、失败条件与诊断

- Semantic Validation 与 Mapping Function 同步/可用性检查先于缓存消费；失败请求不以旧缓存掩盖当前错误。
- 未定义的 `CompilationPurpose`、不存在的 Track，以及不属于本次参与编译 Instrument 的 SubVoice 选择均在语义验证阶段确定性失败，不能退化成静默空输出。
- 失败结果统一设置 `IsPartial=true`、`IsConsumable=false`，并报告 `SemanticValidation`、`InstanceExpansion`、`OverlapValidation`、`ResourceAllocation` 或 `WarningPolicy` 失败阶段；成功结果固定没有失败阶段。Warning-as-error 不改写原 Warning 级别。
- 语义阶段失败仍报告本次 Track 选择范围内的 source Track 数；展开后的失败继续保留已知实例数与 Channel Unit 峰值。partial 结果不暴露可消费事件或分配。
- Segment 内 Note/参数修改至少回退至该 Segment 入口。实例候选数变化导致 `SourceOrder` 不同，必须阻止后缀收敛。
- Event Instrument 定义或 Global Initial/Reset 改变会改变上下文 fingerprint，不允许复用旧 Segment 片段。
- Conductor 每次重新冻结；End Marker、Tempo、Time/Key Signature、Marker、请求范围和输出目的不从 Segment 缓存恢复。
- 缓存片段保留本段 Mapping/展开诊断；复用时按确定 Segment 顺序重放。Track 级空 SubVoice Info 每次从当前合并实例和当前 Instrument 重新生成。
- 全局低号优先资源分配、峰值诊断、canonical 同 tick 排序、范围状态恢复、硬结束和结果 fingerprint 每次重算，因此局部缓存不能固定旧 Port/Channel 或诊断结果。

## 4. 持久化、运行时归属与非目标

- 检查点、source/state fingerprint、冻结 RawInstance 片段和 telemetry 只属于当前 `MidoraCompiler` 与打开 Project 会话。
- 切换 Project、`ClearCache()` 或 `Dispose()` 必须释放缓存；`.midora` 不保存任何编译派生数据。
- 当前实现不在单个 Segment 内建立 Note 级检查点，不缓存全局分配后缀，也不改变 Canonical Result 公共语义。
- SoundFont、播放设备、Mute/Solo、sample-domain 计划和 UI 状态不进入 compiler checkpoint。

## 5. 自动验证门

- 重复 Full Compile 与字典插入顺序确定性。
- 无修改整 Track/Segment 复用及缓存诊断重放。
- 中段 Note 编辑：只重编 Dirty Segment，状态收敛后复用未变后缀。
- Note 数量改变：`SourceOrder` 不收敛，继续重编后续 Segment。
- 删除空 Segment：在后一入口检查点收敛，不重编未变后缀。
- Event Instrument 上下文改变：全部相关 Segment 失效。
- Segment 集合乱序：Canonical Result 不变。
- `ClearCache()` 后全部 Segment 重编；切换为非零范围请求时只复用全上下文 RawInstance 检查点，并重新生成正式范围结果。
- 固定种子 80 轮连续合法 Note 编辑：Incremental 与独立 Full oracle 对 result 状态、范围、fingerprint、statistics、events、allocations、Conductor 和 diagnostics 逐字段相等。
- 语义、展开 MIDI 值域、超过 256 个 Channel Unit、Overlap Reject 及 Warning-as-error 分别锁定失败阶段；所有失败结果锁定 partial/不可消费契约，所有成功结果锁定无失败阶段。
