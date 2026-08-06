# SubVoice 预览监听状态需求追踪

状态：已实现
日期：2026-08-06

## 1. 需求依据

- SRS 8.39～8.42：SubVoice Mute / Solo 只属于 Event Instrument 编辑器内部预览；允许多条 Solo；存在 Solo 时只播放 Solo 集合；Mute 优先于 Solo；状态不保存进 Project，也不影响正式编译、主播放、MIDI 导出或音频渲染。
- SRS 13.21、13.23：Event Instrument / SubVoice 预览必须继续经过 Preview CompileContext、完整 Event Instrument 生命周期、Mapping 和临时 Channel Unit 分配，不得裸发 MIDI。
- INV-009、INV-011：正式消费者只消费 canonical compiled result；临时监听过滤不是 Project 或成品输出语义。

## 2. 输入与正式输出

- `EventInstrumentPreviewRequest` 新增可选的 `MutedSubVoiceIds` 与 `SoloSubVoiceIds` 临时集合；两者只适用于整个 Event Instrument 的编辑器预览。
- 无 Solo 时，预览包含所有未 Mute 的 SubVoice；存在一条或多条 Solo 时，预览只包含 `Solo - Mute`，因此同一 SubVoice 同时 Mute/Solo 时由 Mute 胜出。
- 解析后的 SubVoice 集合写入临时 `CompilationRequest.IncludedSubVoiceIds`；后续语义验证、生命周期、Mapping、资源分配、canonical 排序和音频消费者保持统一正式管线。
- 专用单 SubVoice 预览继续使用 `SubVoiceId`，默认音高仍取该 SubVoice Effective Root Note；不能同时携带 Event Instrument 级 Mute/Solo 状态。

## 3. 边界、失败与诊断

- `null/null` 表示没有编辑器监听过滤，保持既有“预览全部 SubVoice”行为；显式空 Solo 集合等价于当前没有 Solo。
- 全部 SubVoice 被 Mute 是合法的静音预览：canonical 结果可消费，但没有 SubVoice allocation 或 Note On。
- Mute/Solo 集合在编译入口立即快照；重复 ID、引用其他 Event Instrument 的 ID，以及单 SubVoice 预览与集合状态混用均在创建临时 Project 前以 `ArgumentException` 原子拒绝。
- Mute/Solo 集合的输入顺序不构成语义；实际包含集合按 Event Instrument 的 SubVoice 正式上下文交给编译器，canonical 确定性规则不变。

## 4. 持久化与非目标

- 监听集合不进入 `MidoraProject`、`.midora`、Undo / Redo、Modified、canonical fingerprint 或任何输出设置。
- 本次不实现 WPF 中的 Mute/Solo 按钮和状态容器；后续 UI 只负责维护当前编辑器临时集合并传入请求，不得自行重新解释优先级。
- 本次不改变 Track Mute/Solo、Segment Preview、Logical Track Preview 或 Q-NUI-022 暂停的 held virtual-key Gate 语义。

## 5. 自动验证

- 多条 Solo 同时包含，且其中一条同时 Mute 时，只输出其余 Solo SubVoice。
- 没有 Solo 时只排除 Mute；全部 Mute 时产生合法静音 canonical 结果。
- 重复、外部和与专用单 SubVoice 预览冲突的监听状态全部在编译前拒绝。
- 既有单 SubVoice Effective Root Note、PreviewContext、Playback 互斥和完整正式编译测试继续通过。
