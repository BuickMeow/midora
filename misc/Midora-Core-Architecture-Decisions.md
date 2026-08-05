# Midora 领域、编译与播放核心架构决策

状态：核心候选已实现并进入回归验证（2026-08-05）；未决项与非合规过渡实现见一致性审计记录
适用范围：内存 Project 领域模型、语义验证、全量/增量编译、Canonical Compiled Result、播放计划缓存与无 UI 播放控制
上位规范：`Midora-SRS-Initial-Release-v0.1`。本文是实现决策，不是需求规范；冲突时以 SRS 为准。

## 1. Requirement trace

### 1.1 输入

- 正式输入是完整的内存 Project：TPQ、Conductor Track、Event Instrument Library、SubVoice、模板事件、Mapping、Logical Parameter、Lifecycle、Loop、Envelope、Overlap、Global Initial/Reset Defaults、Logical Track、Segment、Logical Note 和参数 Lane。
- 每个正式对象用 Project 内稳定 ID 标识；显示名称、列表位置、tick、Port 和 Channel 不构成身份。
- 编译请求还包含 `[startTick, endTick)`、Track 选择、用途和 Warning-as-error 策略。
- SoundFont、输出设备、设备实际采样率、Master/Limiter 和实时 buffer 不改变 tick-domain canonical MIDI 语义。

### 1.2 正式输出

- `CanonicalCompiledResult` 是播放与本轮离线试听的唯一音乐语义输入。
- 结果冻结保存 Conductor 状态、按稳定顺序排列的 Channel 事件、Port/Channel Unit 分配、资源占用、来源追踪、诊断、统计、CompileContext、可消费性和 partial 标记。
- 音频适配层只把 canonical tick 事件经 Tempo Map 映射到绝对 sample-frame，并生成现有 `MidiRenderPlan`；它不得重新解释 Event Instrument、Mapping、Lifecycle、Segment 或资源分配。

### 1.3 边界

- tick 使用 `Int64`，TPQ 在 Project 创建后固定；所有范围使用 `[startTick, endTick)`。
- 单 Project 最多 16 Ports × 16 Channels；每个有输出的 Event Instrument Instance 按 SubVoice 数原子分配 Channel Group，低编号优先，允许跨 Port，不做 Voice Stealing。
- 每个 Port 的 Channel 10 都是 melodic；CC91/CC93 在源模型验证阶段为 Error，不能进入 canonical result。
- Segment End、显式编译 end 和作为默认范围的 Project End Marker 是硬边界：立即精确 NoteOff，再 Reset，不允许 Release/Tail 越界。
- 范围起点恢复必要非 Note 状态，但不重触发范围前已经开始的 Note。

### 1.4 失败条件

- 结构、引用、范围、值域、Mapping、Lifecycle、Loop/Envelope、Segment 重叠或资源分配非法时，结果不可消费。
- 实际使用的 Mapping Function 编译失败、抛异常、返回 NaN/Infinity，或映射结果不能按显式策略合法化时，编译失败。
- 任一最终 Note、CC、Program、Bank、Pitch Bend、RPN/NRPN 或 Pitch Bend Range 超出合法范围且没有合法 Clamp 策略时，编译失败；Note number 不允许 Clamp。
- 超过 256 Channel Units 或 Channel Group 不能原子分配时为 Error；峰值 `>= 248` 且未超限时只产生 Info。
- 正式消费者只能消费与当前用途匹配的成功、完整结果；Full Project、Playback、Range 和 Preview 的成功结果都不是 partial。失败时若未来返回 partial 数据，它只能用于诊断且不可消费。无有效 SF2 或没有可用输出设备只阻止发声，不改变编译结果。

### 1.5 诊断

- 诊断级别为 Error、Warning、Info、Debug；Warning-as-error 只改变本次成功判定，不改写诊断级别。
- 诊断尽量携带 Track、Segment、Logical Note、Event Instrument、SubVoice、模板事件、Mapping、tick 和资源上下文；初版 Project 本身没有稳定 Project ID。
- 未绑定 Event Instrument 的非空 Track 为 Info；断裂参数 Lane 为 Warning；CC91/CC93、非法实际输出和资源不足为 Error。

### 1.6 持久化归属

- 本轮源模型保留未来持久化所需的稳定 ID、显式顺序和 C# Mapping 源码，但不实现 `.midora` 读写。
- Canonical result、编译 checkpoint、fingerprint、sample-domain 计划、PCM ring、播放状态、Mute/Solo 和诊断结果都是派生或运行时数据，不属于 Project 持久内容。

### 1.7 运行时归属

- `ProjectCompilationSession` 持有 Project、编译缓存、最后成功结果和 sample-domain 缓存；仅在 Stopped 状态提交编辑。
- 每次成功编辑形成明确的 `ProjectChangeSet`，立即执行增量编译并原子替换最后结果；失败结果保留诊断但不替换最后可消费结果。
- sample-domain 缓存键至少包含 canonical result 指纹、编译范围和实际采样率；Project 音乐语义、Tempo 或采样率变化时失效。
- 播放控制负责 Start、Stop、Seek、Mute/Solo 的运行时过滤和后端资源清理，不拥有 Project 语义。Mute/Solo 通过 source-index 控制命令在合成推进边界生效，不触发 canonical 重编译或后端冷启动；已进入 render-ahead / IPC PCM buffer 的音频仍受相应 buffer 延迟约束。

