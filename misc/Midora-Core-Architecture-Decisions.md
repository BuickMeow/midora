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

- 已建立 `.midora` v1 持久化契约基础：严格 `manifest.json`、`metadata.json`、`soundfont-settings.json` DTO/codec，Draft 2020-12 schema、Edition 2024 protobuf 通用类型、descriptor hash 与 golden bytes；完整 package 打开/保存和其余结构性文件 schema 尚未实现。
- Project 源数据中的稳定 ID、显式顺序、opaque sRGB 颜色、C# Mapping 源码和版本化设置属于未来 `.midora` 内容；只有发布了对应结构性文件 schema 后才形成文件兼容承诺。
- Canonical result、编译 checkpoint、fingerprint、sample-domain 计划、PCM ring、播放状态、Mute/Solo 和诊断结果都是派生或运行时数据，不属于 Project 持久内容。

### 1.7 运行时归属

- `ProjectCompilationSession` 持有 Project、编译缓存、最后成功结果和 sample-domain 缓存；仅在 Stopped 状态提交编辑。
- 每次成功编辑形成明确的 `ProjectChangeSet`，立即执行增量编译并原子替换最后结果；失败结果保留诊断但不替换最后可消费结果。
- sample-domain 缓存键至少包含 canonical result 指纹、编译范围和实际采样率；Project 音乐语义、Tempo 或采样率变化时失效。
- 播放控制负责 Start、Stop、Seek、Mute/Solo 的运行时过滤和后端资源清理，不拥有 Project 语义。Mute/Solo 通过 source-index 控制命令在合成推进边界生效，不触发 canonical 重编译或后端冷启动；已进入音频子进程内 render-ahead PCM buffer 的音频仍受相应 buffer 延迟约束。

### 1.8 明确非目标

- 本轮不实现完整 `.midora` ZIP 打开/保存事务、其余顶层对象 schema、WPF UI、完整 MIDI 文件事务/多文件工作流、传统 MIDI OUT、Pause、Scrub、录音、多 SoundFont、MIDI 2.0、VST 或 Voice Stealing。
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

## 4. ADR-CORE-003（已接受）：Segment 边界 Checkpoint、Dirty Range 与状态收敛

决定：Full Compile 在每个按 `ProjectStartTick`、稳定 ID 排序的 Segment 入口以及 Track 末尾生成运行时检查点。缓存单元是“已展开、尚未分配 Channel Unit”的 Segment 冻结片段；每个入口检查点保存 tick、下一实例 `SourceOrder`、完整展开上下文 fingerprint 和由这些字段确定生成的 state hash。状态等价判定同时比较 hash 与全部原始字段，不能只凭 hash 命中。

该边界成立的依据是：同一 Track 的 Segment 经语义验证后不得重叠；每个实例都被 Segment End 硬裁剪并在边界发出完整 NoteOff/Reset。因此进入下一 Segment 时，展开阶段不存在跨边界活动实例、生命周期、Logical Parameter 继承或待处理 Reset；唯一跨 Segment 的展开状态是用于稳定排序的下一 `SourceOrder`。Channel Group、Channel Unit、当前 MIDI 状态和资源分配器不进入此中间缓存，因为每次 Full/Incremental Compile 都对全部本次实例重新执行同一确定性全局分配、materialize、排序、范围恢复与硬裁剪。这样不会复用历史 Port/Channel 分配。

Dirty 起点按新旧有序 Segment source fingerprint 的首个差异确定；插入、删除和移动同时取新旧边界的较早 tick。Track/Event Instrument/全局上下文 fingerprint 变化时从 Track 首段回退。显式 `ProjectChangeSet` 标记但 fingerprint 未变化时，仍至少重编一个 Segment 以验证缓存覆盖面。编译器从 Dirty Segment 按 Full Compile 规则向后展开；只有新的检查点状态逐字段等于旧检查点，且其后的 Segment ID、source fingerprint 和顺序全部未变时，才复用冻结后缀。实例计数或过滤状态改变会改变 `SourceOrder`，从而阻止错误收敛并继续重编到上下文末尾。

Conductor、范围、Track/SubVoice 选择和 End Marker 不混入 Segment 缓存：Conductor 每次重新冻结，原始实例缓存保持全上下文，之后再按本次请求重做范围恢复、选择、硬边界与结果 fingerprint。所有语义验证和实际 Mapping Function 可用性检查也在缓存判定前重新执行；缓存只存在于当前 `MidoraCompiler`/Project 会话，切换 Project、显式清理或 Dispose 时释放，不持久化到 `.midora`。

验证门：Full Compile 继续作为 oracle；固定向量覆盖中段编辑、插入/删除空 Segment、实例计数改变、Event Instrument 上下文失效、集合乱序和显式失效，固定种子性质测试在连续合法编辑后逐字段比较 result、Conductor、事件、分配、诊断、统计与 fingerprint。当前粒度不会在单个 Segment 内建立 Note 级检查点，因此长 Segment 的最坏情况是重编整个 Segment；SRS 未规定更细粒度，后续只有在性能证据要求时才可增加内部检查点，且不得改变本 ADR 的状态等价与 Full oracle 门。

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
- 持久化归属：本 ADR 冻结通用值类型和 `manifest.json` v1；`soundfont-settings.json` 与 `metadata.json` 分别随 ADR-CORE-008、ADR-CORE-009 冻结。受 MIDI 导出和文件命名决定影响的其余 settings 与完整对象 schema 尚未发布。
- 运行时归属：DTO、descriptor、codec 和校验属于 Preparing/open/save 路径，不进入编译器 canonical 语义或音频活动线程。
- 明确非目标：本增量不实现 ZIP 结构、hash 全包校验、迁移、损坏占位、Save/Save Copy 原子事务和完整 Project round-trip。

