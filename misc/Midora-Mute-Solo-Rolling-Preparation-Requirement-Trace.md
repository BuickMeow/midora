# Midora 播放中 Mute / Solo Rolling Preparation 修复 Requirement Trace

状态：2026-08-12，已实施。本文记录运行时 producer 生命周期修复，不修改 SRS。

需求依据：《Midora SRS》§12.6.2、§13.10、§13.19.9～13.19.10、§13.30，以及 INV-011、INV-018、INV-024、INV-042。

## 输入与正式输出

- 输入是播放期间到达的 Track Mute / Solo 监听命令；它只改变运行时 audible Track 集合，不修改 Project 或 canonical compiled result。
- 新进入过滤集合的 Track 按当前 producer frontier 冷启动恢复必要非 Note 状态；被排除 Track 停止贡献后续声音。
- Segment/Unit raw PCM 仍可复用，但旧 playback span generation 不得冒充新监听状态。

## ADR-MON-001：Rolling producer 的可重启 EOS 待命态

- Rolling Preparation 可能在消费者播放完之前，提前生成完整剩余范围并到达 EOS。此时“已完成预生成”不是播放任务终止，也不表示后续 Mute / Solo 命令非法。
- 仅 Rolling Preparation 的内部 render-ahead producer 在 EOS 后保持一个无渲染、无分配的待命线程；普通 render-ahead producer 仍按原规则在 EOS 后结束。
- 监听命令先取得稳定 pause frontier，丢弃内部旧监听 generation 的未消费 prepared suffix，并让底层 resettable renderer 在该 frontier 重建状态。如果内部 producer 已到 EOS，则只在空 ring frontier 清除 completion 标志并恢复同一预分配 worker；不得在 Playing 中创建新线程或新工作缓冲。
- producer restart 必须同时满足：显式 opt-in、worker 已暂停、旧 generation 已完成、ring 无故障且旧 prepared suffix 已清空。任何条件不满足均受控失败，不能覆盖仍活动的 producer。

## ADR-MON-002：监听批次合并与 Stop 抢占

- 主进程对一次 Mute / Solo 状态变更生成的有序 monitoring command 集合，在共享 command ring 中原子发布。Worker 可以把读取时已经连续到达的多个 monitoring command 合并处理，但必须保持原顺序。
- 一个连续 monitoring 批次只允许取得一次外层 producer frontier、执行一次 Rolling Preparation 冷启动，并在同一个稳定 frontier 将整批 source enable/disable、清理和状态恢复命令交给底层 renderer。不得为批次中的每条 MIDI 清理或恢复消息分别丢弃 prepared suffix、重建 renderer 和重新预生成。
- Stop 是会话终止命令。Worker 发现已排队的 Stop 时，可以丢弃排在它之前且尚未产生可观察输出的 monitoring / recovery 工作，并优先进入 Stopping；这不改变 Stop 之前已经送达音频设备的 PCM。
- 等待 producer pause、生成完整 Buffering recovery interval、以及等待 recovery replay 的 Render-Ahead 预填充时，都必须轮询 Stop。Stop 到达后应撤销等待、恢复为可安全释放的线程状态并退出，不得等 recovery source 脱离 Buffering 才处理 Stop。
- 本决定不改变共享内存 ABI v4 的布局、记录格式或持久化格式；它只改变 Worker 对既有有序 command ring 的运行时调度。

## 失败条件与诊断

- 真正的 producer fault、非法并发 pause/restart、非空 ring 重开、超时或 renderer reset 失败仍使当前播放受控失败。
- 正常 EOS 待命后收到 Mute / Solo 不再产生 `The render-ahead producer is not active`。
- producer 无法在期限内暂停、命令 ring 损坏、renderer reset 失败或 recovery 返回非法结果仍是受控播放故障；仅仅等待缓存 I/O 或 recovery source 暂时返回 Buffering 不是故障，但期间必须允许 Stop 抢占。
- Stop 抢占成功后以正常 `Stopped` 终态退出，不得误报为 audio worker fault；若 Worker 未在主进程的有界期限内退出，仍由主进程强制终止并报告超时。
- 运行时失败不修改 Project，不进入 Undo / Redo，也不持久化。

## 归属与非目标

- monitoring command、producer frontier、Buffering recovery 状态、Stop 抢占标志和 prepared PCM 全部属于当前播放任务的运行时状态；不写入 `.midora`，不进入 Undo / Redo，也不改变 canonical compiled result、MIDI 导出或音频文件渲染内容。
- 本轮不改变 Mute / Solo 的可听语义、自然小节 recovery endpoint、Segment/Unit 与 playback-span cache key、缓存文件布局、Master/Limiter 顺序或 WASAPI 模式。

## 验证门

- 短范围已被完整预生成后，第一次和连续多次 monitoring reset 均成功，并从消费者 frontier 生成新 generation。
- restartable producer 在 EOS 后保持可暂停；清空旧 ring、重置底层源、恢复后可再次到达 EOS。
- 正式 Native AOT Worker 在 Rolling producer 已完整准备短范围后，连续处理交替 Mute/Unmute 命令不得进入 Faulted。
- 一次包含多条清理/恢复消息的 monitoring 批次只能触发一次 Rolling cold start，并以原顺序到达底层 renderer。
- recovery source 持续返回 Buffering 时，已排队 Stop 必须使 recovery preparation 和 producer pause 等待可取消；Stop 之前的 monitoring command 不得阻塞 Stop 出队。
- 未 opt-in 的普通 producer 保持既有一次性生命周期和非法状态拒绝行为。
- Mute / Solo、缓存、播放控制与 Native AOT Worker 相关测试以及正式 Worker publish 必须通过。
