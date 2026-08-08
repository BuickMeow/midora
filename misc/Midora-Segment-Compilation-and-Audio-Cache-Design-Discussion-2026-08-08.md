# Midora Segment 编译缓存、音频缓存与 underrun 恢复设计讨论

状态：Q-NUI-034～Q-NUI-042 已全部确认；缓存默认设置、临时恢复存储和 realtime/offline 500 默认值范围均已冻结，已无剩余大决定阻塞；本轮只记录和分析，不修改源码、SRS、ADR、schema 或测试

记录日期：2026-08-08

关联问题库：`misc/Midora-Non-UI-Decision-Question-Library.md`

## 1. 本次要达成的产品意图

本记录把产品所有者对 Q-NUI-034 的补充理解为以下硬目标：

1. `Render-Ahead` 仍是设备前的短期未来窗口，默认约 200 ms 的性能目标与用户设置的 20～2000 ms ring 容量不变。它不承担长期渲染缓存职责。
2. underrun 后不得以“填一点、播放一点”的方式形成急速、不规则的断续。播放位置保持不动，先完成一个有音乐意义的连续恢复区间，再一次性恢复。
3. 第一次播放某个重负载区间时允许发生 Buffering；在 reusable cache 已启用且条目仍完整有效时，已经完整渲染并缓存的区间第二次播放不得再次执行语义编译或 BASSMIDI 合成，也不应再次因同一合成负载 underrun。用户把长期缓存上限设为 0，或配额/普通写入失败触发 retention-disabled 降级时，明确放弃该保证并改为每次实时渲染。
4. 长期音频缓存不是一个不可分割的整曲 PCM 文件，而是一个覆盖 Project 时间线的稀疏缓存，由稳定身份的 Segment/Unit 缓存片段组合而成。
5. 用户通常只修改一个 Segment；未改变的重负载 Segment 必须继续复用编译片段和 PCM，不得因其他普通 Segment 的编辑而反复合成。
6. 多个 Segment 的 PCM 先确定性求和，再统一经过 Playback Master Volume 和全局 Limiter；短 ring 只消费已经准备好的最终连续 PCM。

## 2. 已确认事实与需要纠正的前提

### 2.1 Segment 具备局部展开和音频隔离的主要前提

SRS 第 11.6、11.7.3、11.15.4、10.18.3 节已经规定：Segment 是所属 Logical Track 的有效时间范围容器；同一 Track 的 Segment 不重叠；不同 Track 的运行状态独立；Segment 边界清除 Logical Parameter 状态；Segment End 立即 NoteOff、Reset，且不允许 Release/Tail 越界。

因此，在把所属 Track 的 Event Instrument 绑定、相关 Project defaults、Segment 有效裁剪窗口和局部 Conductor 时间映射一并纳入依赖后，Segment 可以作为语义展开和 PCM 缓存的主要边界。这个结论是已确认事实。

但“Segment 有稳定 ID”本身不足以构成缓存键。相同 ID 下的源内容、Event Instrument、Track 绑定、Tempo、SF2、采样率或合成配置发生变化时，旧缓存都可能失效。正式缓存键必须同时包含稳定 ID、语义 fingerprint 和渲染环境 fingerprint。

### 2.2 现有编译器已经在全局分配前缓存 Segment 展开结果

当前 `MidoraCompiler` 已有 `TrackCacheEntry` / `SegmentCacheEntry`。每个 Segment 缓存其 source fingerprint、entry checkpoint、`RawInstance[]` 和诊断；缓存命中后才把所有 Segment 展开合并，并在后续执行全局 Channel Unit 分配。

所以“Segment 先独立展开、Port/Channel 后分配”并不是尚未开始的重构。需要做的是：

- 把现有私有 Segment 展开缓存形式化为可验证的抽象 Unit/Event 片段；
- 增加可证明安全的 Segment 内 checkpoint/时间片复用；
- 增加播放范围 canonical 结果缓存，避免相同 Project revision + 相同范围在每次 Start 时重新做范围编译；
- 保持 Full/Incremental、诊断、资源峰值、Port/Channel 和 canonical 结果完全等价。

### 2.3 正式 Canonical Compiled Result 仍必须包含全局分配

“Segment 编译片段不依赖 Port/Channel”只适用于编译器内部的中间缓存，不能替代正式 Canonical Compiled Result。全局同时占用决定 256 Channel Unit 上限、资源不足诊断和确定性低号 Port/Channel 分配；MIDI 导出也必须消费最终路由。

因此正式主线仍是：

