# Midora 初版音频后端架构决策记录

状态：实现中（2026-08-05）  
适用范围：BASSMIDI 合成、正式音频链、WAVE 文件输出、BASSWASAPI 实时输出及内部音频子进程
上位规范：`Midora-SRS-Initial-Release-v0.1`。本文不是需求规范；若与 SRS 冲突，以 SRS 为准。

## 1. Requirement trace

### 1.1 输入

- 正式音乐语义只能来自 Canonical Compiled Result。
- 音频后端接收 Preparing 阶段冻结的、由成功 canonical Execution Projection 确定性派生的抽象 Unit/Root 绝对 sample-frame MIDI 事件与 Channel Mode descriptor；同一 Unit 内同 frame 的事件保持 canonical 稳定顺序。物理 Port/Channel 仍保留在 canonical 中，但不单独构成 PCM 身份。
- 输入同时包含精确总 frame 数、目标采样率、单个 Project SoundFont、Playback Master Volume 和版本化 Limiter 参数。
- tick / absolute-seconds 到整数 sample frame 的转换不属于 BASSMIDI、WASAPI 或 WAVE 输出端职责。

### 1.2 正式输出

- 每个抽象 Unit 以独立、stereo、float32、decode-only、`BASS_MIDI_NOFX` 的 1-channel BASSMIDI stream 语义渲染；实际 native stream 由有界可复用 pool 提供。
- Unit raw PCM 按稳定顺序求和，再依次应用 Playback Master Volume 和唯一的全局 Limiter。
- 同一正式链输出到 BASSWASAPI 或普通 RIFF/WAVE（stereo、interleaved IEEE float32 little-endian）。

### 1.3 边界

- Canonical 最多 16 个实际 Port、每 Port 16 Channels。Logical/Event Instrument Unit 的 Channel 10 为 melodic；Pure MIDI Root 使用显式 Melodic/Percussion mode。音频投影把任一 canonical Unit 归一化为 1-channel stream 的 channel 0，并显式建立 descriptor mode。
- 所有缓冲和接口以 frame 为单位；格式中显式携带 sample rate、channel count 和 sample format。
- 实时采样率使用设备初始化后报告的实际采样率；文件采样率为 8,000–192,000 Hz 的任意整数。
- 渲染严格生成请求范围的精确 frame 数；范围结束无 effect tail。

### 1.4 失败条件与诊断

- Preparing 拒绝无效/缺失 SoundFont、非法 Port/Channel/mode、负 frame、越界或乱序事件、Event Instrument 路径非法 CC91/CC93、非法采样率、非法总长度和不可由 RIFF 表示的目标。合法 Pure MIDI CC91/CC93 不失败，正式 NOFX synth 路径忽略其效果。
- Rendering 将 BASS/BASSMIDI/BASSWASAPI 原生失败、短读、非有限样本、设备丢失、IPC 故障和文件写入失败记录为结构化故障码；活动音频线程只写入预分配状态，不构造异常、字符串或集合。
- Finalizing 在非音频线程把故障码转换为用户诊断，包含阶段、原生函数、原生错误码，以及可用时的 Port、frame 和目标路径。
- 整曲单输出失败使任务失败；分轨任务按 SRS 15.13 允许各文件独立成功或失败。失败输出的临时文件不得发布为最终结果。

### 1.5 所有权

- `.midora` 只持久化源数据，不保存本文所述 sample-domain 计划、BASS handle、音频 buffer、session cache、IPC 状态或输出结果。cache root 与 reusable quota 只属于 Application Preferences。
- 主应用拥有 Project、Compiler、Canonical Compiled Result、UI、文件事务授权和任务协调；音频子进程拥有 SoundFont、BASS/BASSMIDI、Limiter、Render-Ahead、BASSWASAPI、设备 callback 和文件专用 OutputDevice。
- Preparing 创建并拥有渲染计划、SoundFont、stream、固定工作缓冲和输出事务；Playing / Buffering / Rendering 只消费；Stop / Finalizing 负责按反向顺序释放。
- 内部音频子进程只能接收已编译的帧事件和运行参数，不能打开或解释 Project，不能显示 UI。

### 1.6 明确非目标

- 后端不解释 Project、Event Instrument、Mapping、Lifecycle、Segment、tempo 或资源分配规则。
- 初版不提供 Reverb/Chorus 音频效果、effect tail、语义级 Voice Stealing 策略、传统 MIDI OUT、MIDI 2.0、多 SoundFont、SFZ/DLS、录音或用户可见的进程拓扑切换。Pure MIDI CC91/CC93 只保留 MIDI 文件语义；opaque imported SysEx/Meta 不送入 synth。已确认的 BASSMIDI sample voice 资源上限不属于语义级 Voice Stealing。

## 2. ADR-AUDIO-001：绝对 sample-frame 消费协议

决定：音频后端公共输入采用绝对 `Int64` sample-frame 位置和紧凑 MIDI 1.0 消息。Preparing 必须一次完成合法性校验，并冻结每个实际 Port 的连续事件数组。活动音频线程只维护数组索引。

渲染器在当前 frame 立即提交该 frame 的全部事件，然后仅通过 `BASS_ChannelGetData` 推进到下一事件边界；不使用 BASSMIDI 的定时预约、不用 `Thread.Sleep`、UI 定时器或调用时钟承担正式时序。一个调用块跨越事件时必须在边界处分块，因而结果不依赖输出 block 大小。

依据：该协议直接匹配未来 Canonical Compiled Result 的稳定 Port/event 序列，避免消费者复制或重新解释音乐模型，同时把 BASS 保持为渲染实现细节。

