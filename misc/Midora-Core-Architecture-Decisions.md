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

- 已建立 `.midora` v1 持久化契约基础：严格 `manifest.json` DTO/codec、Draft 2020-12 common/manifest schema、Edition 2024 protobuf 通用类型、descriptor hash 与 golden bytes；完整 package 打开/保存和其余结构性文件 schema 尚未实现。
- Project 源数据中的稳定 ID、显式顺序、opaque sRGB 颜色、C# Mapping 源码和版本化设置属于未来 `.midora` 内容；只有发布了对应结构性文件 schema 后才形成文件兼容承诺。
- Canonical result、编译 checkpoint、fingerprint、sample-domain 计划、PCM ring、播放状态、Mute/Solo 和诊断结果都是派生或运行时数据，不属于 Project 持久内容。

### 1.7 运行时归属

- `ProjectCompilationSession` 持有 Project、编译缓存、最后成功结果和 sample-domain 缓存；仅在 Stopped 状态提交编辑。
- 每次成功编辑形成明确的 `ProjectChangeSet`，立即执行增量编译并原子替换最后结果；失败结果保留诊断但不替换最后可消费结果。
- sample-domain 缓存键至少包含 canonical result 指纹、编译范围和实际采样率；Project 音乐语义、Tempo 或采样率变化时失效。
- 播放控制负责 Start、Stop、Seek、Mute/Solo 的运行时过滤和后端资源清理，不拥有 Project 语义。Mute/Solo 通过 source-index 控制命令在合成推进边界生效，不触发 canonical 重编译或后端冷启动；已进入音频子进程内 render-ahead PCM buffer 的音频仍受相应 buffer 延迟约束。

### 1.8 明确非目标

- 本轮不实现完整 `.midora` ZIP 打开/保存事务、其余顶层对象 schema、WPF UI、MIDI 文件导出、传统 MIDI OUT、Pause、Scrub、录音、多 SoundFont、MIDI 2.0、VST 或 Voice Stealing。
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

## 6. ADR-CORE-005（已接受，17A）：C# Mapping ABI v1

决定：初版 C# Mapping Function 使用版本化的 `Midora C# Mapping ABI v1`。Project 源数据只保存 `abiVersion = 1`、函数体源码和声明的 Context 字段；编译产物、程序集和缓存不进入 `.midora`。

ABI v1 固定以下持久兼容边界：

```csharp
double Transform(double value, in MappingContextV1 context)
```

- `MappingContextV1`、`MappingStableIdV1`、`MappingTargetParameterV1` 和 `MappingEventKindV1` 位于独立、只读且不引用 Midora Domain/Compiler 的契约程序集。
- 源码是上述固定方法的方法体，不是完整 compilation unit；包装类、方法名、参数名和类型由 ABI 固定。
- 语言版本固定为 C# 14，编译目标引用面固定为 `Microsoft.NETCore.App.Ref 10.0.10` 和 ABI v1 契约程序集。不得引用 Midora Domain/Compiler、WPF/WindowsDesktop 或第三方程序集。
- 引用白名单只用于持久兼容和依赖收敛，不是安全边界。SRS 9.6.2 的“自由 C#、无 sandbox”保持不变；`System.IO`、时间、随机数、反射和外部状态仍可经普通 .NET API 使用，Midora 不保证这类函数可复现或安全。
- Roslyn 固定为 `Microsoft.CodeAnalysis.CSharp 5.3.0`，并使用 Release、deterministic、允许 unsafe 的 C# 14 固定 profile；生成程序集名称由 ABI 版本与完整函数体 UTF-8 SHA-256 确定，不使用 GUID、时间或编译历史。允许 unsafe 是 SRS 9.6.2“完整自由 C#、不做 sandbox”的直接结果；`in MappingContextV1` 只提供普通 C# 语言层的只读调用约束，不是安全隔离。

Compiler 在 canonical 编译阶段执行 Mapping Function，活动音频线程不编译也不调用 Project 源码。每个打开 Project 的缓存按 `ABI version + compiler profile + 函数体精确 UTF-8 SHA-256` 索引；相同函数体可共享当前缓存项，声明字段不改变生成代码，因此不进入代码缓存键，但仍进入 Project/source fingerprint 和兼容性校验。

每个成功代码缓存项由独立 collectible `AssemblyLoadContext` 持有。缓存同步时只保留当前 Project 仍存在的源码修订；编辑、删除、切换 Project、显式清缓存或关闭 Project 后，旧项在没有在途编译调用时释放委托并调用 `Unload()`。缓存大小因此受当前 Project 中不同函数修订数约束，不随编辑历史无界增长。Project 编译会话和 Compiler 都提供显式释放入口；终结器只作为未正确释放时的兜底。

