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
| BASS 本地获取工具 | 21.3、发布约束 | 隐藏选择 win-x64、引用不存在的校验脚本、可无提示下载不固定的当前包 | 已要求显式架构和显式接受“未固定开发候选”，补充 manifest/SHA-256 校验；正式修订与发布 hash 仍待确认 |
| 正式音频进程拓扑 | 3.2.2、13.30、15.7.6、21.3 | 现有正式候选是“子进程合成、主进程 WASAPI”，会让 PCM 跨进程且主进程仍持有设备 callback；控制命名管道逐批分配数组 | 已按 12C 改为完整音频子进程：BASS/BASSMIDI/Limiter/Render-Ahead/BASSWASAPI/callback 全部由 Worker 持有；运行时命令/状态改为固定版本共享内存 ABI，实测热路径零分配；Worker 按显式 RID Native AOT 发布；旧进程内/PCM IPC 类降为仅测试程序集可见 |

## 3. 尚未完成的初版模块（不是“现有代码语义矛盾”）

- Project Metadata、Application Preferences、SoundFont 完整校验/可移植引用及正式应用生命周期尚未实现。
- Event Instrument、Mapping、Lifecycle、Logical Track/Segment 的若干编辑器级数据与命令仍只有核心垂直切片，不是 SRS 07–11 的完整实现。
- `.midora` ZIP/manifest/schema/迁移/安全保存/损坏隔离尚未实现。
- 正式 MIDI Export workflow、SMF Type 1 组织、文件事务和导出报告尚未实现。
- 正式 Audio Render workflow（整曲/分轨、任务快照、取消、结果报告和子进程内文件 OutputDevice）尚未实现；当前只有底层 renderer 与 WAVE 输出能力。
- WPF UI、导航、编辑器、对话框和 Project 打开/关闭工作流尚未实现。
- SRS 12.21 要求的 Segment checkpoint、输入/输出状态 hash、dirty 传播与收敛停止尚未实现；现有 Track 级缓存只能作为过渡实现，不能标记为初版合规。
- Canonical CompileContext 摘要、失败阶段、完整来源追踪和可选 Debug 诊断仍不完整。
- Native interop 尚未固定支持修订；32/64 位布局、calling convention、版本不匹配、重复 init/free 和泄漏的独立自动化门不完整。
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
12. **已确认：12C（2026-08-05）**。初版唯一正式拓扑为完整内部音频子进程：子进程独占 BASS、BASSMIDI、Limiter、Render-Ahead、BASSWASAPI、设备 callback 和文件专用 OutputDevice；主进程只负责 Project、Compiler、Canonical Result、UI 与任务协调。Worker 针对最终支持的每个 Windows RID 单独 Native AOT 发布；运行时命令/状态使用固定版本、固定布局、有界的二进制共享内存 ABI，不使用 JSON/文本反序列化，热路径不得产生托管堆分配。产品 CPU RID 集合仍由第 15 项确认。
13. **已确认：13A（2026-08-05）**。所有正式 BASSMIDI Stream 固定启用 `BASS_MIDI_NOTEOFF1`；同 Port、Channel、pitch 的重叠实例按最早开始者优先逐个释放。配置层已删除 Release All 分支，完整音频 Worker 与旧对照链共享该不可配置语义；真实 BASSMIDI 集成测试已覆盖同音高重叠、NoteOff velocity `0`、Cut Previous 释放与新实例重叠，以及硬边界成对 NoteOff 后的 Reset All Controllers。
14. **已确认：14A（2026-08-05，含后续补充）**。正式 Stream 固定 `BASS_ATTRIB_MIDI_SRC=1`（8-point sinc）和 `BASS_ATTRIB_MIDI_CPU=0`；Preparing 使用 `BASS_MIDI_FontLoad` 预加载冻结计划引用的 presets，缺失组合保持并预加载 BASS fallback。实时与离线 sample voice 上限分别配置，默认均为每 Stream `750`，同一任务所有 Port 使用同一冻结值；达到上限允许 BASS 固定 voice-limit 行为，完美音频一致性测试以未触顶为前提。代码已删除 interpolation/sample-loading/CPU 可选分支，修复实时事件 Stream 错用 `StreamLoadSamples`，并建立实时/离线独立配置传播与真实属性回读测试。Application Preferences、Audio Render Settings 的完整 UI/持久化仍随对应未完成模块实施。
15. 产品 CPU 架构：win-x64、win-x86 或其他明确发布集合；工具已不再暗设 x64。
16. BASS/BASSMIDI/BASSWASAPI 固定修订和每个发布文件的 SHA-256；不得把 vendor `latest` 当正式基线。
17. C# Mapping Function 的持久兼容 ABI：函数签名、允许引用、缓存和 AssemblyLoadContext 卸载策略；“初版不做 sandbox”已由 SRS 9.11.4 确认，不再询问。
18. `.midora` 首版 schema/protobuf 兼容基线：JSON Schema、protobuf 字段号、代码生成方式、文本最大长度和颜色/路径字段表示。
19. Project SoundFont 可移植引用与哈希策略：相对路径规则、大小写/分隔符、同名冲突、哈希算法和计算时机。
20. Project 工程总耗时累计规则：是否统计空闲、最小化、Buffering 等 SRS 明确留给实现的时段。
21. MIDI 导出兼容细节：Tempo 的 microseconds-per-quarter-note 舍入、running status、必要 Meta Event 兼容档和输出命名模板。
22. MIDI/音频输出文件名规则：Unicode normalization、最大长度、非法字符替换、冲突序号和分轨命名模板。

发布前另有一项非技术选择：必须由产品所有者确认并取得适用于实际产品、平台和分发方式的 BASS 商业许可证。技术测试通过不等于具备分发授权。
