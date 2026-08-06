# Midora SRS—代码一致性核对与修正记录

状态：现有代码面的全量核对与直接矛盾修正已完成（2026-08-05）；本状态不表示初版功能已经全部实现或达到发布验收。
上位规范：`misc/Midora-SRS-Initial-Release-v0.1/`。本文件是核对、需求追踪和实现决策记录，不修改 SRS，也不替代 SRS。

核对方法：逐章读取 SRS 00–22，随后检查仓库内全部正式源码、项目文件、自动测试、控制台原型、原生互操作声明和当前 ADR。下文把“现有实现与 SRS 直接相反”与“该模块尚未实现”分开记录。

## 1. 核对范围与 Requirement Trace

- 输入：完整内存 Project、编译上下文、Canonical Compiled Result、播放/预览运行时状态、sample-domain 计划、BASS/BASSMIDI/BASSWASAPI 和 WAVE 输出配置。
- 正式输出：领域源数据；确定性的 Canonical Compiled Result；仅由 canonical 结果派生的播放、预览、MIDI 导出和音频渲染输入。
- 边界：统一使用 `[startTick, endTick)`；稳定 ID 表示身份；Port/Channel 只由编译器分配；实时采样率来自设备实际值，文件采样率为 8,000–192,000 Hz 整数。
- 失败条件：结构/引用/值域/资源/Mapping/范围错误使正式消费者不可消费；无 SF2 只阻止发声消费者；CC91/CC93 在进入 canonical 结果前拒绝。
- 诊断：保持 Error/Warning/Info/Debug 原级别；Warning-as-error 只影响成功判定；`Channel Unit >= 248` 固定为 Info。
- 持久化归属：`.midora` 只保存源数据和 Project 级稳定 ID 计数器；不得保存 Project ID、canonical 结果、缓存、Mute/Solo、播放位置或任务状态。
- 运行时归属：Mute/Solo、设备、buffer、播放状态、sample-domain 缓存和 native handle 均不属于 Project。
- 非目标：MIDI 2.0、传统 MIDI OUT、VST/DAW host、录音、语义级 Voice Stealing 策略、多 SoundFont、Pause/Scrub 和多 Project；已确认的 BASSMIDI sample voice 资源上限除外。

## 2. 已由 SRS 闭合、必须直接修正的差异

