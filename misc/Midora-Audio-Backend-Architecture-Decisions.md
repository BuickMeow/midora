# Midora 初版音频后端架构决策记录

状态：实现中（2026-08-05）  
适用范围：BASSMIDI 合成、正式音频链、WAVE 文件输出、BASSWASAPI 实时输出及内部音频子进程
上位规范：`Midora-SRS-Initial-Release-v0.1`。本文不是需求规范；若与 SRS 冲突，以 SRS 为准。

## 1. Requirement trace

### 1.1 输入

- 正式音乐语义只能来自 Canonical Compiled Result。
- 音频后端接收 Preparing 阶段冻结的、按实际 Port 分组的绝对 sample-frame MIDI 事件；同一 Port 内同 frame 的事件保持 Canonical Compiled Result 的稳定顺序。
- 输入同时包含精确总 frame 数、目标采样率、单个 Project SoundFont、Playback Master Volume 和版本化 Limiter 参数。
- tick / absolute-seconds 到整数 sample frame 的转换不属于 BASSMIDI、WASAPI 或 WAVE 输出端职责。

### 1.2 正式输出

- 每个实际使用的 Port 建立独立、stereo、float32、decode-only、`BASS_MIDI_NOFX` 的 BASSMIDI stream。
- 每个 Port 的输出先求和，再依次应用 Playback Master Volume 和 Limiter。
- 同一正式链输出到 BASSWASAPI 或普通 RIFF/WAVE（stereo、interleaved IEEE float32 little-endian）。

### 1.3 边界

- 最多 16 个实际 Port，每个 Port 16 个 melodic Channel；所有 Port 的 MIDI Channel 10 都显式设为 melodic。
- 所有缓冲和接口以 frame 为单位；格式中显式携带 sample rate、channel count 和 sample format。
- 实时采样率使用设备初始化后报告的实际采样率；文件采样率为 8,000–192,000 Hz 的任意整数。
- 渲染严格生成请求范围的精确 frame 数；范围结束无 effect tail。

### 1.4 失败条件与诊断

- Preparing 拒绝无效/缺失 SoundFont、非法 Port/Channel、负 frame、越界或乱序事件、CC91/CC93、非法采样率、非法总长度和不可由 RIFF 表示的目标。
- Rendering 将 BASS/BASSMIDI/BASSWASAPI 原生失败、短读、非有限样本、设备丢失、IPC 故障和文件写入失败记录为结构化故障码；活动音频线程只写入预分配状态，不构造异常、字符串或集合。
- Finalizing 在非音频线程把故障码转换为用户诊断，包含阶段、原生函数、原生错误码，以及可用时的 Port、frame 和目标路径。
- 整曲单输出失败使任务失败；分轨任务按 SRS 15.13 允许各文件独立成功或失败。失败输出的临时文件不得发布为最终结果。

### 1.5 所有权

- `.midora` 只持久化源数据，不保存本文所述 sample-domain 计划、BASS handle、音频 buffer、IPC 状态或输出结果。
- 主应用拥有 Project、Compiler、Canonical Compiled Result、UI、文件事务授权和任务协调；音频子进程拥有 SoundFont、BASS/BASSMIDI、Limiter、Render-Ahead、BASSWASAPI、设备 callback 和文件专用 OutputDevice。
- Preparing 创建并拥有渲染计划、SoundFont、stream、固定工作缓冲和输出事务；Playing / Buffering / Rendering 只消费；Stop / Finalizing 负责按反向顺序释放。
- 内部音频子进程只能接收已编译的帧事件和运行参数，不能打开或解释 Project，不能显示 UI。

### 1.6 明确非目标

- 后端不解释 Project、Event Instrument、Mapping、Lifecycle、Segment、tempo 或资源分配规则。
- 初版不支持 Reverb、Chorus、CC91、CC93、effect tail、语义级 Voice Stealing 策略、传统 MIDI OUT、MIDI 2.0、多 SoundFont、SFZ/DLS、录音或用户可见的进程拓扑切换。已确认的 BASSMIDI sample voice 资源上限不属于语义级 Voice Stealing。

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

## 4. ADR-AUDIO-003：BASSMIDI stream 策略

决定：

