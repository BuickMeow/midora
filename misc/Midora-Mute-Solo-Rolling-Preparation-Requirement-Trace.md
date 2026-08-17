# Midora 播放中 Mute / Solo Rolling Preparation 修复 Requirement Trace

状态：2026-08-14，已实施。本文记录运行时 producer 生命周期与冷启动原生流清理修复，不修改 SRS。

需求依据：《Midora SRS》§12.6.2、§13.10、§13.19.9～13.19.10、§13.30，以及 INV-011、INV-018、INV-024、INV-042。

## 输入与正式输出

- 输入是播放期间到达的 Track Mute / Solo 监听命令；它只改变运行时 audible Track 集合，不修改 Project 或 canonical compiled result。
- 新进入过滤集合的 Track 按当前 producer frontier 冷启动恢复必要非 Note 状态；被排除 Track 停止贡献后续声音。
- Segment/Unit raw PCM 仍可复用，但旧 playback span generation 不得冒充新监听状态。

## ADR-MON-001：Rolling producer 的可重启 EOS 待命态

- Rolling Preparation 可能在消费者播放完之前，提前生成完整剩余范围并到达 EOS。此时“已完成预生成”不是播放任务终止，也不表示后续 Mute / Solo 命令非法。
- Rolling Preparation 的内部 producer 与包裹当前播放输出的外层 producer 均在 EOS 后保持无渲染、无分配的待命线程，以允许监听状态替换已经预生成但尚未被设备消费的后缀；不参与监听替换的普通 producer 仍按原规则结束。
- 监听命令先取得稳定 pause frontier，丢弃内部旧监听 generation 的未消费 prepared suffix，并让底层 resettable renderer 在该 frontier 重建状态。如果内部 producer 已到 EOS，则只在空 ring frontier 清除 completion 标志并恢复同一预分配 worker；不得在 Playing 中创建新线程或新工作缓冲。
- producer restart 必须同时满足：显式 opt-in、worker 已暂停、旧 generation 已完成、ring 无故障且旧 prepared suffix 已清空。任何条件不满足均受控失败，不能覆盖仍活动的 producer。

## ADR-MON-002：监听批次合并与 Stop 抢占

- 主进程对一次 Mute / Solo 状态变更生成的有序 monitoring command 集合，在共享 command ring 中原子发布。Worker 可以把读取时已经连续到达的多个 monitoring command 合并处理，但必须保持原顺序。
- 若下一批 Mute / Solo 命令在端点已停止、replacement generation 尚未预填完成时到达，旧 generation 立即作废。Worker 必须在同一个可听 consumer frontier 保持命令顺序，把新批次追加到该 replacement 的完整状态变更序列，只允许最终 generation 完成预填并重启端点；不得覆盖前一批的恢复/清理命令、串行完成已不可听的旧 generation，也不得无限停留在对外 `Playing`、位置不推进的状态。
- 一个连续 monitoring 批次只允许取得一次外层 producer frontier、执行一次 Rolling Preparation 冷启动，并在同一个稳定 frontier 将整批 source enable/disable、清理和状态恢复命令交给底层 renderer。不得为批次中的每条 MIDI 清理或恢复消息分别丢弃 prepared suffix、重建 renderer 和重新预生成。
- Stop 是会话终止命令。Worker 发现已排队的 Stop 时，可以丢弃排在它之前且尚未产生可观察输出的 monitoring / recovery 工作，并优先进入 Stopping；这不改变 Stop 之前已经送达音频设备的 PCM。
- 等待 producer pause、生成完整 Buffering recovery interval、以及等待 recovery replay 的 Render-Ahead 预填充时，都必须轮询 Stop。Stop 到达后应撤销等待、恢复为可安全释放的线程状态并退出，不得等 recovery source 脱离 Buffering 才处理 Stop。
- 本决定不改变共享内存 ABI v4 的布局、记录格式或持久化格式；它只改变 Worker 对既有有序 command ring 的运行时调度。

## ADR-MON-003：预渲染原生按键清理与监听前沿