| 项目 | SRS 依据 | 核对时差异 | 已实施修正 |
| --- | --- | --- | --- |
| Project ID | 16.5.2 | `MidoraProject.Id`、source reference 和 canonical result 错误携带 Project ID | 已删除独立 Project ID 及其 fingerprint 输入 |
| 稳定 ID 分配 | 8.42、16.5.3、16.13.2 | 正式对象默认使用 `Guid.NewGuid()`，不符合 Project 级持久化单调计数器 | 已改为 Project 所有的 128-bit 单调计数器；零保留，不补缺、不复用；文件兼容布局已按决定 3A 固定 |
| Project End Marker 身份 | 4、16.8.2 | 只有 `long? EndMarkerTick`，缺少稳定 ID | 已建模为带稳定 ID 的可编辑事件，移动时保留身份，canonical conductor 保留该身份 |
| Event Instrument 文件夹层级 | 18.8.2–18.8.3 | `ParentFolderId` 允许嵌套 | 已删除父文件夹语义；加入单层名称、保留名、引用校验及删除后移入 Unfiled 的领域操作 |
| Marker 名称 | 4.8.2、18.7.4、20.8.4 | Validator 错误拒绝空名称，且同 tick 数量规则冲突 | 已允许空名称、重复名称和同 tick 多个普通 Marker；按稳定 ID 确定同 tick 顺序 |
| Logical Track 名称 | 3.7.3、11.2.2、20.8.4 | Validator 错误拒绝空名称/未 trim 名称 | 已允许为空和重复，名称不参与身份 |
| Template Note 边界 | 7.27、8.51 | 只校验 NoteOn 位于 Template，未校验 NoteOff 不越界 | 已把越界源数据诊断为 Error；编辑器未来应先扩展 Template Length |
| Bank Select 结构 | 8.55.2 | 事件强制同时生成 MSB/LSB，不能表达仅一部分存在 | 已分别保存存在性，只映射和输出实际存在的部分，并保持 MSB 先于 LSB |
| 编译用途 | 12.2、14.1.2、15 | `CompilationPurpose` 缺少 MIDI Export 与 Logical Track 音频渲染用途 | 已增加专用用途，避免把播放运行时状态混入成品消费者 |
| partial 语义 | 12.20、13.1 | Range/Preview 成功结果被错误标记为 partial | 已把成功 CompileContext 结果标记为完整可消费；当前失败路径不返回音乐数据，因此也不伪称含 partial 数据；消费者拒绝任何 partial/不可消费结果 |
| canonical 确定性 | 12.21.2、22 INV-002/003 | canonical statistics 含耗时、缓存命中和重编数量，Full/Incremental 形式不可能相同 | 已从 canonical 移除非确定性运行遥测，缓存/重编计数只通过编译器运行遥测暴露 |
| 实时采样率 | 13.14、15.5 | 8,000–192,000 Hz 文件限制被误用于实时设备实际采样率，且预览仍走文件适配器 | 文件适配器保留范围；主播放和全部实时预览均接受设备报告的任意正整数实际采样率 |
| 播放任务状态 | 13 | 零长度范围和 Preparing 异常后可能残留 Active Task | 已保证这些路径释放编辑锁并恢复 `ActiveTaskKind=None` |
| Channel 10 初始化顺序 | 13、音频后端约束 | stream 先设 melodic 再 Reset，Reset 可能覆盖规范初始值 | 已改为每个 Channel 先 Reset、再显式设置 melodic，包括 MIDI Channel 10 |
| 原生资源释放 | 13.19、15、21 | 部分 `StreamFree`/`FontFree`/全局 `Free`/WAVE `CloseHandle` 返回值被忽略，WASAPI 清理错误未被正式后端上报 | 已逐次检查返回值并立即读取线程本地错误码；WASAPI 先建立正确 device context 再清理；正式后端聚合并报告清理失败，finalizer 保持不抛异常 |
| 未决音频参数的“默认值” | 21.3、音频 ADR | 原型参数被命名为 Initial Release Default，且实时后端可隐式采用 | 原型集合已明确标为 Prototype；9A 已把 Limiter v1 定版，其他未确认的 BASSMIDI 参数仍要求调用方显式传入 |
| BASS 本地获取工具 | 13.30、21.3、发布约束 | 隐藏选择 win-x64、引用不存在的校验脚本、可无提示下载不固定的当前包 | 已固定 win-x64 正式 manifest、三项完整版本码与 SHA-256；正式安装/发布只接受操作员提供且匹配仓库 manifest 的 DLL，vendor current URL 仅生成显式未固定候选 |
| 正式音频进程拓扑 | 3.2.2、13.30、15.7.6、21.3 | 现有正式候选是“子进程合成、主进程 WASAPI”，会让 PCM 跨进程且主进程仍持有设备 callback；控制命名管道逐批分配数组 | 已按 12C 改为完整音频子进程：BASS/BASSMIDI/Limiter/Render-Ahead/BASSWASAPI/callback 全部由 Worker 持有；运行时命令/状态改为固定版本共享内存 ABI，实测热路径零分配；Worker 按显式 RID Native AOT 发布；旧进程内/PCM IPC 类降为仅测试程序集可见 |
| 音频 solution 的 Release 配置 | 构建与验证门 | `midora-audio.slnx -c Release` 未显式包含部分跨目录 ProjectReference，导致这些间接项目实际落到 Debug 输出，不能作为完整 Release 证据 | 已把 Mapping Contract、Domain、Compiler、Playback、Playback.BassWasapi 和 NativeInterops.BassWasapi 加入 solution；Release 构建必须确认全部列出的输出路径均为 `bin/Release` |
| C# Mapping 持久 ABI | 9.6、9.7、12.9、16.1.4/16.9.2 | 原型使用 `LanguageVersion.Latest`、运行机器全部 TPA、随机程序集名、默认 ALC 和无界进程缓存；`MappingContext` 直接暴露 Domain 类型 | 已按 17A 建立独立只读 ABI v1 契约，固定 Roslyn 5.3.0/C# 14/`Microsoft.NETCore.App.Ref 10.0.10` 和基于源码 hash 的身份；每 Project 只保留当前修订，每项使用 collectible ALC；领域源模型只携带 `abiVersion`/函数体/声明，编译产物不持久化 |
| 颜色兼容表示 | 16.13.8、18A | `MidoraColor` 原型使用 32-bit ARGB，允许形成未规定 alpha 语义 | 已改为三个 byte 分量的 opaque sRGB；protobuf 固定 `RgbColor` 1/2/3 字段，JSON 需要颜色时固定小写 `#rrggbb` |
| SoundFont 路径归属 | 6.4、6.7、16.7.6、19A | `MidoraProject.SoundFontPath` 直接保存运行机器绝对路径，外部/内嵌模式、资源 ID 和已接受内容身份均未建模 | 已移除 Project 绝对路径；领域层使用严格 External/Embedded source union，绝对有效路径只存在于播放会话；新增 soundfont-settings v1、流式 SHA-256、精确/唯一 ignore-case 解析和被动验证不改源引用测试 |
| 工程总耗时 | 3.6.4、13.19.8、14.21.3、15.16.2、16.6.4、20A | 领域层无 Project Metadata，播放会话也没有打开时间 owner；SRS 只固定 int64 毫秒表示，未固定空闲、最小化和 suspend 边界 | 已建立 Project Metadata、单 owner 单调会话计时、系统 suspend / closing 组合暂停和 metadata v1 codec/schema；自动累计不失效 canonical 或单独标记 Modified |
| SMF 编码兼容档 | 14.2、14.8–14.14、14.18.5、21A/22A | `StandardMidiFile` 为空，没有正式 canonical MIDI 消费者；Tempo、Time Signature、Running Status、Meta 最小集、Channel 10 初始化和额外末尾清理仍是实现空白 | 已建立 Type 1 writer/validator 和整曲 canonical 垂直切片，固定一次 AwayFromZero、cc/bb、Bank 顺序、严格 UTF-8、无 Running Status、最小 Meta、canonical 外零清理、统一 EOT，以及每个实际相关事件 Track 的 GS→XG Channel 10 Normal Part 初始化 |