已决定：tick→sample frame 使用完整 Tempo Map 在 `[originTick, targetTick)` 上的 decimal 分段积分；总时长乘采样率后只执行一次 `AwayFromZero`。不得逐 Tempo 段取整，也不得先取整绝对 sample 位置再相减。算法位于 Compiler 的 sample-domain adapter，不由音频后端重新实现。

## 3. ADR-AUDIO-002：Limiter v1

决定：版本号为 1 的 stereo-linked、sample-peak、零 look-ahead Limiter：

- ceiling：`1.0f`；
- attack：同一 sample 立即降低增益，确保有限输入的输出峰值不超过 ceiling；
- release：50 ms 指数恢复，系数由实际采样率计算；
- 左右声道使用同一增益；
- 状态跨工作 block 连续，Reset 时回到 unity gain；
- 算法、参数和版本进入渲染任务兼容性信息，但不进入 `.midora` 源数据。

理由：零 look-ahead 不引入起点预卷、范围末尾补偿或额外实时延迟，容易验证 block-size 不变性和精确总长度。代价是极端瞬态的失真可能高于 look-ahead 算法。

限制：不检测 true peak / inter-sample peak。后续若升级算法必须增加版本并修订 SRS/ADR，不能静默改变 v1。

## 4. ADR-AUDIO-003：BASSMIDI Unit stream 与 pool 策略

决定：

- BASS 以 no-sound device 初始化，只承担 decode stream 所需的全局环境。
- 只有全局 canonical 成功后才派生抽象 Unit 音频投影；每个 Unit 以 1-channel stream 语义渲染，flags 固定包含 `BASS_SAMPLE_FLOAT | BASS_STREAM_DECODE | BASS_MIDI_NOFX | BASS_MIDI_NOTEOFF1`。
- Native stream 由有界 pool 按需创建和复用；不得为 Project 中每个 Unit 永久持有 stream。复用前必须完成精确 NoteOff、CC120、Reset、状态重建和当前 SF2/采样率/voice policy 复核。
- stream 采样率直接等于本次实时设备实际采样率或文件目标采样率。
- Unit stream 的 channel 0 由统一例程先清除旧 state/mode，再按 canonical descriptor 显式建立为 Melodic 或 Percussion，并应用 Reset 后的规范初始值和正式 SoundFont；物理 Channel 10 的 BASSMIDI 默认鼓语义不得替代 descriptor。
- SoundFont handle 在 pool stream 间共享，并晚于所有 stream 释放。
- 同一 frame 的 MIDI 消息紧凑打包后立即批量提交；提交和 `ChannelGetData` 都检查返回值并立即捕获当前线程 BASS error code。
- 活动阶段不执行 sample loading、路径转换或托管内存分配。
- `BASS_MIDI_NOTEOFF1` 固定启用；同一 canonical Unit、pitch 的重叠 Note 实例由 velocity `0` 或普通 NoteOff 按最早开始者优先逐个释放。物理 Port/Channel 到 Unit 的投影不得合并原本不同的 Unit。
- 插值固定为 `BASS_ATTRIB_MIDI_SRC = 1`（8-point sinc），不使用含义不够精确的旧式 `BASS_MIDI_SINCINTER` 开关。
- CPU 属性固定为 `0`。官方含义是 automatic；在 Midora 主动拉取的 decode Stream 拓扑中不由 BASS update thread 处理，因此不启用 CPU shedding。拓扑改变时必须重验。
- Preparing 从冻结计划提取实际 Note On 使用的 Bank MSB / Program，并对共享 SF2 调用 `BASS_MIDI_FontLoad`；缺失组合保持 BASS fallback，并预加载 fallback。不得对实时事件 Stream 调用 `BASS_MIDI_StreamLoadSamples`。
- 实时与离线分别配置 `Maximum Sample Voices per Unit Stream`，范围 `1..16,777,216`，默认均为 `500`；同一任务所有 Unit 使用同一冻结值。达到上限时允许 BASSMIDI 固定 voice-limit 行为；完美音频一致性测试必须未触顶。修改设置失效相应全部 PCM/cache generations 和 native stream，不失效 tick-domain canonical。

固定原生修订由 ADR-AUDIO-006 定义；本节不得改回“同 API 主版本即可”的宽松策略。

## 5. ADR-AUDIO-004：WASAPI shared event-driven 与无锁缓冲

决定：shared、event-driven、stereo interleaved float32；请求采样率为 0，让设备选择 mix format，period 请求为 `0`；Device Buffer Request 使用用户的毫秒值。初始化后以 `BASS_WASAPI_GetInfo` 的实际 sample rate、buffer 和 format 为准，并据此重建所有 sample-domain 计划和 stream。不得静默回退到 exclusive、polling/push、整数 sample format、mono/多声道或另一采样率。

WASAPI callback 只从预分配的单生产者/单消费者连续 frame ring 复制。它不调用 BASSMIDI、不编译、不分配、不加锁、不等待、不做 I/O；异常由 native callback 边界完全截断。可消费 frame 不足时，本次 callback 整块输出静音且不推进音乐位置，进入 Buffering；重新达到启动阈值后继续。

underrun 发生时在失败音乐位置 `F` 锁存；callback 持续静音且光标、音乐位置和 ring read position 均不推进。恢复区间由自然小节计算：`F` 在小节起点时取当前完整小节，位于小节中途时取当前剩余部分加下一个完整小节，随后以播放终点和从 `F` 起 16 个四分音符裁剪。只有整个连续恢复区间完成并原子可读后才恢复播放，不能“填约 200 ms、播约 200 ms”。

