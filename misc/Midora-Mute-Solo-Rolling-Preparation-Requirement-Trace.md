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

## 失败条件与诊断

- 真正的 producer fault、非法并发 pause/restart、非空 ring 重开、超时或 renderer reset 失败仍使当前播放受控失败。
- 正常 EOS 待命后收到 Mute / Solo 不再产生 `The render-ahead producer is not active`。
- 运行时失败不修改 Project，不进入 Undo / Redo，也不持久化。

## 验证门

- 短范围已被完整预生成后，第一次和连续多次 monitoring reset 均成功，并从消费者 frontier 生成新 generation。
- restartable producer 在 EOS 后保持可暂停；清空旧 ring、重置底层源、恢复后可再次到达 EOS。
- 正式 Native AOT Worker 在 Rolling producer 已完整准备短范围后，连续处理交替 Mute/Unmute 命令不得进入 Faulted。
- 未 opt-in 的普通 producer 保持既有一次性生命周期和非法状态拒绝行为。
- Mute / Solo、缓存、播放控制与 Native AOT Worker 相关测试以及正式 Worker publish 必须通过。