## 3. 尚未完成的初版模块（不是“现有代码语义矛盾”）

- Project Metadata、metadata v1 schema/codec、20A 会话计时、非 UI Application Preferences、单应用任务协调、Project switch guard，以及不分配稳定 ID 的 Project History/Modified/Undo 基础已实现；Project Metadata 六字段/Track Color、External SoundFont、Track/Instrument/Folder/Damaged Placeholder、Segment、Conductor、Project Settings、Logical Note、Logical Parameter Lane/Point、Event Instrument Lifecycle、SubVoice、Template Event、Value Curve、Initial/Reset State、Envelope、Mapping Function、Mapping Chain/Step，以及不需要 Lane 迁移的 Logical Parameter Definition/Mapping/Target Settings 命令已经接入并用 Full Compile oracle 覆盖。ID 分配型 Undo 等待 Q-NUI-005；需要全 Project Lane 迁移的 Parameter 类型、Enum 结构和范围变更等待 Q-NUI-009。WPF composition 与电源/关闭事件接线仍未完成。SoundFont 的固定版本 BASSMIDI 格式/可加载性验证、External 可撤销 source/runtime 切换和内容竞态拒绝已实现；文件监控/验证缓存与 Embedded 选择 History 尚未实现，内嵌资源复制和损坏修复门已实现。
- Conductor 现有事件与 Playback/Audio Render Settings 的非 ID 分配可逆命令已接入；Compiler 现已在 canonical 语义验证阶段拒绝无法按固定取整规则表示为 24-bit SMF Set Tempo 的 BPM。Event Instrument、Mapping、Lifecycle、Logical Track/Segment 的若干嵌套属性命令仍只有核心垂直切片，不是 SRS 07–11 的完整实现。
- `.midora` 已实现基础 Project package 垂直切片：12 个 Draft 2020-12 JSON schema、source-generated 严格 codec、固定入口确定性 ZIP、manifest/hash/container 校验、普通结构文件默认恢复、必需文件严格失败、Project round-trip，以及 Save/Save Copy 的同目录备份—临时包—严格重开—原子发布与取消清理事务。当前只支持空 Event Instrument/Logical Track 对象图及无/External SF2；完整对象 protobuf、Damaged Placeholder、迁移、Embedded SF2 复制、磁盘/清理故障注入与 WPF 工作流仍未实现。
- MIDI Export 已有整曲 SMF Type 1 canonical 编码、自校验和 Channel 10 GS→XG melodic 初始化垂直切片；按 Track/Port 模式、Compact Routing、Readme、文件事务、取消/进度和完整导出报告尚未实现。
- 正式 Audio Render workflow（整曲/分轨、任务快照、取消、结果报告和子进程内文件 OutputDevice）尚未实现；当前只有底层 renderer 与 WAVE 输出能力。
- WPF UI、导航、编辑器、对话框和 Project 打开/关闭工作流尚未实现。
- 后续复核（2026-08-06）：SRS 12.21 的 Segment checkpoint、dirty range、state hash 与收敛停止已实现并替换 Track 整片段过渡缓存；全局资源分配和 canonical 后处理仍完整重算。剩余工作是继续扩充随机 Project 与 §7～§12 组合覆盖。
- Canonical CompileContext 摘要、失败阶段、完整来源追踪和可选 Debug 诊断仍不完整。
- Native interop 已固定三项支持修订并建立完整版本不匹配测试；32/64 位布局、calling convention、重复 init/free 和泄漏的独立自动化门仍不完整。
- WASAPI 设备移除/默认设备变化、不同 callback block、deadline/underrun 和约 200 ms 端到端基准仍需要在正式候选硬件矩阵上验收。