- Rolling Preparation 可以在当前播放位置之前把未来 Gate Start 提交给 BASSMIDI Unit Stream。回退事件游标之前必须把该 Stream 恢复为真正的干净状态；`MIDI_EVENT_RESET` 仅对应 CC121 Reset Controllers，不能单独承担清音。
- 每次建立 canonical 初始状态固定按 `MIDI_EVENT_NOTESOFF` → `MIDI_EVENT_SOUNDOFF` → `MIDI_EVENT_RESET` → `MIDI_EVENT_DEFDRUMS(0)` 执行并逐项检查返回值。前两项分别清除 pressed-key 实例与全部发声，后两项恢复控制器和 Channel 10 melodic 状态；之后才允许事件游标回退并恢复非 Note 状态。
- 本决定不改变 canonical result、MDAP、共享内存 ABI v4 字节布局或字段含义、缓存 key 或持久化格式；只修正原生 Stream 清理顺序。主进程现有监听恢复 tick 仍是近似观察值；当前缺陷的确定性根因与修复均在 Worker 冷启动实际采用的 producer frontier 和原生流状态之内。
- 主进程编译 Mute/Solo 非 Note 恢复状态和选择 canonical routing 时使用已消费的设备位置，不使用可提前数秒的底层 Render-Ahead 位置；这样不会在播放指针尚未到达某 Segment 时读取该 Segment 的未来状态。Worker 仍在自己的稳定 producer frontier 丢弃和重建未播放后缀。
- Worker 的正式替换边界进一步固定为外层设备 ring 的已消费 frame：先锁存受控 Buffering 并丢弃外层未消费尾部，再让 rolling 层丢弃自身尾部并从同一 frame 重建。外层 producer 也采用可重启 EOS 待命态，避免短项目或深度预准备后快速切换触发 `The render-ahead producer is not active`。
- WASAPI callback 提交前沿不等于扬声器已经呈现的前沿。监听替换先用 `BASS_WASAPI_Lock` 阻止 callback 并发推进，在同一短临界区读取 `BASS_DATA_AVAILABLE`、锁存 callback 提交历史并执行 `BASS_WASAPI_Stop(reset=TRUE)`；随后把“设备帧前沿”通过无分配的 callback 提交记录映射回“真实内容帧前沿”。这避免 Buffering 时 callback 补零导致内容 tick 估算错误，也避免查询队列与停止之间又提交一块旧 PCM。
- 设备 reset 后外层 ring 与 rolling/renderer 同时回到该真实可听内容前沿，旧 endpoint buffer、外层 prepared suffix、rolling suffix 和 recovery replay 一并作废；达到 Resume 水位后才重新启动设备。
- 若切换恰逢 underrun recovery，已准备但尚未播放的 recovery replay 同样属于旧监听 generation，必须在同一暂停前沿作废，不能在 replacement 后重新注入。
- 4 ms 监听过渡不再把旧监听 generation 的 PCM 交叉混入新输出；新 generation 从零增益淡入。这样已经 Mute/Solo 的未来 Segment 不能通过过渡窗产生短促旧音。

## ADR-MON-004：持续监听切换的静默期与端点恢复确认

- 连续 Mute / Solo 输入先等待 40 ms 静默期再建立 replacement；静默期可被新命令延长，但单次合并最多等待 120 ms。命令仍按共享 ring 中的原顺序应用，Stop 始终抢占。该上限只合并人的快速切换突发，不改变最终监听状态。
- replacement 达到预填水位后再次经过同一有界静默期；若期间又到达命令，已准备但尚未播放的 generation 立即作废，并在同一 consumer frontier 重建，不能反复启动、停止 WASAPI 来播放中间状态。
- `BASS_WASAPI_Start` 返回成功只证明调用被接受，不证明 endpoint callback 已恢复。Worker 必须同时确认 `BASS_WASAPI_IsStarted`、至少一次新 callback，以及内容消费位置越过 replacement frontier；2 秒内未确认即受控失败并报告明确诊断，不得继续发布位置不推进的 `Playing`。
- 以上状态只属于当前播放任务；不修改 Project、canonical result、缓存键或文件格式。

## ADR-MON-005：持久 Worker 播放请求确认协议

