# Midora Segment/Unit 音频缓存与 Buffering 恢复 Requirement Trace

状态：Q-NUI-034～Q-NUI-042 已确认；canonical range cache、Unit 投影/键、session cache store、Preferences、MDAP v4、正式 Worker/离线 Worker 的 Unit PCM 命中发布、post-Limiter playback span、整段 Buffering 恢复、RAM fallback 及有界缓存 I/O hot-set 已接入；八组真实固定 BASS/SF2 的 PCM 等价与极端性能门已通过
日期：2026-08-08

## 1. 输入

- 当前 Project semantic revision、编译用途、冻结的 `[startTick, endTick)`、Track/Preview 选择与 held Gate 参数。
- 成功的 Canonical Compiled Result；物理 Port/Channel 分配仍由全局编译器决定并保留在 canonical 中。
- canonical 事件的 Track、Segment、Instance、SubVoice 来源与确定性顺序。
- Segment 源 fingerprint、最早可证明 causal dirty tick；无法证明安全前缀时回退到 Segment 有效起点。
- Project SF2 完整 SHA-256、BASS/BASSMIDI 固定基线、采样率/格式、NOFX、NOTEOFF1、插值、CPU 与 Maximum Sample Voices per Unit Stream。
- Tempo 局部投影、播放冷启动起点、Mute/Solo、Playback Master Volume、Limiter 版本和初始状态。
- Application Preferences 中的 cache root、reusable byte quota；运行时可写性与 session generation。

## 2. 正式输出

- 与 Full/Incremental Compile 完全等价的 canonical range 结果；缓存命中不得改变诊断、资源分配或 fingerprint。
- 只在 canonical 成功后确定性派生的 Segment/抽象 Unit 音频投影；物理 Port/Channel 可从 PCM key 中消去，但不得绕过 canonical。
- pre-Master、pre-Limiter 的 stereo float32 Segment/Unit PCM tiles。
- 对冻结 audible set 做确定性求和后，统一经过 Master 和一个全局 Limiter 的 playback spans。
- 精确容量的短 Render-Ahead SPSC ring；ring 只面向设备，不承担长期缓存或完整小节恢复存储。

## 3. 缓存层与复用保证

1. Project revision / canonical range cache。
2. 编译器内部 Segment/Unit fragment cache。
3. Segment/Unit raw PCM tile cache。
4. Playback span cache。
5. Render-Ahead ring。

相同 Project revision、相同范围、相同完整 key 的 exact replay 命中时，不再次执行语义编译或 BASSMIDI 合成。跨范围切片只有在 cold-start 上下文等价时允许；否则建立新的 range-start generation。

缓存不进入 `.midora`、Undo/Redo、Modified 或 canonical fingerprint。初版只在 Project 打开 session 内有效，不跨会话；Project 关闭时删除该 session 的已知条目。已完成条目在 Project 打开期间不做 LRU 驱逐。

## 4. Buffering 恢复边界

underrun 在失败音乐位置 `F` 锁存：播放光标、音乐位置与 ring read position 均不推进，callback 输出静音。

恢复终点 `R`：

- `F` 位于自然小节起点：目标为当前完整自然小节；
- `F` 位于自然小节中途：目标为当前小节剩余部分加下一个完整自然小节；
- 最终以播放终点和从 `F` 起 16 个四分音符裁剪；16 四分音符上限优先；
- 只有完整 `[F, R)` playback span 原子完成并可连续读取后才恢复播放。

Time Signature 中途变化造成的新自然小节边界服从 ProjectTimeSignatureMap；恢复计算不得自行另建小节算法。

## 5. Stream 与混音边界

- 正式 canonical 的物理 Port/Channel 路由不变，继续承担 256 Unit 上限、资源不足诊断与 MIDI 导出。
- 音频渲染把抽象 Unit 投影为 1-channel BASSMIDI stream 语义；由有界、可复用的干净 stream pool 执行，不为 Project 每个 Unit 永久持有 stream。
- realtime/offline voice 设置相互独立，均命名为 `Maximum Sample Voices per Unit Stream`，范围 1～16,777,216，默认 500。
- 修改任一 voice 设置只失效相应全部音频 PCM/cache generations 与 native stream，不失效 tick-domain canonical 编译结果。
- 所有 Unit raw PCM 按稳定 key 顺序求和；Master 和 Limiter 只在总和之后执行一次。