## 4. ADR-SRS-AUDIT-001（已接受）：Project 级稳定 ID

SRS 已确定的部分：初版领域对象 ID 至少 128-bit；Project 保存 `nextStableId` 单调计数器。分配返回当前值后递增；零值保留为“未分配”；删除不回退计数器；复制、拆分右侧、新建事件和编辑器新增对象都必须通过所属 Project 分配。

Project 本身没有独立稳定 ID。诊断来源和 canonical result 只携带 Project 内对象 ID 与 tick；编译 fingerprint 只依赖 Project 内容和 CompileContext，不引入运行时随机 Project 身份。

加载器未来必须从 `project.json` 恢复 `nextStableId` 并验证所有现存稳定 ID 非零、全局唯一且小于该计数器。加载损坏文件时不得通过扫描最大 ID、补缺或随机生成来“修复”。新对象图的 ID 分配属于编辑/创建阶段，不得由 Validator 或 Compiler 隐式修改源 Project。

文件兼容布局已按决定 3A 确认：JSON、对象文件名和 `nextStableId` 使用固定 32 位小写十六进制字符串，高 64-bit 在前、低 64-bit 在后；protobuf 使用 `StableId { fixed64 high = 1; fixed64 low = 2; }`，数值组合为 `(high << 64) | low`，wire 字节序服从 protobuf `fixed64` 标准。当前 `Guid` 只是在内存中承载该数值的实现细节，不构成 GUID 文件格式。

## 5. 必须由产品所有者确认、当前不得静默定版的项目

本节只登记 SRS 内部冲突或 SRS/ADR 明确留待决定的选择；SRS 已闭合的规则不列入。

按后续确认顺序登记；已确认项目保留原编号并标记，未标记项目均待确认：