兼容规则：已发布 protobuf 字段号不得复用，删除字段必须 reserved。deterministic protobuf 不是跨 library/tool 版本的 canonical encoding；依赖升级必须显式评审 descriptor diff、golden bytes 和旧文件重开。受决定 21–22 影响的其余 v1 对象 / settings schema 只能在对应字段闭合后发布，不能用临时默认值提前冻结。

## 10. ADR-CORE-008（已接受，19A）：Project SoundFont 可移植引用与内容身份

决定：Project 不保存绝对 `SoundFontPath`。领域源数据使用严格 External/Embedded union；实际解析出的绝对路径、验证中/缺失/歧义/hash mismatch/加载失败状态和验证缓存均属于运行时。`settings/soundfont-settings.json` v1 已冻结为 `schemaVersion`、`mode` 及模式对应的 `relativePath`/`resourceId`、`originalFileName`、`sha256`、`fileSizeBytes`。

Requirement trace：

- 输入：`.midora` 的完全限定路径、用户选择的完全限定 SF2 路径、Project SoundFont 源引用及当前外部文件字节。
- 正式输出：External 保存 `<file>.sf2` 或实际大小写的 `<soundfonts>/<file>.sf2`；Embedded 保存稳定 resource ID；二者都保存原始文件名、完整原始字节 SHA-256 和非负文件大小。
- 边界：只允许 Project 根目录或直属 `soundfonts/`；路径逐分量 ordinal 精确匹配优先，唯一 ordinal-ignore-case 候选可回退并 Warning，多个近似候选为歧义。根目录和 `soundfonts/` 的同名文件由完整相对路径区分。
- 失败条件：选择位置越界、嵌套目录、非 SF2、非法/非 canonical 路径或 hash、文件不可读、大小写歧义、Embedded resource ID 为零，以及 mode/payload 组合不一致均失败。
- 诊断：External 缺失、不可读、case fallback 和 hash mismatch 是资源状态，不是编译诊断；fallback/hash mismatch 为 Warning，缺失/不可读/歧义阻止发声消费者但不阻止 Project 打开、编译或 MIDI 导出。
- 持久化归属：External 的相对路径和最后明确接受的内容身份、Embedded 的 resource ID/内容身份属于 Project；普通保存或被动监控不得更新 External 身份。可访问/加载状态不持久化。
- 运行时归属：解析后的绝对路径、完整 hash 验证结果、文件身份/大小/mtime 缓存和文件监控只属于打开会话。`ProjectCompilationSession.EffectiveSoundFontPath` 是当前过渡运行时注入点，不进入 Project 或 compiler fingerprint。
- 明确非目标：本增量不实现 SF2 格式/BASSMIDI 可加载性验证、完整 package 资源复制、损坏 Embedded 保存策略、文件监控器、UI 接受变化命令或 ZIP 事务。

hash 使用 SHA-256 并流式读取。用户选择、替换、重新绑定或明确接受当前内容时才生成新的源引用；被动验证只返回当前 hash/状态，不修改原引用。打开流程未来必须在结构加载后异步完整验证，验证完成前禁用发声；首次音频任务只能使用仍有效的验证缓存，否则重新验证。

## 11. ADR-CORE-009（已接受，20A）：Project 工程总耗时单调会话累计

决定：工程总耗时从 Project 成功新建 / 打开并成为当前可信 Project 时开始，到关闭流程开始时暂停。空闲、最小化、失焦、模态 UI、保存、编译、播放、预览、Buffering、MIDI 导出与音频渲染全阶段均累计；系统睡眠 / 休眠和关闭流程不累计，关闭取消后从恢复打开状态时继续。自动累计不单独设置 Modified、不进入 Undo / Redo、不更新 `modifiedAtUtc`，也不参与 compiler fingerprint。

Requirement trace：

- 输入：已保存的非负 `totalEditingTimeMilliseconds`、单调 `TimeProvider` timestamp、系统 suspend/resume 和 Project begin/cancel-close 生命周期通知。
- 正式输出：当前内存 Project Metadata 的非递减 int64 整毫秒累计快照，以及严格 `metadata.json` v1 的 `totalEditingTimeMilliseconds`。
- 边界：成功建立 Project 前不计；活动打开会话全部计；任一 pause reason 存在时不计；关闭取消只恢复后续累计，不补计暂停区间。每个 Project 同时只能有一个累计会话。
- 失败条件：持久值为负、时间戳非 canonical、修改时间早于创建时间、同一 Project 重复打开累计 owner、注入时钟倒退或 metadata 字段无效均失败；累计超过 int64 表示范围时饱和到 `long.MaxValue`。
- 诊断：当前领域 / codec 以参数、状态和 `InvalidDataException` 分类；完整应用打开 / 保存流程实现时映射为生命周期或 metadata 文件诊断。
- 持久化归属：用户 metadata、UTC 创建 / 修改时间和已累计整毫秒属于 Project；当前 timestamp、sub-millisecond remainder、pause reasons 与 owner flag 不持久化。Save Copy 只序列化快照，不回写当前 Project 修改时间。
- 运行时归属：`ProjectEditingTimeSession` 使用单调 timestamp 并由当前 `ProjectCompilationSession` 过渡持有；UI 未来负责转发 Windows suspend/resume 和 begin/cancel-close 通知。计时不进入音频 Worker 或音频活动线程。
- 明确非目标：本增量不实现 WPF 电源事件接线、Project Modified/Undo 框架、完整 New/Open/Save/Close/Save Copy 事务、自动保存或崩溃恢复。