```text
Project Source Data
→ Semantic Validation
→ Segment-local abstract compilation cache
→ Global allocation and Canonical Compiled Result
→ Canonical segment/unit audio projection
→ Playback / Preview / MIDI Export / Audio Render
```

音频消费者不得直接绕过 canonical 去消费编译器私有的 `RawInstance`。推荐由 canonical 提供或确定性派生一个按 Segment、抽象 Unit 分组且可去除物理 Port/Channel 编号的音频投影；只有全局 canonical 编译成功后，该投影才可正式消费。

### 2.4 “从编辑 tick 起一律失效”不是普遍正确规则

SRS 第 12.21.6 节明确要求 Dirty 起点根据修改类型回退。Logical Note、Lifecycle、Overlap、Mapping、Logical Parameter 或 Segment 裁剪变化可能影响用户指向 tick 之前已经开始的实例或后续状态；不能把 UI 编辑位置直接当作因果边界。

正确目标应改写为：从“能够证明是最早受影响的 tick”开始失效；若无法证明前缀完全等价，则回退到 Segment 有效起点。这样可以在安全编辑类型上复用前缀，又不会用旧 PCM 掩盖语义变化。

### 2.5 一 Channel 一永久 Stream 不是实现 Segment 缓存的必要条件

BASSMIDI 官方接口允许 `BASS_MIDI_StreamCreate` 创建 1～128 个 MIDI channel 的 Stream。现有 SRS/ADR 则明确固定“每个实际 Port 一个 Stream”，且把 750 sample voices 定义为“每个实际 Port Stream”的上限。官方文档也确认 voice limit 是单个 MIDI Stream 内的 sample 数上限，达到上限时会结束最低音量的 voice。

官方依据：