`AudioPullResult.FrameCount` 表示实际消费并推进的音乐 frame，不是本次物理 callback 请求或输出的 frame 数。underrun/Buffering 固定返回 `FrameCount=0`，ring read position、WASAPI `ConsumedFrameCount` 与播放 tick 全部保持；物理设备请求的整个 buffer 仍填充静音并返回完整 byte length。任何 Buffering source 返回非零消费数都属于协议一致性故障，callback 清零整块并标记 fault。producer 完成后的合法短尾仍按实际消费数推进，其余部分静音。

`AudioPullStatus` 是闭合协议，只接受 Continue、Buffering、EndOfStream、Fault；未知值、负数、超请求帧数、正请求下的 Continue(0) 与 Buffering(nonzero) 都是协议故障。Render-Ahead producer 对合法 Buffering(0) 使用与 ring 背压相同的短等待后重试，不写 ring、不推进 frame；Fault 或非法结果立即原子标记 producer fault。WASAPI callback 对非法结果始终清零整个物理 buffer 并标记 callback fault，异常不得越过 native 边界。

正式实时合成与 Render-Ahead producer 最大工作 block 固定为 256 frames；事件边界和任务末尾允许短块。Render-Ahead ring 容量按 `ceil(actualSampleRate × RenderAheadMilliseconds / 1000)` 计算，不按固定 block 数配置。

## 6. ADR-AUDIO-005：完整独立音频子进程与 Native AOT

决定：主进程负责 Project、Compiler、Canonical Result、UI 与任务协调；单个无 UI 音频子进程独占 BASS、BASSMIDI、Limiter、子进程内 Render-Ahead PCM ring、BASSWASAPI、设备 callback 和文件专用 OutputDevice。实时 PCM 不跨进程传输，因而不再存在 IPC Audio Buffer 用户设置。

Preparing 通过固定版本的二进制计划格式传递冻结的 sample-domain 事件；运行时命令与状态使用固定版本、固定布局、有界的共享内存 ABI。协议不得使用 JSON、文本消息或逐消息对象反序列化。命令生产/消费和状态读写在 Playing、Buffering、Preview Playing 与 Rendering 热路径不得产生托管堆分配；队列满、版本不匹配、损坏字段或非法命令均为任务 Error，不允许丢弃后继续。

子进程不得自行读取 Project 或重建音乐语义。序列号、格式、frame 位置或校验不一致均为任务 Error；子进程退出或无响应不得回退为进程内或混合拓扑。

正式实时客户端与正式文件客户端都只接受现存的 `.exe` Worker 路径并按绝对路径启动；托管 `.dll` 仅能通过程序集内部的测试入口显式放行，任意其他扩展名始终拒绝。实时 Worker 退出必须同时满足 exit code 0 与共享状态 Stopped/Completed；非零退出、Faulted，或 exit code 0 但仍停留在 Preparing/Playing/Buffering 等非终态，均为任务错误。显式 Stop 也必须在等待进程后校验这两个信号，不能因进程已经退出而跳过失败报告。

冻结计划文件 MDAP v5 在写入任何 payload 前计算 source、disabled source、Port、event、Unit fragment、Segment/cache binding 与 SHA-256 的完整有界大小；读取时先用剩余 payload 长度验证计数，再分配对应数组。source ID 使用正 `Int64` little-endian，旧 v4 与其他版本一律拒绝，不迁移单次任务临时文件。Port/fragment/Segment record 的 reserved 字段必须为零；即使攻击者重新计算出正确 SHA-256，非零保留位、不可能计数、截断、溢出、非法 Port/MIDI/来源、非法缓存 payload 范围与 trailing payload 仍统一作为 `InvalidDataException` 拒绝，不能进入 Worker 渲染阶段。

共享内存 ABI v4 的 command ring 读写位置必须满足 `0 <= read <= write` 且 `write - read <= 1024`，任何损坏都必须在取模和指针运算前失败。Stop/Monitoring/Held Preview/Buffering Recovery 及其子类型是闭合集；source/Port/message/boolean、路径与 mode 相符的 CC91/CC93 合法性、held plan generation、recovery end frame 和每条 command 的 reserved 字段在写入前整批校验、读取后再次校验，批次失败不得发布前缀。Event Instrument/SubVoice 命令仍拒绝 CC91/CC93；Pure MIDI 执行命令必须允许并按 `NOFX` 音频语义忽略其效果。状态枚举与全部非负计数同样在读取边界校验；映射长度、固定 header 和 reserved header 不匹配时 Open 整体失败。Dispose 后的所有状态/发布/命令入口只抛 `ObjectDisposedException`，不得解引用已释放映射。Worker 可以按原顺序合并连续 Monitoring records，并在一个稳定 producer frontier 只执行一次 cold start；Stop 终止整个会话，因此允许抢占并丢弃排在它之前、尚未形成可观察输出的 Monitoring/Buffering Recovery records。该调度不改变 ABI v4 的字节布局。

Monitoring cold start 建立干净 BASSMIDI Unit Stream 时，必须在回退事件游标前依次发送 `MIDI_EVENT_NOTESOFF`、`MIDI_EVENT_SOUNDOFF`、`MIDI_EVENT_RESET`，再按 canonical descriptor 发送 Melodic `MIDI_EVENT_DEFDRUMS(0)` 或正式 Percussion mode 初始化，并检查每次原生调用。原因是 `MIDI_EVENT_RESET` 只实现 CC121 Reset Controllers，不会释放 Rolling Preparation 已提前提交的未来按键，也不能替代 Root mode。该修复保持 ABI v4 布局及字段含义与 canonical、MDAP、缓存及持久化格式不变；新增 mode 字段/版本由 ADR-AUDIO-012 和 Pure MIDI 实施变更单独冻结。

