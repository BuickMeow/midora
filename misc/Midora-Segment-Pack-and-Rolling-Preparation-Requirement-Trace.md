# Midora Segment PCM Pack 与滚动预准备 Requirement Trace

状态：产品所有者于 2026-08-11 批准实施；首轮实现与验证已完成。

## 1. 需求依据与解释边界

- SRS 13.19.1～13.19.5：实时播放不得等待完整播放范围预渲染；开始前只准备足够的连续初始音频；underrun 后仍按完整自然小节区间恢复。
- SRS 13.19.9～13.19.11、22/INV-042～INV-044：缓存位于 canonical 之后；命中不得重复 BASSMIDI 合成；实时热路径不得执行文件 I/O 或托管分配；缓存不进入 `.midora`。
- ADR-AUDIO-009：保留 canonical range、编译器 fragment、pre-Master/pre-Limiter PCM、post-Limiter playback span、Render-Ahead ring 五层职责。
- 本轮产品决定：可复用 PCM 的逻辑失效粒度改为 Segment；物理存储改为代际 Pack；Segment 内部使用固定 frame block，但 block 不是独立文件。
- 现行 SRS 13.19.11 的“Project 打开期间不 LRU 驱逐已完成条目”不再解释为“所有过时代际必须保留到关闭”。未过时的当前代际仍不做 LRU；不再被当前 Project 状态引用的旧代际可在代际重整中回收。该文句需要后续由产品所有者同步 SRS，本次不得静默改写 SRS。

## 2. Requirement trace

| 项目 | 本轮边界 |
|---|---|
| 输入 | 成功 canonical realtime plan、Segment/Track 稳定 ID 与来源、冻结 SF2/hash/native/voice/sample-rate 环境、Application Preferences cache root/quota、当前播放范围与 audible set |
| 正式输出 | 与未缓存渲染逐样本等价的 pre-Master/pre-Limiter Segment PCM；确定性求和后再统一应用 Master/Limiter；短 Render-Ahead ring 所消费的连续最终 PCM |
| 缓存身份 | Segment 完整语义 fingerprint + range cold-start 上下文 + Tempo/sample 映射 + SF2/native/voice/renderer/format；物理 Pack 位置不得成为身份 |
| 块边界 | 16,384 frame、stereo float32、128 KiB；块只用于流式 I/O、校验和 Pack 定位，不改变 Segment 逻辑失效粒度或可听边界 |
| 启动/水位 | Startup 2 s；Low 0.75 s；Resume 2 s；Target High 6 s；开头超大 Segment 只需所有活动 Segment 都准备到启动水位，不等待整段完成 |
| RAM 背压 | 物理内存 / 64，限制在 128～512 MiB；磁盘落后时先消耗有界 backlog，低于水位后进入受控 Buffering，不静默丢弃可复用 capture 后反复合成 |
| Pack | 单代最大 2 GiB；当前代际 append-only；索引只发布完整校验记录；单 session 少量 Pack/索引文件，不按 block/Segment 建文件 |
| 提交 | 8 MiB、1 s、Segment 完成三者任一到达时 group commit；任何半写、未校验或未提交记录不可命中 |
| 重整 | dead ratio ≥35% 且 dead bytes ≥256 MiB；仅 Stopped 或持续 idle 10 s；只复制当前 Project 状态引用的 live generations；至少保留 4 GiB headroom |
| 容量 | live cache 默认上限 16 GiB；quota=0 禁用可复用 retention；物理代际垃圾不计入 live identity，但重整前仍计入磁盘占用诊断 |
| Stop | 设备输出立即停止；不再开始未来渲染；已完成 Segment generation 中已经交给 Pack writer 的完整物理块允许后台排空，当前未完成物理块可丢弃；Project 关闭删除本 session 已知目录 |
| 失败 | 硬 I/O 失败禁用新的 cache capture 并显示 Warning；可继续实时合成时不让任务无限等待；已接受 hit 损坏/读失败不得输出未验证 PCM |
| 诊断 | live/dead/physical bytes、Pack generation/count、compaction eligibility/state、writer backlog、水位、读写基准、retention warning |
| 持久化归属 | 所有 Pack、索引、block、benchmark 结果均为本机运行时状态；不进入 Project、Undo/Redo、canonical fingerprint |
| 非目标 | 不改变自然小节恢复、Segment 硬边界、tick→sample、Channel Unit 分配、Mute/Solo 运行时过滤、Master/Limiter 顺序或跨 Project session 复用策略 |