未来 ABI 变更必须增加 `abiVersion` 并保留旧 ABI 执行器或提供显式迁移；不得让 `LanguageVersion.Latest`、运行机器 TPA、应用内部程序集或当前进程已加载程序集静默改变旧 Project 的编译面。编译错误、未知 ABI、异常和非有限结果均转为可定位诊断，并按是否实际参与路径决定 Error/Warning。

## 7. ADR-CORE-006：动态 Mute / Solo 与进程边界

决定：sample-domain 计划携带去重后的 Track source table，事件只保存连续 `SourceIndex`。渲染器为每个 source 保存预分配 enabled 位；实时线程只读取位和固定容量的无锁命令批次，不读取领域模型，也不分配托管对象。

禁用 source 时，播放层按当前 render frontier 向该 source 仍占用的 Channel Unit 发送精确清理（All Notes Off、All Sound Off、Reset All Controllers 及 canonical reset defaults）；重新启用时先恢复该范围起点必要的非 Note 状态，再放行未来 canonical 事件，不重触发已经越过起点的 Note。内部音频子进程使用独立控制管道传递同一固定协议，Project 和编译器不进入子进程。

这是运行时消费过滤，不改变 Project、Canonical Compiled Result 或编译 fingerprint。可听响应时间仍包含音频子进程内已经生成的 render-ahead PCM 和设备 buffer；正式拓扑不跨进程传输实时 PCM。

## 8. 领域实现表示说明

- `MidoraColor` 已与 18A 文件兼容决定统一为三个 byte 分量的 opaque sRGB；不再保留 alpha/ARGB 内存入口，protobuf 和 JSON 表示分别服从 SRS 16.13.8。
- Event Instrument Library 的 Create / Rename / Duplicate / Delete 是内存领域服务；Duplicate 重建内部稳定 ID 和内部引用，Delete 在明确确认被引用对象后解除 Track binding 并保留 Track 内容。

## 9. ADR-CORE-007（已接受，18A/18.1A）：`.midora` v1 序列化兼容基线

决定：轻量结构性文件固定使用 JSON Schema Draft 2020-12 和内部版本化 `System.Text.Json` source-generated DTO；重对象固定使用 protobuf Edition 2024、Google.Protobuf 3.35.1 与 Grpc.Tools 2.83.0。`.proto` 和 runtime `FileDescriptorSet` SHA-256 基线提交到仓库，生成 C# 只存在于 `obj`。JSON 未知/重复属性和 protobuf descriptor 未知 tag 均在领域反序列化前拒绝。

Requirement trace：

- 输入：v1 manifest DTO、已发布 protobuf message、UTF-8 JSON/protobuf wire bytes，以及已确认的稳定 ID、文本、路径、颜色和 metadata 基础值。
- 正式输出：字段顺序和 LF 固定的 UTF-8 无 BOM JSON；固定 runtime/profile 下的 deterministic protobuf bytes；版本控制中的 Draft 2020-12 schema、`.proto`、descriptor hash 与 golden bytes。
- 边界：文本上限按 Unicode scalar；相对路径保留大小写和原 Unicode、不 normalization；颜色为 opaque sRGB；UTC 时间严格为七位小数秒 `Z`；总耗时为非负 int64 毫秒。
- 失败条件：BOM、JSON 重复/未知字段、未知 protobuf tag、错误 wire type、非法 UTF-8、越界标量、非 canonical hash/path/version 或 descriptor/golden 漂移均失败，不截断也不静默修复。
- 诊断：当前 codec 以 `JsonException` / `InvalidDataException` 保留失败类别；完整打开流程实现时再映射为 SRS 第 16.20 节的文件级正式诊断，不能把异常文本直接当 UI 诊断协议。
- 持久化归属：本 ADR 只冻结通用值类型和 `manifest.json` v1；受 SoundFont、工程耗时累计、MIDI 导出和文件命名决定影响的 `project.json`、`metadata.json`、settings 与两类对象完整 schema 尚未发布。
- 运行时归属：DTO、descriptor、codec 和校验属于 Preparing/open/save 路径，不进入编译器 canonical 语义或音频活动线程。
- 明确非目标：本增量不实现 ZIP 结构、hash 全包校验、迁移、损坏占位、Save/Save Copy 原子事务和完整 Project round-trip。

兼容规则：已发布 protobuf 字段号不得复用，删除字段必须 reserved。deterministic protobuf 不是跨 library/tool 版本的 canonical encoding；依赖升级必须显式评审 descriptor diff、golden bytes 和旧文件重开。完整 v1 对象 schema 只能在决定 19–22 闭合对应字段后发布，不能用临时默认值提前冻结。