- BASS 以 no-sound device 初始化，只承担 decode stream 所需的全局环境。
- 只为实际使用的 Port 创建 stream，flags 固定包含 `BASS_SAMPLE_FLOAT | BASS_STREAM_DECODE | BASS_MIDI_NOFX | BASS_MIDI_NOTEOFF1`。
- stream 采样率直接等于本次实时设备实际采样率或文件目标采样率。
- 所有 Channel 的初始状态由统一例程显式建立，至少包含 Channel 10 melodic、Reset 后再应用的规范初始值和正式 SoundFont。
- SoundFont handle 在多个 Port stream 间共享，并晚于所有 stream 释放。
- 同一 frame 的 MIDI 消息紧凑打包后立即批量提交；提交和 `ChannelGetData` 都检查返回值并立即捕获当前线程 BASS error code。
- 活动阶段不执行 sample loading、路径转换或托管内存分配。
- `BASS_MIDI_NOTEOFF1` 固定启用；同 Port、Channel、pitch 的重叠 Note 实例由 velocity `0` 或普通 NoteOff 按最早开始者优先逐个释放。该语义已通过真实 BASSMIDI stream 的同音高重叠、Cut Previous 释放与新实例重叠、硬边界成对 NoteOff、Reset All Controllers 和 velocity `0` 集成测试。
- 插值固定为 `BASS_ATTRIB_MIDI_SRC = 1`（8-point sinc），不使用含义不够精确的旧式 `BASS_MIDI_SINCINTER` 开关。
- CPU 属性固定为 `0`。官方含义是 automatic；在 Midora 主动拉取的 decode Stream 拓扑中不由 BASS update thread 处理，因此不启用 CPU shedding。拓扑改变时必须重验。
- Preparing 从冻结计划提取实际 Note On 使用的 Bank MSB / Program，并对共享 SF2 调用 `BASS_MIDI_FontLoad`；缺失组合保持 BASS fallback，并预加载 fallback。不得对实时事件 Stream 调用 `BASS_MIDI_StreamLoadSamples`。
- 实时与离线分别配置每 Stream sample voice 上限，默认均为 `750`；同一任务所有 Port 使用同一冻结值。达到上限时允许 BASSMIDI 固定 voice-limit 行为；完美音频一致性测试必须未触顶。

固定原生修订由 ADR-AUDIO-006 定义；本节不得改回“同 API 主版本即可”的宽松策略。

## 5. ADR-AUDIO-004：WASAPI shared event-driven 与无锁缓冲

决定：shared、event-driven、stereo interleaved float32；请求采样率为 0，让设备选择 mix format，period 请求为 `0`；Device Buffer Request 使用用户的毫秒值。初始化后以 `BASS_WASAPI_GetInfo` 的实际 sample rate、buffer 和 format 为准，并据此重建所有 sample-domain 计划和 stream。不得静默回退到 exclusive、polling/push、整数 sample format、mono/多声道或另一采样率。

WASAPI callback 只从预分配的单生产者/单消费者连续 frame ring 复制。它不调用 BASSMIDI、不编译、不分配、不加锁、不等待、不做 I/O；异常由 native callback 边界完全截断。可消费 frame 不足时，本次 callback 整块输出静音且不推进音乐位置，进入 Buffering；重新达到启动阈值后继续。

`AudioPullResult.FrameCount` 表示实际消费并推进的音乐 frame，不是本次物理 callback 请求或输出的 frame 数。underrun/Buffering 固定返回 `FrameCount=0`，ring read position、WASAPI `ConsumedFrameCount` 与播放 tick 全部保持；物理设备请求的整个 buffer 仍填充静音并返回完整 byte length。任何 Buffering source 返回非零消费数都属于协议一致性故障，callback 清零整块并标记 fault。producer 完成后的合法短尾仍按实际消费数推进，其余部分静音。

`AudioPullStatus` 是闭合协议，只接受 Continue、Buffering、EndOfStream、Fault；未知值、负数、超请求帧数、正请求下的 Continue(0) 与 Buffering(nonzero) 都是协议故障。Render-Ahead producer 对合法 Buffering(0) 使用与 ring 背压相同的短等待后重试，不写 ring、不推进 frame；Fault 或非法结果立即原子标记 producer fault。WASAPI callback 对非法结果始终清零整个物理 buffer 并标记 callback fault，异常不得越过 native 边界。