1. **已确认：1A（2026-08-05）**。初版 Envelope Preset 固定为 SRS 10.11 的 ADSR-like 结构；SRS 18.6.5 已修订为服从第 10.11 节，不采用任意有序点/曲线段模型。现有领域模型与编译器实现符合该决定，无需修改代码。
2. **已确认：2A（2026-08-05）**。同一 tick 允许多个普通 Marker；名称可空、可重复，不按名称或 tick 去重，以稳定 ID 区分。SRS 4.8.2 与 18.7.4 已统一；编译器补充稳定 ID 次级排序，避免 canonical 结果依赖源列表顺序。
3. **已确认：3A（2026-08-05）**。JSON、对象文件名和 `nextStableId` 固定使用 32 位小写十六进制字符串；protobuf 固定使用 `StableId.high` / `StableId.low` 两个 `fixed64` 字段，字段号分别为 1 / 2，wire 字节序遵循 protobuf 标准。SRS 2.5、16.11.2、16.13.2 与 21.3 已同步。
4. **已确认：4A（2026-08-05）**。新建 Event Instrument 默认 `Reject`；策略范围内重叠产生 Error，结果不可消费。用户显式选择 `Warn` 时产生 Warning，默认仍可消费；启用“Warning 视为 Error”后结果不可消费，但诊断级别仍是 Warning。SRS 7.33、10.17 与 12.19 已统一。
5. **已确认：5A（2026-08-05）**。整数目标参数默认 `Round`，midpoint 固定为 Away From Zero；`Floor` / `Ceil` 保留为目标参数可选项。取整只在完整映射链最终输出时执行一次；取整和最终越界策略已从 Mapping Step 移到事件参数、曲线或 Logical Parameter Mapping 的目标配置。SRS 9.4 与 12.9 已统一。
6. **已确认：6A（2026-08-05）**。连续值源以有效范围内每个整数 tick 的最终目标值为参考语义；canonical 输出首次有效值，以及后续相对上次输出发生变化的最终整数值。允许跳跃求值、缓存等优化，但 canonical 事件、tick、值、来源和诊断必须与逐 tick 参考算法完全一致；不采用误差阈值或自适应近似采样。
7. **无需产品确认（SRS 已闭合）**。第 7.30 节只把决定延后到编译章节；第 12.7.3 节已明确最终完全无输出的实例不分配 Channel Group、不占用 Channel Unit。现有 `InstanceWithNoPossibleOutputConsumesNoChannelUnit` 测试已锁定该语义，代码无需修改。原审计记录误写为第 7.31 节，现已纠正。
8. **已确认：8A（2026-08-05）**。tick→sample frame 使用完整 Tempo Map 在 `[originTick, targetTick)` 上的 decimal 分段积分；总时长乘采样率后只执行一次 `AwayFromZero`。不得逐 Tempo 段取整，也不得先取整绝对 sample 位置再相减；实时播放、预览和音频渲染共用该语义。现有实现符合该决定。
9. **已确认：9A（2026-08-05）**。初版 Limiter 算法版本 1 固定为 stereo-linked sample-peak：瞬时 attack、zero-look-ahead、线性 ceiling `1.0`、50 ms 单极指数 release；按实际采样率计算系数，跨 block 保持 gain，新任务或 Reset 后恢复 `1.0`。实时播放、预览和强制启用 Limiter 的音频渲染共用该算法；不检测 true peak / inter-sample peak。现有 DSP 实现符合该决定。
10. **已确认：10A（2026-08-05）**。初版正式 WASAPI 输出固定为 Shared Mode、event-driven、stereo interleaved float32；采样率跟随端点初始化后的实际混音采样率。Device Buffer Request 只作为请求值，period 请求为 `0`，实际 buffer、callback period 与 callback frame 数由设备决定并只读报告。不得静默回退到 Exclusive、轮询 / push、整数 sample format、mono / 多声道或其他采样率。现有实现方向符合该决定，并已将初始化策略集中为可测试约束。
11. **已确认：11A（2026-08-05）**。初版正式实时合成与 Render-Ahead producer 的最大工作 block 固定为 `256 frames`，事件边界和任务末尾允许短块。音频子进程内部的 Render-Ahead 使用单个有界 SPSC PCM ring，容量按 `ceil(actualSampleRate × RenderAheadMilliseconds / 1000)` frames 计算，不存在固定 ring block 数；实时 PCM 不跨进程。
12. **已确认：12C（2026-08-05）**。初版唯一正式拓扑为完整内部音频子进程：子进程独占 BASS、BASSMIDI、Limiter、Render-Ahead、BASSWASAPI、设备 callback 和文件专用 OutputDevice；主进程只负责 Project、Compiler、Canonical Result、UI 与任务协调。Worker 以第 15 项确认的 `win-x64` RID 单独 Native AOT、自包含发布；运行时命令/状态使用固定版本、固定布局、有界的二进制共享内存 ABI，不使用 JSON/文本反序列化，热路径不得产生托管堆分配。
13. **已确认：13A（2026-08-05）**。所有正式 BASSMIDI Stream 固定启用 `BASS_MIDI_NOTEOFF1`；同 Port、Channel、pitch 的重叠实例按最早开始者优先逐个释放。配置层已删除 Release All 分支，完整音频 Worker 与旧对照链共享该不可配置语义；真实 BASSMIDI 集成测试已覆盖同音高重叠、NoteOff velocity `0`、Cut Previous 释放与新实例重叠，以及硬边界成对 NoteOff 后的 Reset All Controllers。
14. **已确认：14A（2026-08-05，含后续补充）**。正式 Stream 固定 `BASS_ATTRIB_MIDI_SRC=1`（8-point sinc）和 `BASS_ATTRIB_MIDI_CPU=0`；Preparing 使用 `BASS_MIDI_FontLoad` 预加载冻结计划引用的 presets，缺失组合保持并预加载 BASS fallback。实时与离线 sample voice 上限分别配置，默认均为每 Stream `750`，同一任务所有 Port 使用同一冻结值；达到上限允许 BASS 固定 voice-limit 行为，完美音频一致性测试以未触顶为前提。代码已删除 interpolation/sample-loading/CPU 可选分支，修复实时事件 Stream 错用 `StreamLoadSamples`，并建立实时/离线独立配置传播与真实属性回读测试。Audio Render Settings 的 v1 领域对象/schema/package 持久化已实现；Application Preferences 和完整 UI 仍随对应未完成模块实施。
15. **已确认：15A（2026-08-06）**。初版产品只发布 `win-x64`；主应用、Native AOT 音频 Worker 与 BASS/BASSMIDI/BASSWASAPI 必须同为 x64，不生成 x86、Arm64 或 AnyCPU 正式产物。Worker publish target 和 BASS 获取/校验脚本已拒绝其他 RID；x64 的 SSE2 基线满足 14A 固定的 8-point sinc 前提。
16. **已确认：16A（2026-08-06）**。初版固定 BASS `2.4.18.3 / 0x02041203`、BASSMIDI `2.4.16.0 / 0x02041000`、BASSWASAPI `2.4.4.1 / 0x02040401` 及三项 win-x64 DLL SHA-256。仓库只提交正式 manifest，不提交 DLL；正式安装和 Worker publish 只接受操作员提供且逐文件匹配 manifest 的二进制，运行时校验完整版本码。vendor current/latest 下载只能生成 `releaseBaseline=false` 的开发候选；升级必须显式更新基线并完成全回归。第三方许可发布门由 24A 进一步固定。
17. **已确认：17A（2026-08-06）**。初版固定 C# Mapping ABI v1：`double Transform(double value, in MappingContextV1 context)`，独立只读契约程序集，Roslyn 5.3.0、C# 14、`Microsoft.NETCore.App.Ref 10.0.10`，不开放 Midora 内部、WPF/WindowsDesktop 或第三方编译引用。缓存键为 ABI/compiler profile/函数体 UTF-8 SHA-256，每 Project 只保留当前修订且每项使用 collectible ALC；`.midora` 只保存 ABI 版本、函数体和 Context 声明。该引用边界不是 sandbox，SRS 9.6.2 的风险仍成立。
18. **已确认：18A + 18.1A（2026-08-06）**。结构性 JSON 固定 Draft 2020-12、内部版本化 `System.Text.Json` source-generated DTO，严格拒绝重复/未知属性；protobuf 固定 Edition 2024、Google.Protobuf 3.35.1、Grpc.Tools 2.83.0，`.proto`/descriptor 基线提交、生成 C# 仅在 `obj`，descriptor-aware 检查拒绝未知 tag。字段号、reserved、deterministic 输出与 golden bytes 构成兼容门，依赖升级必须评审。文本上限按 Unicode scalar 固定为 256/4,096/65,536/1,048,576，路径为最多 4,096 scalars 的 UTF-8 相对 `/` 路径且保留大小写/原 Unicode；颜色固定 opaque sRGB。`createdAtUtc`/`modifiedAtUtc` 固定 `yyyy-MM-ddTHH:mm:ss.fffffffZ`，`totalEditingTimeMilliseconds` 为非负 int64。当前已冻结 12 个基础结构性 JSON schema v1，并实现空对象图 Project package 的确定性 ZIP 读写与保存事务；Event Instrument/Logical Track 的完整 protobuf schema、descriptor/golden、迁移与受损对象隔离仍未发布。
19. **已确认：19A（2026-08-06）**。External SF2 只允许 Project 根目录或直属 `soundfonts/`，保存实际大小写/原 Unicode 的 `/` 相对路径；逐分量精确匹配优先，唯一 ignore-case 回退并 Warning，多个近似候选为歧义不可用，根目录与子目录同名文件按完整路径区分。SHA-256 对完整原始字节流式计算；路径/hash/size 只在用户明确选择、替换、重绑定或接受当前内容时更新，被动缺失/hash mismatch/fallback 不改 Project。绝对路径和验证缓存是运行时状态。代码已移除 `MidoraProject.SoundFontPath`，建立 External/Embedded union、soundfont-settings v1 codec/schema、解析/hash 服务和测试；无/External/Embedded SF2 均已进入 package 链，External 编辑已接入固定版本 BASSMIDI 可加载性验证和 source/runtime 同步 History。当前剩余项是文件监控/验证缓存以及受 Q-NUI-005 约束的 Embedded 选择 History。
20. **已确认：20A（2026-08-06）**。从 Project 成功新建 / 打开到开始关闭，以单调时钟累计完整打开会话；空闲、最小化、失焦、模态 UI、保存、编译、播放、预览、Buffering、MIDI 导出和音频渲染全阶段均计入。系统睡眠 / 休眠与关闭流程暂停，关闭取消后只恢复后续累计。自动累计不进入 Undo / Redo、不单独标记 Modified、不更新 metadata 修改时间、不影响 canonical。代码已建立 Project Metadata、单 owner 计时会话、组合 pause reason、metadata v1 codec/schema、保存快照合并和安全 package 保存事务；WPF 电源/关闭生命周期、Modified 与 Undo/Redo 接线仍未实现。
21. **已确认：21A（2026-08-06）**。初版 SMF Type 1 采用兼容优先固定档：Tempo 为十进制 `60,000,000 / BPM` 后一次 `AwayFromZero`，结果限 `1..0xFFFFFF`；Time Signature 固定 `cc=24`、`bb=8`；Bank 顺序 CC0→CC32→Program；事件 Track 只写 Track Name 与 MIDI Port，不写 Device/Program Name；文本 Meta 严格 UTF-8；每个 Channel Event 显式 status，不用 Running Status；不在 canonical 外追加 Channel 清理；所有 Track EOT 对齐统一 endTick。代码已实现低层 writer/validator、整曲 canonical adapter、结构自校验与 golden/一致性测试。Track 可见字符串和文件命名模板不由编码器隐藏决定。
22. **已确认：22A（2026-08-06）**。每个实际包含 Channel 10 canonical 事件的事件 Track 在相对 tick 0、MIDI Port Meta 后、canonical Channel Event 前固定各写一次 Roland GS Normal Part 与 Yamaha XG Normal Part SysEx，顺序 GS→XG，使用固定默认 Device ID / Device Number。不发送 GS Reset、XG System On/Reset 或 GM Reset，不改写 canonical Bank/Program；不相关 Track 与 Conductor 不写。接收方忽略 vendor SysEx 或设备编号不同时仍可能按鼓通道处理，Readme 必须说明。代码已锁定精确 payload、顺序、条件写入和多 Port 分布。
23. **已确认：23A（2026-08-06）**。纠正本审计旧记录：原 SRS 并未统一命名策略，14.17 要求 MIDI 保留非法字符并失败，15.10 则要求音频自动合法化。现已统一为 MIDI/音频共用确定性的 Windows 安全文件名合法化与冲突检测服务，任务开始前必须预览并冻结全部最终路径；合法化不回写 Project 源名称，已有目标仍需明确覆盖授权。现有源码尚无批量命名器，底层 WAV writer 只消费调用方冻结的目标路径，因此没有旧算法需要迁移。
23.1. **已确认：23.1A（2026-08-06）**。公共算法固定为 NFC；Win32 保留字符、Unicode Control category 和固定安全风险不可见字符连续段替换为 `_`，保留 ZWNJ/ZWJ/Variation Selector/emoji tag；设备保留名前缀 `_`；最终文件名部分限 255 UTF-16 code unit并按 text element 截断；同目录以 NFC + OrdinalIgnoreCase 检测冲突，按稳定源顺序/key 分配 ` (2)`、` (3)`。已有目标不参与后缀分配。代码已在 `Midora.Common` 实现纯公共规划器并覆盖精确字符表、设备名、Unicode、长度、冲突、乱序输入和原子失败测试。
23.2. **已确认：23.2A（2026-08-06）**。整曲 MIDI/音频固定为 `<ProjectStem>.mid/.wav`，来源依次为 Project 名称、当前 `.midora` stem 和模式 fallback；分 Track 固定为 `<NN> - <LogicalTrackDisplayName>.mid/.wav`，使用整个 Project 的一基手动序号且至少两位；逐 Port MIDI 为 `Port <PP>.mid`；Readme 为 `README.md`。MIDI Conductor Track Name 为 `Conductor`，事件 Track Name 为原始 Logical Track 名称或 fallback 加 ` / Port <P>`，不经过文件名合法化。多文件模式选择完整输出目录，不自动嵌套。代码已在 `Midora.Common` 实现纯模板规划组件，并覆盖来源 fallback、全项目序号、逐 Port、原始 Track Name、README、Unicode 和原子失败测试。
24. **已确认：24A（2026-08-06）**。Midora 初版是免费、开源、非商业软件。该定位不把 BASS/BASSMIDI/BASSWASAPI 纳入 Midora 的开源许可证，也不单独证明已满足 BASS 免费使用条件。正式分发前必须按实际发布主体、收入方式、平台、分发方式和届时有效条款完成核验并提供 notices；条件不明或商业化时必须先联系权利人确认或取得适用许可。仓库继续不提交 BASS DLL。
25. **已确认：25B（2026-08-06）**。Midora 自有源代码采用未经自定义修改的标准 MIT License。该许可证允许下游商业使用；“Midora 初版非商业”只描述项目自身发布定位，不得写成额外许可限制。BASS/BASSMIDI/BASSWASAPI、用户 SoundFont 和其他第三方材料不纳入 MIT 授权范围。
25.1. **已确认：25.1A（2026-08-06）**。根目录 `LICENSE` 使用标准 MIT 全文，版权署名固定为 `Copyright (c) 2026 Midora contributors`；不需要逐源码文件添加许可证头。

本文件记录 1A～25.1A 的既有确认基线；后续“全部非 UI”实施中新发现且仍开放的问题统一维护在 `misc/Midora-Non-UI-Decision-Question-Library.md`，不得再把本段旧时点结论解释为当前没有开放决定。尚未实现的初版模块和发布验收门继续按第 3 节及实施台账推进；一般实现细节不应重新包装为产品决定。