- [BASS_MIDI_StreamCreate](https://www.un4seen.com/doc/bassmidi/BASS_MIDI_StreamCreate.html)
- [BASS_CONFIG_MIDI_VOICES](https://www.un4seen.com/doc/bassmidi/BASS_CONFIG_MIDI_VOICES.html)
- [BASS_ATTRIB_MIDI_VOICES](https://www.un4seen.com/doc/bassmidi/BASS_ATTRIB_MIDI_VOICES.html)

因此，改变 Stream 粒度会改变达到 voice limit 时的可听结果和总 CPU 上限，不能视为纯内部优化。BASS 在一个 Stream 内对无 FX 的多个 melodic channel 求和本身不是不健康的语义；真正非线性的部分是同 Stream voice-limit 竞争。该冲突单列为 Q-NUI-036。

## 3. 推荐的分层缓存架构

### 3.1 第一层：Project revision / canonical range cache

缓存当前 Project revision 下的成功 canonical 结果，以及确定性范围视图：

```text
key = Project semantic revision
    + CompilationPurpose
    + [startTick, endTick)
    + Track selection
    + Preview/Gate parameters
```

完全相同的第二次播放直接复用已有结果，不再次调用语义展开或全局分配。不同播放范围可以从当前完整 canonical 和 Segment fragment cache 生成范围视图；范围起点状态恢复仍必须遵守 SRS，不能简单切数组。

### 3.2 第二层：Segment / Unit compiled fragment cache

每个 Segment 缓存抽象 Unit 及其事件，不包含最终 Port/Channel 编号。依赖至少包括：

```text
Segment ID and source fingerprint
owning Logical Track binding
Event Instrument and Mapping revisions
relevant Global Reset Defaults
effective crop and lifecycle/overlap inputs
local Conductor projection needed by the consumer
compiler/Mapping ABI identity
```

该层是编译器性能缓存，不是新的正式消费者输入；它必须经过全局分配生成 canonical。

### 3.3 第三层：Segment / Unit PCM tile cache

从成功 canonical 的确定性 Segment/Unit 音频投影生成 stereo float32 PCM tile。该层位于 Track Mute/Solo、Playback Master Volume 和 Limiter 之前，以便未修改 Segment 跨以下变化复用：

- 其他 Segment 编辑；
- 当前播放 Mute/Solo 组合变化；
- Playback Master Volume 变化；
- Limiter 开关变化；
- Segment 只移动位置且局部 Tempo 投影没有变化。

渲染键至少包括：

```text
canonical Segment/Unit fragment fingerprint
canonical playback/render range start and cold-start context when relevant
Project SF2 SHA-256
actual sample rate and sample format
BASS/BASSMIDI fixed version and native baseline
NOFX / NOTEOFF1 / interpolation / CPU settings
sample-voice policy
audio-render implementation version
```

该缓存按确定性小块存储。完整 tile 写完、校验通过后才原子标记为可读；不能让播放线程读到半块或混合 generation。

SRS 第 12.4.2 节已经固定：从 Project 中途冷启动时，不补发范围前已经发生的 Note On，即使该音在连续播放下理论上仍在发声。因此，“从 Segment 起点连续合成后截取中间 PCM”与“从该中间 tick 冷启动”的声音可能不同。缓存不得为了提高命中率改变这一语义：

- 相同播放/渲染范围起点的 exact replay 可以直接复用；
- 跨范围复用只有在 canonical Unit 音频投影和冷启动上下文 fingerprint 完全相同时才允许；
- 否则创建新的 range-start generation，不能直接切片包含范围前持续音的旧 PCM。

这仍满足本次核心目标：同一范围第二次不再编译/BASS 合成；它只拒绝语义并不相同的跨范围伪命中。

长期缓存写盘必须由专用 cache I/O 路径承担。WASAPI callback、BASSMIDI render/mix、ring 搬运线程不得执行文件 I/O；音频热路径只在 Preparing 时取得的有界 tile buffer pool 中生产，完整 tile 交给独立 writer 原子发布。writer 落后时通过有界背压进入 Buffering，不能在 callback 中等待磁盘或临时分配新 buffer。

### 3.4 第四层：Playback span cache

这是产品所有者最初所说“整曲渲染缓存”的准确实现：它是 Project 时间轴上的稀疏、可组合覆盖，不是单个整曲文件。

它从第三层取出当前可听 Track/Segment 的 PCM，完成：

```text
deterministic Segment/Unit sum
→ Playback Mute/Solo filter
→ Playback Master Volume
→ one global Limiter
→ ready playback span
```

该层的 key 必须包含可听 Track 集合、Master Volume、Limiter 配置、播放范围起点及 Limiter 初始状态。若 exact span 命中，第二次播放只把已完成 PCM 搬入短 ring。若第四层 miss 而第三层全 hit，则只做求和、Master 和 Limiter，不再调用 BASSMIDI；完成后也写入第四层。

Limiter 是非线性且有历史状态，不能把每个 Segment 分别过 Limiter 后再相加。随机播放起点也不能无条件复用从其他起点生成的 post-Limiter tile；必须把范围起点/状态 checkpoint 纳入 key，或从一个已验证的 Limiter checkpoint 顺序重算。

### 3.5 第五层：精确 Render-Ahead ring

现有 ring 继续严格等于用户请求的毫秒容量，只承担 device-facing PCM queue：

```text
Playback span cache
→ exact Render-Ahead SPSC ring
→ WASAPI callback
```

ring 不再承担“保存一个小节”的职责，也不决定长期缓存容量。这样 Q-NUI-034 原先认定的容量冲突消失：恢复区间先存在长期/恢复 span cache，再分批进入短 ring。

## 4. 推荐的 underrun 状态机

发生 underrun 时：

1. 在当前音乐位置 `F` 锁存 `Playing.Buffering`；光标、音乐时间和 ring read position 不推进，callback 持续输出静音。
2. 冻结当前 Project/canonical/cache generation。播放期间既有 Project Edit Lock 可防止源内容改变；Mute/Solo 等运行时变化创建新的 playback-span generation。
3. 计算恢复终点 `R`。Q-NUI-035 已冻结：`F` 在 Bar 起点时取当前完整自然小节；`F` 在小节中途时取当前剩余部分加下一个完整自然小节；随后以总计 16 个四分音符和实际播放终点裁剪，16 四分音符上限优先。
4. 以最高优先级补齐所有与 `[F, R)` 相交的 Segment/Unit PCM tile；再完成当前 Mute/Solo、Master、Limiter 下的连续 playback span。
5. 只有整个 `[F, R)` 已完成、校验通过并原子发布后，才开始把它搬入短 ring 并恢复声音。
6. 恢复后继续按优先级准备后续未来窗口；已完成的 Segment/Unit 和 playback span 保留供重复播放。
7. 连续 underrun 再次执行同一锁存流程，不把“暂时慢”提升为 Error；结构性编译失败、BASS fault、SF2 失败、缓存损坏且无法重建等按正式失败策略处理。普通 reusable cache 配额/写失败只产生 Warning 并退化为实时重渲染；只有 Q-NUI-035 所需 transient recovery spool 也无法取得时才受控 Stop。

该流程保证恢复后至少连续播放一个完整恢复区间，而不是每得到约 200 ms 就重新尝试。

## 5. 推荐的失效矩阵

| 变化 | Segment compiled fragment | Segment/Unit PCM | Playback span | 短 ring / device state |
|---|---|---|---|---|
| Segment 内容修改 | 最早可证明因果 tick 起失效；无法证明则整个 Segment | 同左，向后按 tile 失效 | 与变化时间相交及其后受 Limiter 状态影响的 span 失效 | 当前 generation 丢弃 |
| 其他 Segment 修改 | 不影响未改 Segment | 不影响未改 Segment | 只失效重叠时间范围及受 Limiter 状态影响的后续 span | 当前 generation 丢弃 |
| Event Instrument / Mapping / Lifecycle 修改 | 所有引用 Segment 失效 | 同左 | 所有相交 span 失效 | 当前 generation 丢弃 |
| Segment 移动 | 全局 canonical/routing 和放置索引重算 | 局部内容与局部 Tempo 投影相同可复用 | 旧/新覆盖范围失效 | 当前 generation 丢弃 |
| Tempo 修改 | tick-domain MIDI fragment通常可保留 | 只失效局部有效 Tempo 投影变化的 Segment/Unit | 变化后的时间放置和相交 span 重建 | 时间映射/ring 重建 |
| Time Signature 修改 | 相关全局上下文重建 | 音色 PCM 通常可保留 | 恢复区间/Bar 索引重建 | 不因拍号本身重建合成 PCM |
| Key Signature / Marker | 按 canonical Meta 规则处理 | 不失效音色 PCM | 不失效 | 不失效 |
| Project End Marker | 默认范围/canonical 硬边界重建 | 边界外 raw tile 可留作以后复用 | 最终覆盖截断 | 当前 generation 丢弃 |
| SF2/hash、采样率、BASS baseline、voice policy | canonical MIDI 可保留 | 全部失效 | 全部失效 | 全部重建 |
| Mute/Solo | 不失效 | 不失效 | 新 audible-set generation | 未消费 ring 后缀替换 |
| Master Volume / Limiter | 不失效 | 不失效 | 全部相关 span 失效 | 未消费 ring 后缀替换 |
| 设备变化但实际格式相同 | 不失效 | Q-NUI-039 已确认保留 | raw PCM 保留，device-bound output generation 重建 | 必须断开/重建 |

Tempo 失效不必机械地“从变化点到曲末失效所有 Segment PCM”。更精确的 key 是 Segment 起点的有效 Tempo 加 Segment 内 Tempo 事件的局部投影；先前 Tempo 变化造成的绝对 sample 放置变化可以只重建时间索引。将全部后续失效作为保守实现是正确但可能浪费。

## 6. Stream 方案的改进建议

产品所有者提出的“一 Channel 一 Stream”方向可以改成：

```text
一个抽象 Channel Unit 的渲染任务
→ 一个干净的 1-channel BASSMIDI Stream 语义
→ 由有界、可复用的 Stream worker pool 执行
```

这不等于为 Project 的每个 Unit 永久创建并持有一个 native Stream。Worker pool 只保留少量干净 Stream；每次复用前完成精确 NoteOff、CC120、Reset 和状态重建。Unit PCM 可分别缓存，再按 Segment 和 Project 层级确定性求和。

优点：

- 完全去除物理 Port/Channel 编号对 PCM key 的影响；
- 不同 Unit 不再在同一个 BASS Stream 内竞争 sample-voice limit；
- 可以单独复用未改变 Unit；
- native handle、SF2 绑定和预加载数量由 worker pool 上限控制，而不是由 Project Unit 总数控制。

代价：

- 现行“每 Port Stream 750 voices”改为“每 Unit render Stream，默认 500 voices”，总可用 sample voices 和达到上限时的可听结果改变；
- 首次渲染工作次数明显增加，必须依靠优先级队列、worker pool 和 Buffering 恢复区间；
- 实时与文件渲染若要完美一致，必须共同采用同一 Unit 分解、浮点求和顺序和 voice policy。

Q-NUI-036 已批准该方向。复音数允许用户修改；修改后清除全部音频 PCM/cache generations。Q-NUI-042 已进一步确认 Realtime 与 Offline 保持为两个独立设置，语义均为 `Maximum Sample Voices per Unit Stream`，两个默认值均采用 500。

## 7. 缓存存储规模与生命周期

stereo float32 在 48 kHz 下约为 384,000 bytes/s，即每累计一小时 PCM 约 1.38 GB（约 1.29 GiB）；192 kHz 约为四倍。Segment/Unit 分离后，磁盘量按所有已缓存 Unit 的累计时长增长，不只按 Project 总时长增长；再加上 playback span，极端工程可能很大。

因此不能只写“整曲缓存”而不规定：

- RAM 上限；
- 临时磁盘位置；
- 是否允许 LRU 驱逐；
- Project 打开期间是否保证已完成条目不被驱逐；
- 磁盘不足的失败语义；
- Project 关闭/应用退出后是否删除；
- 是否允许跨会话复用；
- 缓存文件校验、损坏隔离和旧 engine version 清理。

Q-NUI-037 已确认初版使用 session-scoped、磁盘后备、RAM hot-set 的缓存；缓存不写入 `.midora`，Project 关闭时删除，不跨会话复用。已完成条目在 Project 打开期间不做 LRU 驱逐。缓存目录、当前占用和最大 reusable cache 大小对用户透明，属于 Application Preferences；允许上限 0。

reusable cache 达到配额或普通磁盘写入失败时，已完成条目继续可读，新条目停止写入并产生状态 Warning；cache miss 改为每次实时渲染。该降级不直接阻止播放。

但 Q-NUI-035 的完整恢复区间仍必须暂存：最大长期缓存为 0 只表示“不保留供第二次复用”，不能消除 transient recovery spool。该 spool 消费后立即删除。Q-NUI-041 已确认默认 root 为 `%LOCALAPPDATA%\Midora\AudioCache`、默认 reusable 上限为 16 GiB、仅允许本机绝对路径；spool 独立于 reusable quota 并单独透明显示。若 spool 与预留 RAM 都不可用，则保留失败 tick 并以 `AudioRecoveryStorageUnavailable` 受控停止。

## 8. Requirement trace

- 输入：当前成功 Project semantic revision、canonical range、Segment/Unit source provenance、SF2 完整 hash、Tempo 投影、采样率、BASS baseline、voice policy、Mute/Solo、Master、Limiter、设备实际格式。
- 正式输出：确定性的 canonical compiled result；可校验的 Segment/Unit PCM tiles；完整、连续且 generation 一致的 playback spans；短 ring 中的最终 PCM。
- 边界：统一 `[startTick, endTick)`；Segment End 硬 NoteOff/CC120/Reset；恢复区间完整发布；Limiter 只在总和后执行。
- 失败条件：semantic/canonical 失败、256 Unit 资源不足、SF2 无效、BASS fault、tile checksum/generation 不一致、transient recovery spool 不可用、设备丢失。普通 reusable cache 配额/写失败不是播放失败。
- 诊断：区分 `BufferingPerformanceInsufficient`、`AudioCacheMiss`（通常仅遥测）、`AudioCacheCorruptAndRebuilt`、`AudioCacheRetentionDisabled` Warning、`AudioRecoveryStorageUnavailable`、正式 renderer/device fault；缓存命中率和重用量不进入 canonical 诊断。
- 持久化归属：缓存和命中元数据不属于 Project，不进入 `.midora`、Undo/Redo 或 Modified；缓存 root、最大 reusable bytes 等策略进入 Application Preferences。
- 运行时归属：Project session 管理 semantic/canonical generation；Audio Worker 管理 Unit 渲染池和 PCM tile 写入；Playback consumer 管理 audible mix generation、Limiter checkpoint、span coverage 和短 ring。
- 非目标：不改变 MIDI 导出 routing；不取消 256 Channel Unit 语义上限；不允许音频缓存绕过 canonical；不把 Mute/Solo 写回 Project/canonical；不引入 Reverb/Chorus、语义级 Voice Stealing 或持久化编译结果。

## 9. 决定状态索引

- Q-NUI-034：已采用五层分离架构，旧 ring-cap 推荐/A/B/C 作废。
- Q-NUI-035：已选择自然 Bar 对齐的 B；总上限改为 16 个四分音符。
- Q-NUI-036：已采用每 Unit 单通道渲染语义 + 有界 Stream pool；默认改为 500，设置变化失效全部音频缓存。
- Q-NUI-037：已采用 session 磁盘缓存、已完成条目 pin、用户可选 root/上限和 0-cache 实时降级。
- Q-NUI-038：已采用因果 Dirty tick + 无法证明时回退 Segment 起点。
- Q-NUI-039：已采用相同实际格式下保留 device-independent PCM。
- Q-NUI-040：已采用正式内容共享、草稿 transient、离线 exact-key raw Unit PCM 复用边界。
- Q-NUI-041：已采用默认 root/16 GiB、本机绝对路径限制、独立透明的 transient recovery spool，以及 spool/RAM 均不可用时受控 Stop 的推荐协议。
- Q-NUI-042：已确认 Realtime/Offline 设置继续分离、语义均改为每 Unit Stream，两个默认值均从 750 改为 500；任一设置变化清除全部音频缓存。