正式实时合成与 Render-Ahead producer 最大工作 block 固定为 256 frames；事件边界和任务末尾允许短块。Render-Ahead ring 容量按 `ceil(actualSampleRate × RenderAheadMilliseconds / 1000)` 计算，不按固定 block 数配置。

## 6. ADR-AUDIO-005：完整独立音频子进程与 Native AOT

决定：主进程负责 Project、Compiler、Canonical Result、UI 与任务协调；单个无 UI 音频子进程独占 BASS、BASSMIDI、Limiter、子进程内 Render-Ahead PCM ring、BASSWASAPI、设备 callback 和文件专用 OutputDevice。实时 PCM 不跨进程传输，因而不再存在 IPC Audio Buffer 用户设置。

Preparing 通过固定版本的二进制计划格式传递冻结的 sample-domain 事件；运行时命令与状态使用固定版本、固定布局、有界的共享内存 ABI。协议不得使用 JSON、文本消息或逐消息对象反序列化。命令生产/消费和状态读写在 Playing、Buffering、Preview Playing 与 Rendering 热路径不得产生托管堆分配；队列满、版本不匹配、损坏字段或非法命令均为任务 Error，不允许丢弃后继续。

子进程不得自行读取 Project 或重建音乐语义。序列号、格式、frame 位置或校验不一致均为任务 Error；子进程退出或无响应不得回退为进程内或混合拓扑。

正式实时客户端与正式文件客户端都只接受现存的 `.exe` Worker 路径并按绝对路径启动；托管 `.dll` 仅能通过程序集内部的测试入口显式放行，任意其他扩展名始终拒绝。实时 Worker 退出必须同时满足 exit code 0 与共享状态 Stopped/Completed；非零退出、Faulted，或 exit code 0 但仍停留在 Preparing/Playing/Buffering 等非终态，均为任务错误。显式 Stop 也必须在等待进程后校验这两个信号，不能因进程已经退出而跳过失败报告。

冻结计划文件 MDAP v2 在写入任何 payload 前计算 source、disabled source、Port、event 与 SHA-256 的完整有界大小；读取时先用剩余 payload 长度验证计数，再分配对应数组。Port record 的 reserved byte/word 必须为零；即使攻击者重新计算出正确 SHA-256，非零保留位、不可能计数、截断、溢出、非法 Port/MIDI/来源与 trailing payload 仍统一作为 `InvalidDataException` 拒绝，不能进入 Worker 渲染阶段。

共享内存 ABI v1 的 command ring 读写位置必须满足 `0 <= read <= write` 且 `write - read <= 1024`，任何损坏都必须在取模和指针运算前失败。Stop/Monitoring 及其子类型是闭合集；source/Port/message/boolean、CC91/CC93 和每条 command 的 reserved 字段在写入前整批校验、读取后再次校验，批次失败不得发布前缀。状态枚举与全部非负计数同样在读取边界校验；映射长度、固定 header 和 reserved header 不匹配时 Open 整体失败。Dispose 后的所有状态/发布/命令入口只抛 `ObjectDisposedException`，不得解引用已释放映射。

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

## 10. 验证门

- 相同事件计划以不同工作 block（含非 2 次幂）渲染必须逐 sample 相同。
- 验证事件前静音、事件 frame 起音、真实 NoteOff velocity 0、同 tick 顺序、同音高重叠、Reset、硬结束和总 frame 数。
- 验证多 Port 求和、Channel 10 melodic、统一 SF2、NOFX 和 CC91/CC93 全路径拒绝。
- Limiter 验证峰值、左右联动、release 连续性、Reset 和 block-size 不变性。
- Rendering/Playing/Buffering 活动线程在预热后使用线程分配计数器验证零托管堆分配。
- WAVE 验证 8,000、44,100、48,000、192,000 和自定义采样率，以及 RIFF/fmt/fact/data 大小、frame 对齐、上限拒绝、取消和原子发布。
- WASAPI 验证短读、underrun/Buffering、设备移除、连续 start/stop、不同 callback block 和 callback 异常边界。
- 子进程验证 Native AOT 发布、协议版本、损坏输入、命令 ring wrap、背压、进程退出、超时、吞吐、运行时 IPC 零分配和包含 IPC 的端到端延迟。
- 原生基线验证正式 manifest schema、三个精确 hash、完整运行时版本、缺失/多余/篡改文件拒绝、开发候选隔离，以及正式发布目录确实包含被校验的 DLL 与 manifest。