主进程生成 Mute/Solo cleanup 与非 Note restore 命令时，以设备已消费 sample frame 映射当前 tick，不以可提前数秒的底层 Render-Ahead frame 解释当前音乐状态；Worker 收到命令后仍在自己的稳定 producer frontier 原子丢弃旧 prepared suffix 并冷启动。两者之间至多保守滞后一个有界 Render-Ahead 区间，不能超前读取尚未播放 Segment 的状态或补发范围前 NoteOn。

ABI v2 引入并由当前 ABI v4 保持：固定 header offset 68 是对齐 `Int32 statusSequence`，以单 Writer seqlock 发布整组状态。Writer 必须用 compare-exchange 将偶数序列变为奇数，发布全部字段后以 release 写入下一偶数；并发 Writer 或遗留奇数序列立即作为协议错误。Reader 只接受前后相同的偶数序列，最多无分配重试 1024 次，耗尽则报告 IPC 一致性错误；序列允许 two's-complement wrap。ABI v3 在 offset 88 增加 held preview plan generation；v4 保持 header 布局，在现有 16-byte command record 的 offset 4 通用 64-bit payload 上增加 `BufferingRecoveryPrepare(endFrame)`。Create/Open 只接受 v4，不提供 v1～v3 回退。状态发布与读取热路径、序列 wrap、并发压力和中断 Writer 均由自动门验证。

held Preview 在 ABI v3 引入、当前 ABI v4 保持 `HeldPreviewPause`、`HeldPreviewApplyPlan(generation)`、`HeldPreviewResume` 三条有界命令。Pause 只冻结 Render-Ahead producer，WASAPI 仍消费 ring 内 PCM；主进程把 checksum MDAP v5 计划写入会话私有目录后发布新 generation，Worker 只在 producer frontier 替换未渲染后缀并以状态 generation 确认，随后恢复 producer。计划文件和 generation 都是单次会话运行时状态；实时 PCM 仍不跨进程。

ABI v4 的 `BufferingRecoveryPrepare(endFrame)` 只在 ring 已锁存 Buffering 且已配置 transient recovery spool 时合法。主进程用统一自然小节映射计算 sample-domain `endFrame`；Worker 暂停 producer，在 spool 中完整生成并校验 `[F,endFrame)`，重置 ring 到 `F` 后连续回放该区间，再恢复正常 producer。命令 payload 不携带 tick、拍号或 Project 数据，Worker 不重新解释 Conductor。

实时 Worker 启动事务先验证并冻结现存 SF2、Worker 和原生目录的绝对路径，再依次取得私有计划目录、MDAP、共享控制区和子进程；任何一步失败都反向释放已经取得的资源并删除私有计划目录。子进程启动后立即并发排空 stdout/stderr，不能等到 `WaitForExit` 之后才读取而形成重定向管道背压死锁；Preparing 的 Faulted、探测完成和显式 Stop 均须在同一个有界期限内等待退出，逾期强制结束。Worker 内等待 producer frontier、Buffering recovery 完整区间或 recovery replay 预填充的循环必须协作检查 Stop，使正常 Stop 不依赖 renderer/cache source 先脱离 Buffering。监控线程自身的异常必须被截获并提升为任务故障，不能越过线程边界成为未处理异常或让父进程无限等待。

Worker 是独立的协议校验边界，不能只信任当前父进程会生成合法命令行。`probe/play/file-probe/file-render` 的文件与目录输入必须是现存的 fully-qualified 路径，文件渲染目标必须是 fully-qualified、父目录现存且尚未占用的新路径；协议布尔只接受精确 `0`/`1`。正式实时与文件模式只接受最大 256-frame 工作块、规定 buffer 值域和 Limiter v1 固定 ceiling/release；文件模式还强制 Limiter enabled 与 8,000～192,000 Hz。MDAP 解析与全部纯托管策略校验必须先于加载原生库，非法输入统一在 Preparing 失败并发布 Faulted。

主进程释放正式实时会话时，读取最终共享状态、采集 stderr/exit code 与释放进程/映射/计划目录是相互独立的清理步骤。状态读取因 ABI 损坏失败时仍必须执行会话释放；读取失败、原 Stop 失败与释放失败按发生顺序聚合报告，不能由后一个异常覆盖前一个，也不能因诊断采集失败泄漏资源。一次 `IsFaulted`/FaultDescription 判断只使用一个已读取的状态值，不在同一判断内重复轮询并混合多个时刻。

音频 Worker 固定以 `win-x64` Native AOT、自包含发布，正式运行不依赖 JIT；不生成或接受 x86、Arm64、AnyCPU Worker 作为初版正式产物。主应用、Worker 与 BASS/BASSMIDI/BASSWASAPI 必须全部为 x64。Native AOT 只消除 JIT 路径，不保证线程调度、原生库或设备行为确定，因此零分配、deadline、underrun、IPC 延迟和故障恢复门仍须独立验收。

状态：已接受并作为初版唯一正式拓扑。旧的进程内链和“子进程合成、主进程 WASAPI”链仅保留为开发期对照测试，不得成为产品回退路径。

## 7. ADR-AUDIO-006：BASS win-x64 固定原生基线

决定：初版固定 `bass.dll 2.4.18.3 / 0x02041203`、`bassmidi.dll 2.4.16.0 / 0x02041000`、`basswasapi.dll 2.4.4.1 / 0x02040401`，逐文件 SHA-256 以仓库 `src/midora-audio/bass-native-baseline.win-x64.json` 为唯一正式清单。