### 1.8 明确非目标

- 本轮不实现 WPF UI、`.midora` 文件、MIDI 文件导出、传统 MIDI OUT、Pause、Scrub、录音、多 SoundFont、MIDI 2.0、VST 或 Voice Stealing。
- 本轮不把 BASS handle、WASAPI 设备或 sample-frame 写入领域模型或 canonical result。

## 2. ADR-CORE-001：分层与冻结边界

决定：新增三个单向依赖层。

```text
Midora.Domain
    ↓
Midora.Compiler
    ↓
Midora.Playback
    ↓
现有 Midora.Audio / BASS / AudioDevice
```

领域模型保存可编辑源数据；编译器输出冻结连续数组；播放层只做范围过滤、tick→sample、缓存和后端生命周期。Canonical result 不引用 BASS、WASAPI 或 WPF。

## 3. ADR-CORE-002（候选）：确定性曲线离散化 v1

已实现候选：编译器按整数 tick 采样连续曲线和 Envelope，包含曲线有效区间端点；阶梯段保持前值，直线与自由手绘段按相邻控制点线性插值。每个目标在同 tick 只输出最终合成值，跨 tick 的相同状态值暂不折叠。

依据：Project 的最高时间精度就是 tick；按 tick 采样不会引入第二个隐藏时间网格，结果与输出设备采样率无关，且可通过连续数组和预估容量实现线性时间复杂度。

限制：这是实现 v1，不是 SRS 固定算法。若极长曲线的事件量或听感测试不能接受，必须以新 ADR 和 golden vectors 修改，不能静默改变。

## 4. ADR-CORE-003（非合规过渡原型）：Track 级增量缓存

当前原型：全量编译仍是 oracle。增量编译按 Logical Track 缓存“已展开但未分配 Channel Unit”的冻结片段；ProjectChangeSet 与覆盖全部编译输入的 source fingerprint 共同决定 Track 片段复用。随后始终对本次上下文的全部实例重新执行确定性低号优先资源分配、全局排序、范围恢复和硬边界裁剪。

该原型避免未变化 Track 的 Mapping、曲线、Loop 和模板展开工作，同时不保留历史 Port/Channel 分配；但它不满足 SRS 12.21.3 强制的 Checkpoint + Dirty Range + State Hash 收敛模型，不能作为初版合规实现。正式实现必须补齐该模型，并继续以 Full Compile 逐字段等价作为门槛；这不是由基准结果决定的可选优化。

## 5. ADR-CORE-004：tick 到 sample-frame 映射候选 v1

已实现候选：音频适配器按完整 Tempo Map 积分得到相对所选范围起点的绝对秒数，再使用 decimal 算术计算 `seconds × sampleRate`，最终以 `MidpointRounding.AwayFromZero` 舍入到 `Int64` sample-frame。每个 Tempo 分段的累计秒数不在分段边界提前取整。

这是为本轮端到端试听采用的明确候选，不是 SRS 已规定规则。它替代“各调用点自行截断”的隐式行为，并保证相同输入、采样率和运行时下稳定、单调。正式发布前仍需用极端 Tempo、长时间累计和所有合法采样率的 ADR 测试向量确认或升级。

## 6. ADR-CORE-005：C# Mapping 执行

决定：C# Mapping Function 保存函数体源码和声明的 Context 字段。Compiler 使用 Roslyn 在 Preparing/Compilation 阶段编译为委托，并按源码、声明字段和编译器版本缓存；活动音频线程不编译也不调用 Project 源码。Mapping 执行发生在 canonical 编译阶段。

初版按 SRS 不提供 sandbox，也不声称确定性或安全隔离。编译错误、异常和非有限结果均转为可定位诊断并使实际使用路径失败。

## 7. ADR-CORE-006：动态 Mute / Solo 与进程边界

决定：sample-domain 计划携带去重后的 Track source table，事件只保存连续 `SourceIndex`。渲染器为每个 source 保存预分配 enabled 位；实时线程只读取位和固定容量的无锁命令批次，不读取领域模型，也不分配托管对象。

禁用 source 时，播放层按当前 render frontier 向该 source 仍占用的 Channel Unit 发送精确清理（All Notes Off、All Sound Off、Reset All Controllers 及 canonical reset defaults）；重新启用时先恢复该范围起点必要的非 Note 状态，再放行未来 canonical 事件，不重触发已经越过起点的 Note。内部音频子进程使用独立控制管道传递同一固定协议，Project 和编译器不进入子进程。

这是运行时消费过滤，不改变 Project、Canonical Compiled Result 或编译 fingerprint。可听响应时间仍包含已经生成的 render-ahead PCM、设备 buffer，以及子进程模式下的 IPC audio buffer。

## 8. 领域实现表示说明

- `MidoraColor` 当前以 32-bit ARGB 值表示颜色；SRS 规定颜色元数据能力，但没有固定内存表示。这是领域实现选择，不是文件格式决定。
- Event Instrument Library 的 Create / Rename / Duplicate / Delete 是内存领域服务；Duplicate 重建内部稳定 ID 和内部引用，Delete 在明确确认被引用对象后解除 Track binding 并保留 Track 内容。