`metadata.json` v1 同时冻结项目名称、用户版本、作者/团队、原作、版权、备注、UTC 创建 / 修改时间和总耗时字段。会话内部保留 100 ns `TimeSpan` tick 余数，生成持久快照时向下取完整毫秒；重复取快照不会重复累计同一区间，系统墙钟校时不改变累计值。

## 12. ADR-CORE-010（已接受，21A/22A）：SMF Type 1 兼容编码档

决定：初版 `.mid` 编码固定使用 SMF Type 1 和 Project TPQ。Tempo 以十进制 `60,000,000 / BPM` 计算，并只对最终 microseconds-per-quarter-note 执行一次 `AwayFromZero`；舍入结果超出 `1..0xFFFFFF` 时整体失败。Time Signature 固定写 `cc=24`、`bb=8`。同 tick 的 Bank/Program 字节顺序固定为 CC0、CC32、Program Change。所有文本 Meta 使用严格 UTF-8；事件 Track 只写 Track Name 与 MIDI Port Meta，不写 Device Name / Program Name。每个 Channel Event 都显式写 status byte，不使用 Running Status。

Requirement trace：

- 输入：用途为 `MidiExport`、成功、完整、可消费的 `CanonicalCompiledResult`，以及按 Logical Track 手动顺序提供的显式 Track/Port 名称布局。编码器不读取 Project、播放状态、SoundFont、设备或 Mute/Solo。
- 正式输出：范围起点重基为 MIDI tick 0 的确定性 SMF Type 1 字节；Track 0 为 Conductor，事件 Track 按 Logical Track 布局顺序再按 Port 排序；所有 Track 在统一相对 `endTick - startTick` 写 EOT。
- 边界：Channel Event 逐条保持 canonical 子序列和真实 NoteOff velocity 0；RPN/NRPN/Pitch Bend Range 使用 canonical 已展开的标准 CC；导出器不得折叠状态，不得在 canonical 外追加 All Notes Off、All Sound Off、Reset All Controllers 或其他 Channel 清理。Track Name 的最终可见字符串由上层工作流显式提供，编码器不隐藏选择命名模板。
- 失败条件：非 MidiExport 上下文、不可消费/partial 结果、非法 TPQ、超出四字节 VLQ 的事件间隔、24-bit Tempo 越界、非法 Time/Key Signature、未知 Channel Event、CC91/93、NoteOn velocity 0、非零 NoteOff velocity、路由/来源不一致、Track 布局缺失或自校验失败均整体失败且返回零 partial 字节。
- 诊断：当前垂直切片区分 canonical consistency 与 encoding 两类结构化诊断；完整工作流实现时再接入统一任务/文件写入诊断，不把异常文本当持久协议。
- 持久化归属：SMF 是导出产物，不进入 `.midora`；Track 可见名称布局和输出路径是本次工作流快照。受文件命名决定影响的 Export Settings schema 仍未发布。
- 运行时归属：SMF 组织、字节编码和读取后自校验属于 MIDI 导出 Preparing/Encoding；不进入 compiler canonical 语义，也不进入音频 Worker。
- 明确非目标：本增量不实现按 Logical Track/按 Port 多文件模式、Compact Routing、Readme、临时目录原子发布、覆盖确认、取消/进度、最终文件命名模板和完整 WPF 工作流。

编码完成后必须重新解析并检查 MThd、MTrk 数量与长度、显式 status、可编码 delta、单个最终 EOT、所有 Track EOT tick 一致和文件末尾无额外字节。低层 `StandardMidiFile` 已提供 Type 1 writer/validator；正式消费者 `CanonicalMidiFileExporter` 只接受 canonical 结果。

22A 已固定 Channel 10 melodic 兼容档。每个实际包含 Channel 10 canonical 事件的事件 Track 在相对 tick 0、Track Name 与 MIDI Port Meta 之后、全部 canonical Channel Event 之前，分别写一次 Roland GS Normal Part `F0 41 10 42 12 40 10 15 00 1B F7` 和 Yamaha XG Normal Part `F0 43 10 4C 08 09 07 00 F7`，顺序为 GS→XG。编码器不得发送 GS Reset、XG System On/Reset、GM Reset，不得替换或补写 canonical Bank/Program；不相关事件 Track 与 Conductor 不写这些 SysEx。