仓库不提交 DLL。正式发布由操作员提供官方二进制目录；发布目标先独立读取仓库 manifest 校验三个文件，再把 DLL 与 manifest 复制进 Worker 输出。运行时对三个 `GetVersion` 完整 32-bit 值做精确匹配。安装目录自带 manifest、机器 PATH、开发机缓存和供应商 current/latest URL 都不能替代仓库正式 manifest。

`Get-BassNative.ps1 -AcceptUnpinnedDevelopmentCandidate` 只产生标记为 `releaseBaseline=false` 的本地候选。候选即使 API 主版本兼容，也不能进入正式发布；只有其字节恰好匹配仓库正式 hash 时才可由正式校验路径接受。升级必须显式更新 ADR/SRS/manifest、完整版本常量和回归证据，不自动追随供应商更新。

本决定不授权重新分发 BASS。Midora 初版虽按 24A 定位为免费、开源、非商业软件，正式分发仍须核验发布主体、收入方式、平台、分发方式和届时有效的 BASS 条款，并随产物提供第三方 notices；条件不明或商业化时先联系权利人确认或取得适用许可。

## 8. ADR-AUDIO-007：离线文件 Worker 与双层原子发布边界

决定：正式音频文件渲染仍使用 ADR-AUDIO-005 的唯一 `win-x64` Native AOT Worker，不依赖 WASAPI 或物理设备。主进程只向 Worker 传递固定二进制 `MidiRenderPlan`、冻结 SF2、sample voice 上限、Master Volume、Limiter v1 和一个已授权的任务临时 WAV 路径；不传 Project，不跨进程传 PCM。正式客户端只接受 `.exe`，托管 `.dll` 启动入口只供测试。

公共 `file-probe` 在任务级 Preparing 验证原生基线、SF2 可加载性和固定输出链；每个 `file-render` 进程创建本文件实际 Port 的干净 BASSMIDI stream，按计划预加载实际 preset/fallback，以固定 256-frame 最大工作块执行“多 Port 求和 → Master Volume → Limiter v1”，直接流式写普通 RIFF/WAVE。Worker 内部先写其私有临时文件并最终化为主进程授权的任务临时路径；主进程随后独立校验 WAVE，再负责最终目标的移动/替换事务。Worker 不能直接获得最终覆盖权限。

共享内存状态协议只携带 Preparing/Rendering/Cancelling/Finalizing/Completed/Faulted、frame 进度、故障码和热路径分配计数；取消使用有界控制命令，不靠终止进程模拟正常 Stop。Preparing 超时可强制结束尚未建立正式输出的 Worker。正常取消等待资源安全释放，并扫描/清理 Worker 内层临时文件；任何无法删除的残留路径返回主任务诊断。

Worker 启动时先按绝对路径加载三项原生库，并为各 interop assembly 安装返回这些精确句柄的 DllImport resolver；不得让 AOT 发布目录与外部目录的同名 DLL 形成两个 BASS 全局实例。该选择已用真实 BASS/BASSMIDI 验证开发期托管 Worker 与正式 `win-x64` Native AOT `.exe`：两者均通过独立进程文件协议、非静音 WAVE、精确 frame/文件长度和 Rendering 零托管分配；AOT publish 同时通过固定 manifest/DLL 校验。剩余发布门是故障/取消压力、最终分发核验与人工试听。

## 9. ADR-AUDIO-008：WASAPI 设备枚举与 UTF-8 全局模式

决定：Windows 上任何 `BASS_WASAPI_GetDeviceInfo` 或 `BASS_Init` 之前，进程必须把 `BASS_CONFIG_UNICODE` 显式设为 enabled 并回读确认；独立 WASAPI probe 和 process-wide BASSMIDI runtime 都执行同一门。正式代码随后只按 UTF-8 解码 `BASS_WASAPI_DEVICEINFO.name/id`，不能用默认 ANSI 数据交给 UTF-8 解码器。配置读取的 `uint.MaxValue`、设置失败或回读仍为 disabled 均立即读取当前线程 BASS error 并在 Preparing 失败。

设备枚举只接受 `BASS_ERROR_DEVICE` 作为越过最后索引的正常终止；`BASS_ERROR_WASAPI` 或其他返回码表示枚举未完成，必须整体失败，不能返回部分设备列表。候选必须同时为 enabled、非 input、非 loopback、非 unplugged、非 disabled；没有 enabled/disabled/unplugged 任一状态的 not-present 端点因缺少 enabled 自动排除。设备 ID 必须非空且本次枚举内 Ordinal 唯一，mix sample rate 必须可表示为正 `Int32`。从列表到 Init 之间再次按 ID 查找时重新检查 eligibility；设备已移除/禁用必须失败并触发上层重新枚举，不能继续初始化旧条目。

`BASS_WASAPI_NOTIFY_DISABLED` 与 `BASS_WASAPI_NOTIFY_FAIL` 只有在通知 device index 等于本任务已冻结的输出设备时才标记 DeviceLost；其他端点变化只更新运行时观察标志。用户偏好为“系统默认”（未保存显式设备 ID）时，`BASS_WASAPI_NOTIFY_DEFOUTPUT` 表示当前选择已映射到另一物理端点，也必须使任务失败；显式选择固定设备时，仅默认端点变化不影响当前任务。正式 Worker 与进程内对照 backend 都必须把本设备 DeviceLost/默认映射变化与 callback/ring/renderer fault 一并提升为不可恢复实时任务错误，由播放控制器执行 Stop 类清理、失效 sample-domain 缓存并保留位置；不能等待一个已停止回调再次推进状态。