## 3. 初始实施参数

- Segment block：16,384 frames。
- Startup / Low / Resume / High：2.0 / 0.75 / 2.0 / 6.0 seconds。
- RAM block pool：`clamp(physicalMemory / 64, 128 MiB, 512 MiB)`。
- 初始 Segment producer 并发：`min(4, max(1, logicalProcessorCount / 2))`，并受实测 writer bandwidth 的 50% 预算约束。
- Pack generation：2 GiB。
- Compaction：35% 且 256 MiB；Stopped 或 idle 10 s；4 GiB headroom。
- Group commit：8 MiB 或 1 s 或 Segment 完成。
- 支持门槛：本机顺序读至少 200 MiB/s、顺序写至少 100 MiB/s；初版硬件需求声明为本地 SSD。

## 4. 验证门

- Segment exact hit 的 BASS `ChannelGetData` 合成帧为 0；改动一个 Segment 后，其他 Segment key/PCM 命中保持不变。
- 同一 Segment 在不同工作 block、cache miss/hit、Pack 重整前后逐样本等价；Segment end 完整 NoteOff/Reset，无跨段 tail。
- 10,000+ block 不产生 10,000+ 文件；Pack rollover、索引事务、checksum、截断、重复 key、硬 I/O failure、quota 和 Project-close 清理均有自动测试。
- 大型长 Segment 在准备到 2 s 后即可进入 Playing；低/恢复水位状态可重复；慢 writer 先积压再受控 Buffering，不反复丢 capture。
- Playing/Buffering 渲染、混音、ring 和 cache producer/consumer 热线程保持零托管分配；WASAPI callback 不做文件 I/O。
- 指定 `TestProject.midora`：首次播放不等待所有 Segment 完整渲染；第二次 exact replay 命中；只编辑第一轨 Segment 后第二轨密集 Segment 继续命中。

## 5. 实施边界与验证证据

- `MidiSegmentRenderPlan` 和 `MidiSegmentPcmCacheKey` 以 Track/Segment 稳定 ID、完整 Segment 语义以及冻结渲染环境建立逻辑 generation；route、列表位置和物理 Pack offset 不参与身份。
- Segment miss 先按 canonical Unit 分离渲染，再按固定 Unit 顺序确定性求和为 pre-Master/pre-Limiter Segment stem；Segment exact hit 只读取一次 stem，不重复执行对应 BASSMIDI 合成。
- Segment generation 是原子可复用边界。16,384-frame block 是 Pack 内部传输和校验单位；不完整 Segment 的 PCM 前缀不能独立成为 cache hit，否则无法恢复 BASSMIDI sample phase、包络及跨 block 持续音状态，也就不能保证与连续未缓存渲染逐样本等价。
- session store 使用单后台 writer、pending-entry 立即可读路径、2 GiB Pack rollover、checksum/index checkpoint、代际失效和受门控重整；Project 关闭排空 writer 后删除本 session 目录。
- realtime producer 使用 2 s Startup、0.75 s Low、2 s Resume、6 s High 的滚动水位；初始准备不等待长 Segment 结束。并行 decode 只并行独立 Unit，Segment 和全局混音顺序保持确定。
- 2026-08-11 Release 非 UI 门：1,102/1,102 通过，无 skip；包含 Native AOT `win-x64` Worker 发布以及真实固定版本 BASS/BASSMIDI/BASSWASAPI + `Roland XP-80 Layer0.sf2` 集成测试。
- 2026-08-11 Desktop Release：build 0 warning / 0 error；Presentation 31/31、Desktop 34/34 通过。
- 2026-08-11 人工 smoke test：打开 `D:\MIDI\Midora Projects\Test\TestProject.midora`，首次播放、停止后再次播放、编辑第一轨 Segment 后再次播放均正常；未出现播放错误、无限弹窗或第二轨密集 Segment 的可见重复停顿。测试后已 Undo、保存恢复原状态并自然关闭程序。