这些消息采用厂商文档中的默认 Device ID / Device Number。接收方不识别 vendor SysEx 或使用不同设备编号时仍可能把 Channel 10 当鼓通道，Readme 必须说明该兼容边界。该选择依据 [Roland M-GS64 MIDI Implementation](https://cdn.roland.com/assets/media/pdf/M-GS64_OM.pdf) 的 `40 1x 15 USE FOR RHYTHM PART` 和 [Yamaha XG MIDI Data Format](https://uk.yamaha.com/en/download/files/2090960) 的 `08 nn 07 PART MODE`；外部资料用于确认 wire 定义，不替代 SRS。

## 13. ADR-CORE-011（已接受，23A/23.1A/23.2A）：共享输出命名边界与初版模板

决定：MIDI 导出和音频文件渲染不得各自实现不同的文件名策略。两个工作流共用一个确定性的 Windows 安全文件名合法化与冲突检测服务；它接收原始候选名称、扩展名预算和同一任务的候选集合，输出可预览、可诊断、可冻结的完整最终目标列表。任务开始后，编码器、渲染器和文件写入器不得再次解释或改变目标名称。

合法化只属于 Preparing / Review 输出规划，不修改 Project、Logical Track 或其他源名称，不进入 Undo / Redo，也不影响 canonical、MIDI 字节或音频样本。已有目标的覆盖授权仍由任务工作流在开始前一次性取得；合法化不能转化为静默覆盖权限。

Requirement trace：输入是源名称、导出模式、扩展名、父目录和同批候选集合；正式输出是合法化且内部唯一的冻结目标列表。边界包括 Windows 非法字符、保留设备名、尾部空格/句点、不可见字符、文件名部分长度、大小写和 Unicode 别名冲突。失败条件是公共算法无法形成唯一、合法、可表示的完整目标，或合法化后完整路径仍不可用；诊断归属文件系统/输出规划。持久化只允许保存 SRS 明确允许的有限命名偏好，不保存最终路径或合法化结果。明确非目标是修改源名称、基于父目录临时改变算法、运行中重命名、自动授权覆盖或把命名并入 canonical 内容。

23.1A 固定公共算法：候选 stem / 扩展名使用 NFC；Win32 保留字符、Unicode Control category 和 SRS 14.17.4 的固定不可见字符表按连续段替换为 `_`，同时保留 ZWNJ、ZWJ、Variation Selector 与 emoji tag；清除 stem 两端 ASCII 空格和尾部句点。Windows 设备保留名（包括 `CONIN$` / `CONOUT$` 以及 `COM¹` / `LPT¹` 等 superscript 形式）统一在 stem 前加 `_`。最终文件名部分最多 255 UTF-16 code unit，包含扩展名和后缀，并只在 .NET text-element 边界截断。

同一目录的冲突键是 NFC + `OrdinalIgnoreCase`。分配顺序由稳定源顺序和稳定源 key 固定，第一个无后缀，后续使用 ` (2)`、` (3)`……并重新预算；已有文件不参加后缀分配。公共实现 `Midora.OutputPlanning.WindowsOutputFileNamePlanner` 是纯 Preparing 组件，不读取文件系统；无合法 UTF-16、合法化后为空、扩展名契约错误、预算容不下一个完整文本元素或稳定 key 重复均原子失败。

23.2A 固定模板：整曲 MIDI / 音频分别为 `<ProjectStem>.mid` 与 `<ProjectStem>.wav`，ProjectStem 依 Project 名称、当前 `.midora` stem、模式固定 fallback 选择；分 Track 为 `<NN> - <LogicalTrackDisplayName>.mid/.wav`，NN 使用整个 Project 的一基手动顺序且至少两位；逐 Port MIDI 为 `Port <PP>.mid`；Readme 为 `README.md`。MIDI Conductor Track Name 固定 `Conductor`，事件 Track Name 固定 `<原始 Logical Track 名称或 fallback> / Port <P>`，不经过文件名合法化并由编码器严格 UTF-8 编码。多文件模式让用户选择完整输出目录，不自动增加嵌套目录。公共实现 `Midora.OutputPlanning.InitialReleaseOutputNaming` 只生成并合法化候选，不读取文件系统或推断覆盖权限。

## 14. ADR-CORE-012（已接受）：`.midora` v1 基础 Project 包垂直切片

决定：首个完整 package 切片只冻结当前已有领域能力可以无损重建的 JSON 边界，并贯通“内存 Project → 完整固定目录 ZIP → 严格自校验 → 同目录原子发布 → 释放句柄后重开”。本切片发布 `project.json`、`conductor-track.json`、`project-settings.json`、`export-settings.json`、`playback-settings.json`、`audio-render-settings.json`、`global-reset-defaults.json` 与 `global-event-scope-defaults.json` 的 schema v1；既有 `manifest.json`、`metadata.json` 与 `soundfont-settings.json` v1 保持不变。

Requirement trace：

- 输入：当前内存 `MidoraProject` 的 Metadata、TPQ、Conductor、Event Instrument Library 文件夹、Global Initial / Reset、Playback、Audio Render、无或 External SoundFont 设置，以及目标路径、软件版本和保存时间快照。
- 正式输出：包含全部第 16.2 节固定核心文件的确定性 Zip package；manifest 索引所有写出 entry 的 kind、schemaVersion 与未压缩内容 SHA-256。
- 边界：稳定 ID 全局唯一且小于 `nextStableId`；路径使用 `/`；JSON 严格 UTF-8 无 BOM、LF、固定字段顺序；Zip entry 顺序与时间戳固定。保存时间在事务建立不可变快照时冻结，作为本次普通保存或 Save Copy 的文件修改时间。
- 失败条件：未知/重复字段、路径或 kind 错乱、hash/schema 不一致、非法稳定 ID、非法设置组合、非空 Event Instrument/Logical Track 集合、Embedded SoundFont、序列化、自校验或发布失败均原子失败。本切片不得写出无法重开的 partial package。
- 诊断：package/container、manifest/index/hash、structure/schema、serialization/self-validation、publish/cleanup 分阶段；未知或未索引 entry 只产生打开 Info，保存时不保留。
- 持久化归属：只写 Project 源数据；canonical、缓存、诊断、Undo/Redo、Modified、设备、Mute/Solo、播放位置、任务和 UI 状态均不写入。
- 运行时归属：目标绝对路径、事务 ID、临时/备份路径、打开诊断和 External SoundFont 解析状态仅属于打开/保存会话。
- 明确非目标：本切片不发布 Event Instrument / Logical Track protobuf schema，不实现 Damaged Placeholder、旧版本迁移、Embedded SF2 复制或 WPF Modified/Undo 接线。非空对象集合和 Embedded SF2 必须显式拒绝，不能静默丢弃；后续垂直切片在发布对应 `.proto`、descriptor 与 golden bytes 后解除限制。

`project.json` v1 预留并严格定义 Event Instrument / Logical Track 索引项结构，但本切片只接受空索引；这使后续对象 `.pb` 切片无需重新解释 Project 身份、顺序、路径和名称快照。`export-settings.json` v1 仅确认固定顶层设置对象存在，不提前选择 SRS 尚未固定的默认导出模式；Audio Render 与 Playback 只保存 SRS 已固定的字段和默认值。Audio Render 的有限命名偏好按已确认的固定分 Track 模板记录为 `project-order-number-and-track-name`，不重新开放“是否包含 Track 序号”的可选分支。

后续状态：ADR-CORE-013 已解除非空 Event Instrument / Logical Track 限制；ADR-CORE-014 已解除完整性正常的 Embedded SF2 限制。ADR-CORE-012 的拒绝规则只描述基础切片当时的兼容边界，不再代表当前实现能力。

## 15. ADR-CORE-013（已接受）：`.midora` v1 重对象与损坏占位切片

决定：Event Instrument 与 Logical Track 使用独立 protobuf Edition 2024 schema v1，并以 `project.json` 的显式索引作为身份、顺序、名称快照与路径入口。对象内部保存完整源对象图、稳定 ID 和映射源码；manifest/object header/project index 三层类型与 schema 必须一致。正常对象进入领域集合，无法信任的单个对象形成独立损坏占位，不把半反序列化对象交给编译器。

Requirement trace：

- 输入：完整 Event Instrument、SubVoice、Template Event、Curve、Envelope、Logical Parameter/Mapping/C# Function、Logical Track、Segment、Logical Note 和 Parameter Lane 源对象图，以及 `project.json` 索引和对象 `.pb` 字节。
- 正式输出：稳定路径 `event-instruments/ei_<id>.pb`、`logical-tracks/lt_<id>.pb`；确定性 wire bytes；已提交 `.proto`、descriptor SHA-256 与代表性 golden bytes；打开后的正常对象或保留 ID/名称快照/路径/错误/原位置的损坏占位。
- 边界：所有嵌套稳定 ID 全局唯一且小于 `nextStableId`；集合和 map 按固定顺序编码；公共 ABI 字段号发布后不得复用；Event Instrument 文件夹归属只在 `project.json` 保存。语义上非法但结构可表示的音乐数据仍交给 Semantic Validation，不由持久化层冒充业务诊断。
- 失败条件：对象类型/schema/path/kind 错乱使整个 Project 打开失败；对象 entry 缺失、hash 不匹配、wire/UTF-8/必需字段损坏或内部 ID 与索引不一致形成损坏占位；含占位的 Project 禁止保存，直到用户删除占位。孤立对象只产生 Info，保存时移除。
- 诊断：结构兼容失败属于 package Structure；单对象损坏使用稳定 FileDamage 诊断；删除损坏 Event Instrument 会解除 Track 绑定但保留名称，删除损坏 Track 同步维护 Audio Render 显式选择。删除与 Undo token 原子恢复原位置和引用。
- 持久化归属：只保存源对象、显式顺序、稳定身份和引用；损坏占位、错误文本、Undo token、descriptor runtime 对象和反序列化缓存不写入包。
- 运行时归属：占位承载、删除/撤销 token 和打开诊断只属于当前会话；未来应用命令栈负责把 token 接入统一 Modified/Undo/Redo 工作流。
- 明确非目标：本切片不实现全局应用 Undo 栈、旧版本迁移、自动修复对象字节或保留未知 protobuf tag。

## 16. ADR-CORE-014（已接受，Q-NUI-001）：Embedded SF2 流式资源租约

决定：Project/Domain 继续只保存 Embedded SF2 的稳定 resource ID、原始文件名、SHA-256 和大小，不保存绝对路径或字节数组。Persistence 为打开会话建立可释放的运行时资源租约：导入时先把用户选择文件流式复制到会话临时快照并计算身份，全部成功后才原子设置 Project 引用；打开 package 时把合法资源流式解压、计算实际 hash/size，并返回随 `MidoraProjectOpenResultV1` 释放的绝对临时路径。Save/Save Copy 必须显式接收与当前 Project 引用匹配的可用租约，并在 staging 再次边复制边校验。

Requirement trace：

- 输入：用户选择的完全限定 SF2 路径，或 package 中 `resources/soundfonts/<resourceId>.sf2` entry；当前 Embedded 引用；manifest 记录；保存事务目标。
- 正式输出：manifest kind `embedded-resource` 且无结构 schemaVersion；settings hash、manifest hash、未压缩实际字节与大小一致的 package；打开会话可供后续 BASS 验证/加载的只读语义运行时路径和资源状态。
- 边界：复制、hash、解压和写包使用固定有界缓冲，不把整个 SF2 读入单个托管数组；资源稳定 ID 参加 Project 全局 ID 唯一性校验；租约路径与临时目录不进入 Domain、compiler fingerprint 或 package。取消、替换或清空引用后的新包只写当前引用资源。
- 失败条件：导入源缺失/不可读时 Project 与 `nextStableId` 不变；保存缺少匹配可用租约、租约文件被改写或 hash/size 不一致时事务在发布前原子失败；manifest/Zip entry 缺失、kind 错误、size/hash 不一致或解压失败时 Project 仍打开，但资源状态不可用且发声消费者必须被阻止。
- 诊断：Embedded 结构/内容损坏产生 Resource Error，但不设置 Project Modified；未引用 Embedded entry 产生 Info 并在下次合法保存移除。SF2/BASSMIDI 可加载性仍由音频 Preparing 阶段报告，不由 ZIP 完整性检查假装完成。
- 持久化归属：只有 Embedded 引用与合法资源原始字节属于 package；实际路径、临时目录、当前实际 hash、可用状态和租约所有权只属于会话。
- 运行时归属：调用方必须在 Project 关闭、替换/清空资源或打开结果不再使用时释放租约；保存自校验会释放其内部重开租约，包句柄在返回前全部关闭。
- 明确非目标：本切片不加载 BASSMIDI、不验证 SF2 内部格式、不实现文件监控缓存，也不保存未由用户明确接受的损坏 Embedded 资源表示。

Q-NUI-001 已确认：损坏、缺失或与当前 Project 引用错配的租约不能直接用于 Save/Save Copy。持久化层返回结构化 `MidoraEmbeddedSoundFontRepairRequiredExceptionV1`，列出且只列出 `ReplaceOrRebind`、`ClearReference` 两个修复动作；Preflight 或 staging 失败均不得发布文件。用户完成明确修复编辑后重新保存，输出才重新满足 settings、manifest、实际字节三方一致。

## 17. ADR-CORE-015（已接受）：`.midora` 版本预检与故障注入事务门

决定：打开流程在 manifest 索引和任何内容 hash 之前读取最小版本头。`fileFormatVersion`、`minimumReadableVersion` 或 `manifestSchemaVersion` 任一高于当前支持值时，以结构化 `MidoraPackageVersionCompatibilityExceptionV1` 在 `VersionPreflight` 阶段拒绝，不按损坏 v1 处理。低版本成功迁移所需的来源契约由 Q-NUI-002 决定；在决定前不得猜测 v0 字段、默认值或 protobuf wire 语义。

保存事务设置内部、确定性的故障注入缝，覆盖 Backup、Staging、SelfValidation、Publish 和 Cleanup；打开覆盖 Container 与 Manifest I/O。注入器只供测试与内部组合使用，不进入公共产品配置、Project、package 或诊断协议。发布前失败必须保留原目标并清理本事务产物；进入发布尝试后失败必须保留原目标、已验证临时包和备份供恢复；发布成功后的清理失败只能产生稳定 Warning，不得把已经发布的保存反转成失败。

Requirement trace：输入为现有目标、冻结保存快照、manifest 最小版本头及可重复的阶段故障；正式输出为原子发布的新包或带精确阶段/恢复路径的失败。边界是原目标字节和 Project `modifiedAtUtc` 只在发布成功后改变，临时包必须通过严格重开和逐内容相等校验。I/O、权限、格式、自检和发布异常不得越过阶段包装；清理异常不得遮蔽主要结果。绝对事务路径、注入状态和保留恢复文件只属于保存会话，不持久化。明确非目标是自动回滚一个已经成功的原子替换、自动采用未来格式、或在 Q-NUI-002 前伪造旧格式迁移。

## 18. ADR-CORE-016（已接受，Q-NUI-003 局部暂停）：MIDI Export 冻结任务与多文件事务

决定：正式任务先以专用 `CompilationPurpose.MidiExport` 和显式 Track 集合生成单一 canonical 快照；Whole Project、Per Logical Track 与 Per Port 只在该 canonical 之上组织文件。Per Track 按 Track owner 过滤但保留该 Track 的全部实际 Port；无音乐输出的有效 Track 仍生成 Conductor-only SMF。Per Port 只为有 canonical 事件的 Port 生成文件，文件内 MIDI Port Meta 固定归一化为 Port 1，Track Name/文件名/Readme 保留原始一基 Port。

文件名经公共合法化器形成绝对路径并冻结，同时冻结目标存在状态和一次性覆盖授权。所有 `.mid` 与被请求的 `README.md` 先写入同卷 staging 并完成 SMF Type 1 自校验；缺失目标目录以目录 rename 整体发布，已有目录逐文件原子替换/移动并保留事务备份，任一中途失败按逆序恢复。Finalizing 前允许取消并清理；Finalizing 短暂不可取消。回滚失败保留 staging/backup 路径，发布成功后的清理失败只产生 Warning。

Requirement trace：输入为冻结 Project/Track/范围/Routing/Warning 参数、canonical、原始名称、Project/file metadata、软件版本、输出目录和覆盖授权；正式输出为固定模板 SMF Type 1 文件及可选 `README.md`，或不含 partial 成功文件的失败/取消报告。边界包括同 tick canonical 顺序、统一 EOT、Channel 10 GS→XG、Per Port 文件级 Port 归一化、目标出现竞态和 Readme 同事务。编码、自校验、staging、publish、rollback、cleanup 均有独立阶段；任务状态、绝对路径、缓存、诊断和导出时间不进入 Project。明确非目标是读取播放 buffer/Mute/Solo、在导出器中重算语义、静默覆盖新出现目标或自动修改 Project Export Settings。

当前编译器的确定性 Channel Unit 分配本身从 Port 1/Channel 1 起使用最低空闲单元，因此 Compact 对当前 canonical 分配是同形映射；Preserve 保持该导出 CompileContext 的同一分配，二者均不由编码器重分配音乐事件。若未来 Project 引入可持久化显式路由，必须在编译上下文内实现并重新证明 Compact 等价，不能把语义分配下放给文件写入器。

Q-NUI-003 只暂停 Project `ExportProjectSettings` 正式字段、schema v2 和 v1→v2 设置迁移；一次性任务参数、三模式编码、Readme、输出规划和事务不依赖该默认值决定。

## 19. ADR-CORE-017（已接受）：Audio Render 冻结任务与逐文件事务

决定：音频文件渲染必须从专用 canonical 编译上下文开始。Whole Mix 使用一个 `AudioRender` 上下文；Per Logical Track 按 Project 手动顺序为每条已选且有效 Track 建立独立 `LogicalTrackAudioRender` 上下文。未选 Track 的内容和诊断不进入该上下文。默认自然范围先按各独立上下文的实际 Event Instrument Instance 输出、NoteOff、Release/Tail 与 Reset 求得，再冻结所有成功分轨共同使用的最大 `endTick` 并重新编译；显式范围直接冻结。空 SubVoice 合法、仍计入实例 Channel Unit 并产生 Info，因此已绑定但无 Note 输出的 Track 可生成同范围静音 WAV。

正式任务在启动前冻结：canonical 结果、8,000～192,000 Hz 整数采样率、离线每 Stream sample voice 上限、Playback Master Volume、Limiter v1、已验证 SF2 内容快照、公共命名服务生成的最终绝对路径、目标存在状态和覆盖授权。External SF2 被动 hash 变化不能修改 Project；只有调用方明确接受变化时本次快照可继续，并产生 Warning。Embedded SF2 通过当前有效资源租约建立任务私有快照。任务结束释放快照，不把它写回 `.midora`。

每个输出先在目标目录写唯一临时 WAV，完整渲染后严格校验 RIFF/WAVE、float32 stereo、采样率、frame 数和文件长度，再以移动或带备份替换原子发布。冻结时不存在而发布前新出现的路径不得覆盖。Whole Mix 任一失败使任务失败；Per Track 的编译、渲染、校验和发布彼此独立，失败后继续，已成功文件保留。取消清理当前临时文件、不开始后续 Track并保留已发布文件；清理失败必须报告残留路径，不能把残留物视为有效输出。

Requirement trace：输入是 Project 源数据、专用 CompileContext、有效 SF2 运行时身份、Project 音量/渲染设置和冻结路径授权；正式输出是 canonical 唯一派生的普通 RIFF/WAVE 及逐输出任务报告。边界是 `[startTick,endTick)`、同一分轨最终 sample 长度、Mute/Solo 不参与、公共输出命名和普通 RIFF 上限全任务预检。任务级失败包括无有效目标、零范围、无有效 SF2、公共 Worker 准备失败、路径规划失败或任一文件超过 RIFF 上限。绝对路径、canonical、sample-domain plan、进度、诊断、临时/备份文件和结果只属于运行时，不持久化、不进入 Undo/Redo。明确非目标是 UI、并发多渲染任务、按 Port 音频、RF64、编码格式/位深/声道选择、tail、断点续渲和任务历史；应用级“同一时间单个音频任务及开始渲染前自动 Stop”由 NUI-10 任务协调器统一实现。

## 20. ADR-CORE-018（已接受，Q-NUI-004 待确认）：单一应用任务与本机偏好边界

决定：主应用使用唯一 `ApplicationTaskCoordinator` 仲裁播放、预览、显式编译、Project 生命周期、保存、MIDI Export 和 Audio Render。任务只允许直接 admission 或拒绝，不建立命令队列；只有 SRS 明确列出的命令可以自动 Stop。任务持有与操作对应的 UI 锁级别，并通过可计数 lease 持有 `ProjectCompilationSession` 编辑锁，避免嵌套任务或播放清理错误地解除其他所有者的锁。

Project 切换顺序固定为播放清理、Function Draft 处理、未保存处理、实际切换。Draft Apply 在取得编辑锁前执行；后续未保存处理和实际切换在锁内执行。Save/Save Copy 的保存事务不接受取消。播放清理失败时，Save 类命令继续并保留错误；其他会改变 Project/产物的命令返回与原命令绑定的一次性 continuation，只有调用方明确继续后才执行。

Application Preferences 是与 `.midora` 独立的当前 Windows 用户本机状态。非 UI 切片保存正式音频偏好和五类 picker 最近目录；音频偏好只能在 Stopped 且应用空闲时提交，实际变化清除 sample-domain cache 并通知应用 composition 重建实时后端。读写失败回到 SRS 安全默认值并生成非 Project notice，不设置 Modified，不进入 Undo/Redo。

Requirement trace：输入为当前应用/播放状态、一个任务请求、Project 切换决策回调和已验证偏好；正式输出为结构化任务结果、阶段/锁投影及本机偏好快照。边界是单活动任务、无队列、明确 auto-Stop 白名单、一次性风险继续、Stopped-only 音频设置和原子偏好发布。异常、取消、播放 cleanup、偏好 I/O/格式/版本失败分别保留类别；所有路径释放自身 lease。任务状态、绝对输出路径、设备运行状态和偏好均不进入 Project/canonical；冻结的导出/渲染请求仍由既有 canonical 消费链执行。明确非目标是 WPF 表面、通知展示、任务历史持久化、多 Project/多任务并发、Preference sync/profile/import/export 和纯 UI 布局偏好。

Q-NUI-004 只涉及 SRS 未固定的本机表示：当前实现使用 `%LOCALAPPDATA%\Midora\preferences-v1.json`、source-generated UTF-8 JSON v1、1 MiB 读取上限和同目录原子替换。该选择不影响 `.midora`、可听语义或跨机器文件兼容；产品所有者若选择其他本机存储，可替换 store 而不改变协调器或偏好领域契约。

## 21. ADR-CORE-019（已接受，Q-NUI-005 局部暂停）：Project History 与编译事务

决定：初版使用每 Project 一个、跨编辑器统一的线性 History。正式 Project 编辑先只读 Prepare，再以 `Apply/Undo` 可逆动作和冻结 `ProjectChangeSet` 进入 `ProjectCompilationSession`；每次 Execute、Undo、Redo 都在同一 Project Edit Lock 边界内完成源变更和 Incremental Compile。语义错误可以形成不可消费 canonical 并进入 History；基础设施异常必须反向恢复源数据并 Full Compile 校验，不得留下“源已变但 History 未记录”的半事务。

Modified 不使用简单“Undo cursor 是否为零”。每个会话历史状态有不持久化的稳定 state ID，Save 成功把当前 ID 设为保存点；Undo/Redo 只有回到同一保存点才清除 Modified。Undo 后建立新分支会丢弃 redo entries，但不会让已经不可达的保存点与新分支错误等价。迁移/损坏回退等非普通命令变化以 external dirty reason 叠加，普通 Undo 不清除，成功 Save 才清除。

未保存新 Project 的初始构建不进入 History且可保持 `IsModified = false`，但因没有持久化来源，`NeedsSaveBeforeClose = true`。Save Copy 不调用保存点提交，不改变当前 Modified、History 或来源。History entry 在会话内保留准备好的反向数据；SRS 没有定义容量或合并策略，初版不设置会静默丢失旧 Undo 的固定条目上限，手势级合并由调用方形成单个 prepared command。

Requirement trace：输入为 Project、来源状态、可逆 command、保存成功和 external dirty reason；正式输出为全 Project History、操作名称、Modified/关闭保护和同步 canonical。边界是单线性分支、无操作不建历史、Project Edit Lock 排他和 command change-set 冻结。失败时恢复源并 Full Compile；rollback 再失败必须聚合报告。History/state ID/反向对象不持久化、不影响 canonical fingerprint；Project 源本身照常持久化。明确非目标是 Draft/文本本地 Undo、WPF focus routing、历史持久化、autosave/crash recovery，以及 Q-NUI-005 决定前所有会分配新稳定 ID 的 Undo 命令。

首批具体命令采用同一约束：Prepare 完成引用、名称、确认、时间范围、重叠和可恢复索引校验；Apply/Undo 复用原对象与原稳定 ID。Track/Instrument/Folder/Damaged Placeholder 删除、Track 绑定/排序、Library 组织和 Segment 移动/裁剪/删除/连接已经接入。Last Known Instrument Name 在显式绑定时更新为目标当前名称、显式取消绑定时保留最近可用名称、Instrument 重命名时同步更新当前绑定 Track 的快照；撤销恢复此前精确值。该快照只用于断裂提示，不参与按名称匹配或正式编译引用。

Conductor 更新使用“同稳定 ID 的不可变记录替换”，Undo 恢复原记录对象；tick 0 Tempo/Time Signature、同 tick 唯一性及 SMF Tempo 可表示性在 Prepare 阶段阻止非法输入。Playback 与 Audio Render Settings 以整组快照原子替换；它们进入 Project History/Modified，但使用空 compilation change-set，canonical 保持不变，下一次播放/渲染任务从正式 Project Settings 冻结实际参数。

第三批命令覆盖不分配 ID 的 Logical Note、Logical Parameter Lane/Point 与 Event Instrument Description/Color/Root Note。Note 编辑不把裁剪区当作数据合法边界；Point 正常编辑必须能从当前绑定定义验证值域和类型，断裂 Lane 只保留、删除或走显式重绑定修复。重绑定的 Clamp/Discard 由调用方每次明确选择，目标 Enum 还强制调用方确认整数兼容不代表语义兼容；转换后的点沿用原稳定 ID，Undo 恢复原对象图。Q-NUI-007 待确认期间，整数中点暂按现有 Mapping `Round` 一致的 AwayFromZero，目标 Enum 的保留点转为 Step；该局部选择不得扩散成持久化或编译器的新隐式默认。

第四批命令覆盖 Event Instrument Template Length、Isolation、Overlap、Lifecycle、Loop 与 SubVoice 基础结构。Template Length 的应用层下界由 Note end、瞬时事件/Curve Point 的半开边界和 Loop End 共同决定；Initial State 与 Envelope 时长不参与。关闭 Isolation 保留现存不兼容数据并允许 canonical 产生正式诊断，不能借编辑命令删除数据；受限制的 Loop 仍允许显式禁用。删除 SubVoice 拒绝最后一条、对非空内容要求确认，并把指向该 Voice 的 Logical Parameter Mapping 作为同一可逆事务删除/恢复。Compiler 另外对四类公开策略枚举增加定义域诊断，防止损坏或未来未知数值落入 switch 默认路径。

第五批命令覆盖既有 Template Event 的 Note、CC、Bank、Program、Pitch Bend、RPN、NRPN、Pitch Bend Range 属性及删除。编辑保持事件和三个 Mapping Chain/Target Settings 的对象身份；Note end 或瞬时事件 `tick + 1` 超过当前 Template Length 时原子延长。编辑到同 tick/同状态目标时保留当前被编辑事件并删除冲突旧对象，Undo 按原索引恢复；Pitch Bend Range 与 RPN 0 作为同一状态目标处理。Bank MSB/LSB 的存在性与数值分离，存在活动 Mapping Step 的组件不得被移除。Compiler 同时拒绝未定义 Template Event Kind 与 Curve Interpolation，避免损坏/未来枚举值越过验证。

第六批命令覆盖既有 Value Curve 的 Target Settings、Point 更新/删除和整条 Curve 删除。Point 仍以稳定 ID 定位，更新使用同 ID 的不可变记录替换，移动超过 Template Length 时按 `tick + 1` 半开边界原子延长；Target Overflow 为 Fail 时拒绝超值域基础点，为 Clamp 时允许保存并由 canonical 归一化。删除 Curve 只移除曲线对象，不触碰同目标离散事件。Compiler 修正首点前语义：事件曲线在第一个点之前不输出隐式 0，点集在本次编译准备期排序一次后复用，最后一点之后仍由 MIDI Channel 状态自然保持。
