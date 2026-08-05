# Midora 初版音频后端架构决策记录

状态：实现中（2026-08-05）  
适用范围：BASSMIDI 合成、正式音频链、WAVE 文件输出、BASSWASAPI 实时输出及内部音频子进程候选  
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
- 任一正式文件失败使整个任务失败；临时文件不得发布为最终结果。

### 1.5 所有权

- `.midora` 只持久化源数据，不保存本文所述 sample-domain 计划、BASS handle、音频 buffer、IPC 状态或输出结果。
- Preparing 创建并拥有渲染计划、SoundFont、stream、固定工作缓冲和输出事务；Playing / Buffering / Rendering 只消费；Stop / Finalizing 负责按反向顺序释放。
- 内部音频子进程只能接收已编译的帧事件和运行参数，不能打开或解释 Project，不能显示 UI。

### 1.6 明确非目标

- 后端不解释 Project、Event Instrument、Mapping、Lifecycle、Segment、tempo 或资源分配规则。
- 初版不支持 Reverb、Chorus、CC91、CC93、effect tail、Voice Stealing、传统 MIDI OUT、MIDI 2.0、多 SoundFont、SFZ/DLS、录音或用户可见的进程拓扑切换。

## 2. ADR-AUDIO-001：绝对 sample-frame 消费协议

决定：音频后端公共输入采用绝对 `Int64` sample-frame 位置和紧凑 MIDI 1.0 消息。Preparing 必须一次完成合法性校验，并冻结每个实际 Port 的连续事件数组。活动音频线程只维护数组索引。

渲染器在当前 frame 立即提交该 frame 的全部事件，然后仅通过 `BASS_ChannelGetData` 推进到下一事件边界；不使用 BASSMIDI 的定时预约、不用 `Thread.Sleep`、UI 定时器或调用时钟承担正式时序。一个调用块跨越事件时必须在边界处分块，因而结果不依赖输出 block 大小。

依据：该协议直接匹配未来 Canonical Compiled Result 的稳定 Port/event 序列，避免消费者复制或重新解释音乐模型，同时把 BASS 保持为渲染实现细节。

仍待决定：任意合法采样率下 tick / absolute-seconds 到整数 sample-frame 的统一舍入算法。该算法应位于 Compiler 的 sample-domain adapter，并在决定前不得由音频后端暗中实现。

## 3. ADR-AUDIO-002：初版 Limiter v1

决定：先实现版本号为 1 的 stereo-linked、sample-peak、零 look-ahead Limiter：

- ceiling：`1.0f`；
- attack：同一 sample 立即降低增益，确保有限输入的输出峰值不超过 ceiling；
- release：50 ms 指数恢复，系数由实际采样率计算；
- 左右声道使用同一增益；
- 状态跨工作 block 连续，Reset 时回到 unity gain；
- 算法、参数和版本进入渲染任务兼容性信息，但不进入 `.midora` 源数据。

理由：零 look-ahead 不引入起点预卷、范围末尾补偿或额外实时延迟，容易验证 block-size 不变性和精确总长度。代价是极端瞬态的失真可能高于 look-ahead 算法。

限制：这是本轮实现决策，不是 SRS 已规定的算法。听感、峰值、确定性和性能测试不通过时必须以新 ADR 升级，不能静默改变 v1。

## 4. ADR-AUDIO-003：BASSMIDI stream 策略

决定：

- BASS 以 no-sound device 初始化，只承担 decode stream 所需的全局环境。
- 只为实际使用的 Port 创建 stream，flags 固定包含 `BASS_SAMPLE_FLOAT | BASS_STREAM_DECODE | BASS_MIDI_NOFX`。
- stream 采样率直接等于本次实时设备实际采样率或文件目标采样率。
- 所有 Channel 的初始状态由统一例程显式建立，至少包含 Channel 10 melodic、Reset 后再应用的规范初始值和正式 SoundFont。
- SoundFont handle 在多个 Port stream 间共享，并晚于所有 stream 释放。
- 同一 frame 的 MIDI 消息紧凑打包后立即批量提交；提交和 `ChannelGetData` 都检查返回值并立即捕获当前线程 BASS error code。
- 活动阶段不执行 sample loading、路径转换或托管内存分配。

尚未决定，且本轮不得隐藏选择：`BASS_MIDI_NOTEOFF1`、interpolation、voice/CPU limiting、sample loading 参数及固定 BASS 修订。`NOTEOFF1` 仅在同音高重叠、Cut、Reset 和 NoteOff 配对测试证明与 Canonical 语义一致后才能启用。

## 5. ADR-AUDIO-004：WASAPI 候选与无锁缓冲

实现候选：shared、event-driven、stereo float32；请求采样率为 0，让设备选择 mix format；Device Buffer Request 使用用户的毫秒值。初始化后以 `BASS_WASAPI_GetInfo` 的实际 sample rate、buffer 和 format 为准，并据此重建所有 sample-domain 计划和 stream。

WASAPI callback 只从预分配的单生产者/单消费者连续 frame ring 复制。它不调用 BASSMIDI、不编译、不分配、不加锁、不等待、不做 I/O；异常由 native callback 边界完全截断。可消费 frame 不足时，本次 callback 整块输出静音且不推进音乐位置，进入 Buffering；重新达到启动阈值后继续。

状态：候选，不是初版最终模式。shared/exclusive、event-driven、格式协商、period 和内部工作 block 仍须 ADR 与可复现设备测试决定。

## 6. ADR-AUDIO-005：独立音频子进程候选

实现候选：主进程负责 Project、Compiler、Canonical Result、任务状态和 WASAPI；单个无 UI 子进程负责 BASS/BASSMIDI 合成。Preparing 通过有版本、长度和校验的二进制控制协议发送已编译的帧事件；Rendering 使用有界共享内存 SPSC float32 frame ring 和预创建事件协调。IPC Audio Buffer 决定 ring 容量。

子进程不得自行读取 Project 或重建音乐语义。序列号、格式、frame 位置或校验不一致均为任务 Error；子进程退出或无响应不得回退为另一套语义路径。

状态：这是为基准测试实现的候选拓扑。初版最终采用进程内还是子进程仍由正式 ADR 和可复现的稳定性、吞吐、故障恢复及端到端延迟测试决定；不向用户提供切换设置，也不承诺发布时维护两套正式模式。

## 7. 验证门

- 相同事件计划以不同工作 block（含非 2 次幂）渲染必须逐 sample 相同。
- 验证事件前静音、事件 frame 起音、真实 NoteOff velocity 0、同 tick 顺序、同音高重叠、Reset、硬结束和总 frame 数。
- 验证多 Port 求和、Channel 10 melodic、统一 SF2、NOFX 和 CC91/CC93 全路径拒绝。
- Limiter 验证峰值、左右联动、release 连续性、Reset 和 block-size 不变性。
- Rendering/Playing/Buffering 活动线程在预热后使用线程分配计数器验证零托管堆分配。
- WAVE 验证 8,000、44,100、48,000、192,000 和自定义采样率，以及 RIFF/fmt/fact/data 大小、frame 对齐、上限拒绝、取消和原子发布。
- WASAPI 验证短读、underrun/Buffering、设备移除、连续 start/stop、不同 callback block 和 callback 异常边界。
- 子进程验证协议版本、损坏输入、ring wrap、背压、进程退出、超时、吞吐和包含 IPC 的端到端延迟。