Requirement trace：输入为固定版本 BASS/BASSWASAPI、进程全局字符集配置和当前 endpoint 列表；正式输出为完整、UTF-8、只含当前可用输出端点的运行时快照，或带原生错误码的 Preparing 失败。边界是正常越界终止码与 WASAPI 不可用严格区分、设备选择按原始稳定 ID 做 Ordinal 匹配、实际采样率仍由 Init/GetInfo 冻结。配置、设备绝对 ID/名称、索引、默认标志和错误码只属于当前机器运行时，不写入 Project；Application Preference 仅持久化用户选择的设备 ID。明确非目标是列出输入/loopback/disabled/not-present 端点、ANSI fallback、返回部分列表、自动改选设备或把枚举 mix format 当作最终初始化结果。

## 10. ADR-AUDIO-009：五层 session 音频缓存与恢复存储

决定采用分层结构：canonical range cache、编译器内部 Logical Segment/Unit fragment 与 Pure MidiSegment/Root checkpoint cache、pre-Master/pre-Limiter Unit/Root PCM tile cache、post-sum/Master/Limiter playback span cache、精确 Render-Ahead ring。缓存只能位于 canonical 之后或编译器内部，不得成为新的正式语义来源。

相同 semantic revision、CompileContext、范围和完整渲染 key 的 exact replay，若 reusable entry 完整有效，则不得再次执行语义编译或 BASSMIDI 合成。Logical Segment/Unit PCM key 必须包含既有 fingerprint；Pure MIDI Root PCM key 必须包含 Root composite/start-state/mode fingerprint；二者均包含 SF2 hash、Tempo 投影、采样率/格式、固定 native 基线、voice policy 与 renderer version。playback span key 另包含 audible set、Master、Limiter、范围起点和 Limiter 状态。

缓存是 Project-open-session 范围的磁盘后备存储加有界 RAM hot set，不跨会话，不进入 `.midora`。已完成条目在 Project 打开期间不驱逐；Project 关闭时只删除由版本化 manifest 识别的本 session 目录。默认 root 为 `%LOCALAPPDATA%\Midora\AudioCache`，只接受可写本机绝对路径；reusable quota 默认 16 GiB，允许 0 到 `Int64.MaxValue` bytes。程序只能管理 root 下已知的 `session-*` 子目录，不得递归清空 root 或删除未知文件。

Project 音乐编辑只清除受影响的 sample-plan 派生索引，不重建当前 session cache store。Unit PCM 与 playback span 由完整内容 key 自然形成局部失效；旧 key 在本次 Project-open session 内保留到 Project 关闭，未变化 Segment/Unit 的完整条目继续可复用。只有 cache root 或 reusable quota 实际改变时才替换 session store；重复应用相同缓存偏好必须幂等，不能删除已完成条目。

reusable quota=0、配额满或普通写失败只产生 `AudioCacheRetentionDisabled` Warning，并停止新 reusable 写入；已完成条目继续可读，miss 现渲染。Buffering 的 transient recovery spool 不受 reusable quota 限制，使用后立即删除并单独报告当前/峰值占用。spool 不可用且预留 RAM 不足时在失败 tick 受控停止并报告 `AudioRecoveryStorageUnavailable`，不得退化为短块断续播放。

跨进程 recovery spool 使用显式所有权交接：主进程先创建、定长并计入 session transient 占用，启动 Worker 前关闭本进程文件句柄但继续持有逻辑租约；Worker 在整个活动任务内独占该路径的 memory mapping；Stop/失败清理后由主进程租约删除文件并扣减占用。不得让主进程保留打开句柄再要求 Worker 重新映射同一路径。命令协议同时携带等容量的 RAM fallback 上限，但只在 Worker 的磁盘 mapping 失败时于 Preparing 分配 unmanaged RAM；两种存储都失败才记录结构化 storage failure，并在实际 underrun 请求恢复时受控 Stop。

cache writer 位于专用 I/O 路径。WASAPI callback、BASSMIDI render/mix、ring 搬运热路径不做文件 I/O；完整 tile 写完、checksum/generation 验证通过后才原子发布。损坏条目隔离并重建，不能以半写或旧 generation PCM 命中。

初版 I/O hot-set 固定实现为：最终 playback span 使用一个 `16,384 frames` 顺序 SPSC ring；raw Unit 对本任务实际有 hit/miss 的 canonical Unit 分别使用 `16,384 frames` 读/写 ring。最大 256 Unit 时 raw 读/写 hot-set 各最多 32 MiB。每个 Unit 的 hit fragment PCM 在 staging 文件中建立连续虚拟 frame 流；Preparing 先填满首个有界读 hot-set，专用 AboveNormal I/O 线程随后按播放顺序跨 fragment 主动预读，且每轮先服务读取、后服务写入。fragment 的独立 header/文件偏移不能导致每个短 fragment 都进行一次 render-thread↔I/O-thread readiness 握手。

miss fragment 同样映射到每 Unit 连续虚拟写流；写 ring 达到 `4,096 frames`、fragment 结束或任务完成时刷盘。reusable capture 是机会性副作用：实时与离线正式渲染都不得因为 writer 初始化、切换、ring 满或普通写失败停在当前 frame；不能无等待入队时只使当前 capture generation 失效，正式 PCM 继续生成。只有已接受 hit 的读取尚未到达时才允许返回 `Buffering`；读取损坏或失败不得输出未验证 PCM。cache-hit 且未进入 monitoring cold-start fallback 的 fragment 不重复建立未被消费的 BASSMIDI 初始状态；一旦 monitoring 要求绕过 PCM，必须先显式恢复干净 stream 状态再合成。

一个实际 canonical Unit route 对应一个干净 1-channel BASSMIDI Stream；同 route 上时间不重叠的 Unit fragment 在精确 Reset 后顺序复用该 Stream。任务持有的 Stream 数等于 canonical 实际分配过的 route 数，也就是该任务的峰值并发 Unit 数，严格不超过 256；不会按 Project 历史 fragment 总数永久创建 Stream。任务结束统一释放，下一任务重新建立干净池，避免跨任务原生状态泄漏。