## 6. 存储、配额与失败

- 默认 cache root：`%LOCALAPPDATA%\Midora\AudioCache`；只接受可写的本机 fully-qualified 路径，拒绝相对路径、UNC 和网络位置。
- 默认 reusable quota：16 GiB；范围 `0..Int64.MaxValue` bytes。0 禁用 reusable retention，表示每次实时重渲染。
- 程序只管理 root 下由当前格式 manifest 标识的 `session-*` 子目录；不得递归删除 root 或未知文件。
- reusable 配额满、普通写入失败或空间不足：发布状态 Warning，停止新 reusable 写入；已完成条目继续可读，cache miss 现渲染，不使播放直接失败。
- Buffering 的 transient recovery spool 独立于 reusable quota，使用后立即删除，并单独报告当前/峰值占用。
- spool 不可用且预留 RAM 也无法容纳完整恢复区间：在 `F` 受控 Stop，报告 `AudioRecoveryStorageUnavailable`；不得退化为短块断续播放。
- 半写条目、checksum/generation 不匹配或损坏条目不可读；可重建时隔离后重建，不得把损坏 PCM 当作命中。

## 7. 诊断与可观察状态

- `BufferingPerformanceInsufficient`：可恢复性能状态，不进入 canonical 诊断。
- `AudioCacheRetentionDisabled`：quota=0、配额满或普通写入失败后的 Warning。
- `AudioCacheCorruptAndRebuilt`：损坏条目隔离并重建的 Warning/运行时状态。
- `AudioRecoveryStorageUnavailable`：恢复区间无法取得 spool/RAM 的受控停止错误。
- 可观察：配置 root、reusable 当前占用/上限、transient 当前/峰值、retention 状态、最近 Warning；提供 Open Cache Folder 与 Clear Inactive Cache 的应用层能力。

## 8. 明确非目标

- 不跨 Project session 复用缓存，不把缓存写入 Project package。
- 不以缓存结果替代 canonical，不为了命中保留旧 Port/Channel 分配。
- 不在 WASAPI callback、BASSMIDI render/mix 或 ring 搬运热路径做文件 I/O、等待锁或托管分配。
- 不对每个 Segment/Unit 分别应用 Limiter，不把多个 post-Limiter 文件相加当作整曲。
- 不在设备格式未变时无条件丢弃 device-independent raw PCM；设备连接和 device-bound generation 仍必须重建。

## 9. 验证门

- exact replay 命中：第二次调用编译计数与 BASS 合成计数均为 0。
- Segment 局部修改：未变 Segment/Unit 命中；最早 causal dirty tick 后失效；不安全类型回退到 Segment 起点。
- Tempo、SF2、采样率、voice policy、Mute/Solo、Master/Limiter、设备格式变化的失效矩阵。
- 中途/小节起点 underrun、Time Signature 变化、播放终点、16 四分音符 cap、连续 underrun。
- quota=0、quota 满、写失败、损坏条目、spool unavailable、Project 关闭清理、未知文件保留。
- 不同 Unit/Segment 数量和不同工作 block 下确定性求和、Limiter 连续性和实时/离线等价。
- Playing/Buffering/Rendering 热路径零托管分配，callback 无文件 I/O、无异常越界。

## 10. 当前实施证据与剩余边界