- 持久 Worker 对正式播放 generation 的“已接收”确认使用共享内存状态中的单调 generation，并与 `Preparing` 在同一 seqlock publication 中原子发布。
- 不再创建、轮询或删除 `accepted-<generation>.mawa` 临时文件。旧方案会让 Worker 写入、主进程读取和两端清理竞争同一路径及文件句柄，能够产生真实的跨进程 sharing violation；删除该中间文件即删除竞争资源，而不是吞错或重试。
- 共享音频 Worker 控制协议由 v5 升至 v6；新增字段只属于本机同版本进程间运行时 ABI，不持久化进 `.midora`。

## 失败条件与诊断

- 真正的 producer fault、非法并发 pause/restart、非空 ring 重开、超时或 renderer reset 失败仍使当前播放受控失败。
- 正常 EOS 待命后收到 Mute / Solo 不再产生 `The render-ahead producer is not active`。
- 冷启动前已由 Rolling producer 提交的未来 NoteOn 必须全部失效；切换后不得在其原 tick 之前出现 Gate Start，也不得留下无 Gate End 的持续音。
- producer 无法在期限内暂停、命令 ring 损坏、renderer reset 失败或 recovery 返回非法结果仍是受控播放故障；仅仅等待缓存 I/O 或 recovery source 暂时返回 Buffering 不是故障，但期间必须允许 Stop 抢占。
- monitoring replacement 预填也必须有明确期限；若最终 generation 在期限内无法产生恢复输出，则进入受控播放故障，不能永久保持 `Playing` 且位置不推进。
- endpoint 重启后若未实际恢复 callback 和内容消费，同样必须在 2 秒内进入受控播放故障；仅 `Start` API 返回成功不能解除该门。
- Stop 抢占成功后以正常 `Stopped` 终态退出，不得误报为 audio worker fault；若 Worker 未在主进程的有界期限内退出，仍由主进程强制终止并报告超时。
- 运行时失败不修改 Project，不进入 Undo / Redo，也不持久化。

## 归属与非目标

- monitoring command、producer frontier、Buffering recovery 状态、Stop 抢占标志和 prepared PCM 全部属于当前播放任务的运行时状态；不写入 `.midora`，不进入 Undo / Redo，也不改变 canonical compiled result、MIDI 导出或音频文件渲染内容。
- 本轮不改变 Mute / Solo 的可听语义、自然小节 recovery endpoint、Segment/Unit 与 playback-span cache key、Pack 文件布局、Master/Limiter 顺序或 WASAPI 模式。

## 验证门

- 短范围已被完整预生成后，第一次和连续多次 monitoring reset 均成功，并从消费者 frontier 生成新 generation。
- restartable producer 在 EOS 后保持可暂停；清空旧 ring、重置底层源、恢复后可再次到达 EOS。
- 正式 Native AOT Worker 在 Rolling producer 已完整准备短范围后，连续处理交替 Mute/Unmute 命令不得进入 Faulted。
- 正式 Native AOT Worker 在前一 replacement generation 仍处于预填期时连续收到交替 Mute/Unmute，必须丢弃中间 generation，并在最后一批命令后恢复设备消费位置推进。
- 交替 Monitoring 突发必须在静默期内合并；端点恢复门必须观察到 callback 与内容位置同时推进，不能只检查 API 返回值或 Worker 状态枚举。
- 持久 Worker 连续正式播放 generation 的接收确认只能经共享内存原子 generation 完成；交换目录不得再出现 `accepted-*.mawa`。
- callback 提交记录必须在固定预分配数组内更新、零托管分配；连续 Monitoring reset 的 callback 锁定与 WASAPI reset 不得死锁，最终 callback 分配计数必须仍为零。
- 原生集成门必须覆盖 128 个未来按键已被预渲染后，在其 tick 之前执行 Disable/Cleanup/Enable 批次并回退事件游标：pressed-key 计数立即归零，允许的 de-click 窗口后保持静音，未来事件不提前。
- 一次包含多条清理/恢复消息的 monitoring 批次只能触发一次 Rolling cold start，并以原顺序到达底层 renderer。
- recovery source 持续返回 Buffering 时，已排队 Stop 必须使 recovery preparation 和 producer pause 等待可取消；Stop 之前的 monitoring command 不得阻塞 Stop 出队。
- 未 opt-in 的普通 producer 保持既有一次性生命周期和非法状态拒绝行为。
- Mute / Solo、缓存、播放控制与 Native AOT Worker 相关测试以及正式 Worker publish 必须通过。