详细 requirement trace：`misc/Midora-Segment-Unit-Audio-Cache-Requirement-Trace.md`。

## 10.1 ADR-AUDIO-010：Segment 逻辑缓存、代际 Pack 与滚动预准备

产品所有者于 2026-08-11 决定以 Segment 作为可复用 pre-Master/pre-Limiter PCM 的逻辑失效单元。Segment 内部固定划分为 16,384-frame stereo float32 block；block 只承担流式传输、校验、索引和背压，不建立独立文件，也不把缓存身份重新细化为 Note/Instance/SubVoice。一个 Segment 的任意可听输入改变时产生新的完整 Segment generation；未改变 Segment 的 generation 继续命中。

物理存储使用 session-scoped append-only generational Pack。单代最大 2 GiB，完整记录通过 checksum 和索引事务发布；半写记录、未提交索引和旧 generation 不得命中。live cache 默认上限仍为 16 GiB。dead ratio 至少 35% 且 dead bytes 至少 256 MiB 时具备重整资格，只在 Stopped 或持续 idle 10 s 执行，并至少保留 4 GiB 临时 headroom。重整只复制当前 Project 状态引用的 live generations；过时代际回收属于 generation compaction，不是对当前代际做 LRU。

这一点取代 ADR-AUDIO-009 中“旧 key 一律保留到 Project 关闭”的实现决定。现行 SRS 13.19.11 的不驱逐文句需要产品所有者后续同步：当前代际在 Project-open session 内仍不做 LRU，但不再要求无限保留已被新 Project 状态取代的 generation。`.midora`、Undo/Redo、canonical fingerprint 和跨 session 行为均不改变。

实时准备改为滚动水位：Startup 2 s、Low 0.75 s、Resume 2 s、Target High 6 s。开头存在超大 Segment 时，只要求全部活动 Segment 的连续 PCM 达到启动水位，不等待该 Segment 完整结束；随后渲染、Pack writer 与设备消费并行。writer backlog 使用 `clamp(physicalMemory / 64, 128 MiB, 512 MiB)` 的有界 RAM block pool。磁盘持续落后时通过水位进入受控 Buffering，不再以静默失效 capture、下一次重复渲染作为常规背压策略。硬 I/O 失败仍禁用新 retention、保留可继续的现场合成并发布可见 Warning。

初始 producer 并发为 `min(4, max(1, logicalProcessorCount / 2))`，同时限制在启动基准测得 writer bandwidth 的 50% 预算内。group commit 在累计 8 MiB、经过 1 s 或 Segment 完成时触发。正式硬件需求为本地 SSD，最低顺序读 200 MiB/s、写 100 MiB/s；低于门槛必须在诊断中明确报告，不能伪装成音频语义失败。

Stop 立即停止设备输出并禁止开始未来渲染；已完整 block 可由 I/O worker 排空，未完成 block 可丢弃。Project 关闭仍删除本 session 的已知目录。自然小节 underrun recovery、Segment 硬边界、Mute/Solo 运行时过滤、Master/Limiter 顺序和短 Render-Ahead ring 的职责保持不变。

详细 trace：`misc/Midora-Segment-Pack-and-Rolling-Preparation-Requirement-Trace.md`。

2026-08-14 性能修正：精确 Segment 命中不再在 Worker 启动前从 Pack 全量复制到 transient staging。主进程批量更新 Segment generation 后，只发布本任务私有的有界只读 manifest；Worker 专用 cache I/O 线程在对应 Segment 实际进入准备窗口时按块读取并校验 Pack，音频热线程仍不执行文件 I/O。尚在后台 Pack writer 队列内的完整 Segment 通过有生命周期租约的原始 spool extent 直接读取，已发布 Pack 在当前播放租约结束前不得重整删除；cache miss 继续写预分配 sparse staging 并后台发布。超过 Target High 6 s 的完整 playback-span 不在启动前物化，交由按需 Segment Pack 命中或现场 miss 路径提供，因此远处 Segment 的数量与 PCM 大小不再决定播放启动等待。Pack 格式、cache key、`.midora` 和正式音频语义均不改变。

2026-08-14 启动修正：默认 `[0, effective Project end)` 播放复用当前完整 canonical 结果的事件、分配、诊断和 fingerprint，只生成 `Playback` consumer context，不能重复执行同一 revision 的语义编译。sample-domain 计划按 `(canonical fingerprint, start, end, actual sample rate)` 缓存在 Project-open session，并在 Project 打开及后台编译发布后由独立任务预热；投影本身不持有 session 编辑锁，过期 generation 不得发布。设备仍在正式启动前重新 Probe，若实际采样率改变则只接受对应 rate 的计划。canonical→sample 投影使用单次 source/Port/Unit 分组，不得按 16 Port、16 Channel 对完整极端事件流反复扫描；这只缩短 Preparing，不截断远处 canonical 事件，不改变滚动 PCM 的 Startup/High 水位、缓存 key、MDAP 或可听结果。

## 10.2 ADR-AUDIO-011：持久 Worker 响应发布代际与纯音高试听直接命令

决定：共享音频控制 ABI v7 在 header offset 104 增加单调 `PersistentResponseGeneration`，reserved 区从 offset 112 开始。Worker 必须先关闭临时响应写句柄并将完整 payload 原子发布为 `response-<generation>.maws`，随后才在 seqlock 状态快照中发布该 generation；Desktop 只在观察到相等 generation 后读取一次不可变 payload。这样删除了“看到文件名即尝试打开”造成的跨进程 writer/reader sharing violation，而不是用重试隐藏竞争。未来 generation、已发布但缺失 payload、非单调 generation 和协议版本不匹配全部受控失败。