- 已完成：`ProjectCompilationSession` exact-key canonical range cache；成功 canonical 到 `(InstanceGroupId, SubVoiceId)` 抽象 Unit 的 route-independent 投影；local Tempo/SF2/native baseline/sample rate/voice policy 完整 PCM key；一 canonical Unit 对应一个干净 1-channel BASSMIDI stream 的渲染与固定顺序混音；session-scoped 原子 cache store、quota=0/配额/暂存写失败 Warning 降级、transient spool、占用快照和 inactive-session 清理；Application Preferences 默认 16 GiB/500 voices 及 Stopped-only 重配置；MDAP v4 把 sample-domain Unit fragment 与缓存 binding 传入 Worker。
- 已完成：实时与离线 Worker 先从同一 Project session cache 串流装载有效 raw Unit PCM；命中片段不调用 BASS `ChannelGetData` 合成，miss 在 Master/Limiter 前写入暂存并仅在完整渲染后原子发布。Mute/Solo 改变会保留 reusable raw tile、使当前 capture generation 失效，并对活动 cache hit 执行 4 ms 有界退场后按当前 tick 冷启动/过滤。
- 已完成：正式主时间线按 exact range/audible-set/Master/Limiter key 缓存 post-sum 最终跨度；第二次命中完全绕过底层合成，监控变化从 producer frontier 冷启动并作 4 ms 最终 PCM 交叉淡化。Segment Preview 只启用可复用 raw Unit 层，Event/SubVoice/held 草稿不进入 reusable cache。
- 已完成：一个实际 canonical Unit route 对应一个干净的 1-channel Stream，并由同 route 上不重叠 fragment 顺序清理/复用；数量等于该任务 canonical 峰值并严格不超过 256，不为 Project 全部历史 Fragment 永久建 Stream。磁盘 recovery spool 预留失败时在 Worker Preparing 预留等容量 unmanaged RAM，spool/RAM 均不可用才在 F 受控停止。
- 已完成：raw Unit 与最终跨度的磁盘访问迁移到专用 I/O 线程；render/mix 线程只访问有界 unmanaged hot-set。实时 I/O 背压进入正式 Buffering，离线同 frame 等待；cache 写失败只失效 capture 并继续现渲染。
- 已完成：ring underrun 锁存 `F` 且不推进 read position；控制器用统一自然小节映射计算 `R`，共享 ABI v4 命令 Worker 暂停 producer，把 ring 已有前缀与补渲染后缀合成完整 `[F,R)` transient spool，重置 ring 后连续回放，再返回正常 producer。任务开始时预留最大磁盘恢复区间；预留失败不阻止首次播放，但实际 underrun 时按冻结 `F` 受控失败。
- 已完成自动门：canonical 路由不改变 Unit key、声音环境维度失效、MDAP/cache payload 损坏门、命中/miss/publish、暂存瞬态占用释放、4 ms 分块无关退场、ring 锁存、恢复源连续性/零分配、自然小节/16 四分音符 endpoint、ABI v4 round-trip/拒绝门，以及 Playback/AudioRender/Application/BASS 定向回归。修改 Realtime 或 Offline voice policy 会轮换当前 Project/session 全部 PCM generation；Offline 执行、Undo、Redo 均覆盖，且 tick-domain canonical fingerprint 保持不变。
- 已建立环境驱动原生门：同一正式 Unit 先以真实 BASSMIDI/SF2 miss 合成并发布 raw PCM，再 exact hit 逐样本比较，且命中路径的 `ChannelGetData` 合成帧数必须严格为 0；另以最大 256 canonical Unit 验证 256 个 1-channel Stream、完整拉取、render 热路径 0 B 托管分配和无 renderer fault。当前源码的 win-x64 Native AOT Worker 已成功发布并通过固定原生 manifest 校验。
- 当前本机证据：Application 274/274、Playback 75/75、BASS 完整门 170/170；固定原生 DLL 下无效 SF2 拒绝 1/1；6 个 solution CI Release build 为 0 warning/0 error。八组音色库分别独立执行完整 1053 项零跳过发布门，累计 8424/8424、0 failure、0 skip，覆盖约 0.63 MiB～827.37 MiB、GM 标准、仅钢琴、复杂结构和非标准音色库。证据目录为 `artifacts/non-ui-release-gate-sf2-01-sdetrimental-20260808` ～ `artifacts/non-ui-release-gate-sf2-08-minecraft-20260808`。
- 环境门结论：真实 cache miss→publish→exact hit 逐样本等价、命中路径零 `ChannelGetData` 合成、最大 256 个 1-channel Stream、render 热路径 0 B、无 renderer fault及 Native AOT 子进程集成均已在上述八组 SF2 中通过。更多 SF2、物理输出设备和不同磁盘属于持续加固，不再是当前实现阻塞项。