首次 pure pitch audition 仍使用 generation 文件请求完成输出设备配置。输出已经配置后，pitch/value 更新和结束改用有界、固定 16-byte command record 的 `PitchAuditionUpdate` / `PitchAuditionEnd`，不得创建 response 文件或同步阻塞 WPF Pointer Move。直接命令仍按 FIFO 与后续 Probe/Playback 排序；Probe/Playback 销毁 audition output 后双方清除配置状态。ring 满、Worker 退出或原生命令失败是显式故障，不静默丢弃或重试。

Requirement trace：输入是持久 Worker 的 generation 请求、共享控制状态及纯音高试听手势；输出是不可变响应或有序瞬时试听命令。边界是正式播放/held Preview 继续消费 canonical，首次设备配置仍同步，热更新只在已配置的专用试听流上执行。故障诊断保留完整 Worker stderr/响应文本；所有 generation、响应门、试听活动标志和命令只属于运行时，不写入 Project、Undo/Redo、缓存或 `.midora`。明确非目标是改变 Event Instrument、Mapping、Program/Bank/CC、正式预览或 MIDI/音频导出语义。

SRS §13.30 仍写明 ABI v4；现行 v6 已由 ADR-MON-005 实施，本决定把当前 ABI 提升为 v7 并记录差异，不修改 SRS 原文。自动验证必须覆盖 v7-only open、响应 generation 初值/单调性/损坏字段拒绝、直接试听命令 round-trip、共享状态零分配，以及正式 Native AOT 环境下连续 pitch update/end 与响应文件竞争压力。

## 10.3 ADR-AUDIO-012（已接受）：Pure MIDI Root 单流合成与 Root PCM cache

决定：同一 MIDI Channel Root 的全部 Pure MIDI Tracks 必须先在 canonical Execution Projection 中按 `tick → Track order → event order` 合并，再进入一个抽象 1-channel BASSMIDI stream；不得逐 Track 独立合成后求和。stream mode 服从 Root Melodic/Percussion descriptor。Pure MIDI 音频缓存使用 normalized MidiSegment fragment → Root merged checkpoint → Root raw PCM generation；编辑只 dirty 所属 Root 的最早 causal tick，完整 state/active Note/allocation/suffix dependency 收敛后可复用旧后缀，其他 Root 与 Logical Unit 不连带失效。

合法 Pure MIDI CC91/CC93 保留在 canonical/SMF 投影，但 `BASS_MIDI_NOFX` 音频执行不解释其效果；opaque imported SysEx/Meta 不送入 synth。播放中 child Track Mute/Solo 通过来源追踪在稳定 producer frontier 精确关闭该来源 Note 并重建未来 Root suffix，不允许向整个 Root 发送 CC120、卡在 Playing 或保留旧 prepared suffix。

Requirement trace：输入为 Root Execution Projection、Channel Mode、Root lifecycle/checkpoint、SF2/Tempo/sample/native/voice profile 与 audible source set；正式输出为一个 Root PCM 流或结构化失败。Root mode/source/cache descriptor 属于 canonical/运行时；Root 源字段属于 Project；PCM/checkpoint 不进 `.midora`。详细领域和导出决定见 `misc/Midora-Pure-MIDI-Tracks-and-SMF-Import-Architecture-Decisions.md`。

## 11. 验证门

- 相同事件计划以不同工作 block（含非 2 次幂）渲染必须逐 sample 相同。
- 验证事件前静音、事件 frame 起音、真实 NoteOff velocity 0、同 tick 顺序、同音高重叠、Reset、硬结束和总 frame 数。
- 验证 Unit/Root 拆分、稳定求和、Logical Channel 10 melodic、Pure Root Melodic/Percussion、统一 SF2、NOFX、SubVoice CC91/CC93 拒绝与 Pure MIDI CC91/CC93 音频忽略。
- 固定 win-x64 ABI 快照必须验证正式使用的 BASS/BASSMIDI/BASSWASAPI C 结构大小与字段偏移、pointer/function-pointer/handle 宽度、精确 LibraryImport DLL/entry point、BOOL/handle 返回宽度和 Windows x64 统一默认调用 ABI；不能只靠“真实调用没有崩溃”推断声明正确。
- Limiter 验证峰值、左右联动、release 连续性、Reset 和 block-size 不变性。
- Rendering/Playing/Buffering 活动线程在预热后使用线程分配计数器验证零托管堆分配。
- WAVE 验证 8,000、44,100、48,000、192,000 和自定义采样率，以及 RIFF/fmt/fact/data 大小、frame 对齐、上限拒绝、取消和原子发布。
- WASAPI 验证短读、underrun/Buffering、设备移除、连续 start/stop、不同 callback block 和 callback 异常边界。
- 缓存验证 exact replay 零重复编译/合成、Logical Segment 与 Pure Root 局部失效、Root checkpoint 收敛、同 Root 不做 per-Track synth、quota=0/满/写失败、损坏隔离、spool 失败、Project 关闭清理、未知文件保留和设备同格式 raw PCM 复用。
- 子进程验证 Native AOT 发布、协议版本、损坏输入、命令 ring wrap、背压、进程退出、超时、吞吐、运行时 IPC 零分配和包含 IPC 的端到端延迟。
- 原生基线验证正式 manifest schema、三个精确 hash、完整运行时版本、缺失/多余/篡改文件拒绝、开发候选隔离，以及正式发布目录确实包含被校验的 DLL 与 manifest。
