# Midora 播放恢复、光标与 held-preview 错误清理 Requirement Trace

状态：已实现
日期：2026-08-09

## 1. 需求依据

- SRS 13.3、13.6、13.7、13.19.4～13.19.11：播放状态、光标、Seek 与 Buffering。
- SRS 13.22.7、13.24.5：Pitch Ruler、草稿音符和 held Preview 的因果 Gate Start/Gate End。
- SRS 17.2.3、20.1.2～20.1.3、INV-039、INV-043、INV-044：运行时归属、预览清理和自然小节恢复。
- `misc/Midora-Audio-Backend-Architecture-Decisions.md`：ADR-AUDIO-005、ADR-AUDIO-009。
- `misc/Midora-Core-Architecture-Decisions.md`：ADR-CORE-028。

本文不修改 SRS；它记录本次跨进程恢复存储、静止态光标和 held-preview 错误路径的实现边界。

## 2. Requirement trace

| 范围 | 输入 | 正式输出/行为 | 边界 | 失败条件与诊断 |
|---|---|---|---|---|
| Recovery spool 预留 | Project session cache、冻结 sample rate、最大自然段恢复 frame 数 | session 私有、定长、计入 transient 当前值/峰值的 `.spool` 租约 | 不受 reusable quota 影响；不进入 `.midora` | 创建或定长失败转入 RAM fallback admission |
| 跨进程交接 | 已定长 spool 租约、正式 Worker 启动 | 主进程关闭文件句柄但保留路径、计费和删除责任；Worker 建立 memory mapping | Worker 不取得 Project；主进程不并发读写 mapping | mapping 失败时 Worker 在 Preparing 按冻结容量尝试 unmanaged RAM |
| Buffering 恢复 | 锁存失败 frame `F`、主进程计算的恢复终点 `R` | 完整准备并连续回放 `[F,R)` 后释放 Buffering | callback、render/mix、ring 热路径不做文件 I/O；恢复期间不推进失败位置 | disk/RAM 均不可用时报告 `AudioRecoveryStorageUnavailable` 并受控 Stop |
| Playback Cursor | 非负 tick、当前播放状态与活动任务身份 | Stopped 或 Error/None 时更新 session cursor；下次主播放从该 tick 开始 | Error 定位不调用 backend，不清除错误，不修改 Project/Modified/Undo | 负 tick 拒绝；Preparing/Stopping/Preview 等状态拒绝 |
| held-preview 后端错误 | 打开的 causal Gate、Worker/backend fault、当前 task 和编辑锁 | 同一错误事务内清除 Gate、活动 canonical/plan、任务身份和编辑锁，并进入 Error | 不生成补偿 Project edit；不持久化预览状态 | 原始 backend fault 保存在 `LastError`；桌面 Notice 显示失败原因；随后 Mouse Up 不再发送孤立 Gate End |

## 3. 所有权、持久化与非目标

- spool、文件句柄、memory mapping、RAM fallback、播放错误、cursor 和 held-preview Gate 只属于当前 Project session 的运行时状态。
- 租约 Dispose 是正常删除和 transient 计费释放边界；Worker 启动失败、Stop 和 Project 关闭都必须走该边界。
- backend fault 清理必须原子移除 held-preview Gate，不能留下“协调器无任务、Playback 仍有 Gate”的分裂状态。
- 本修复不改变 canonical result、tick→sample、自然小节恢复算法、Stop Cursor Behavior、SoundFont 引用或 `.midora` 格式。
- 本修复不增加 Pause、Scrubbing、跨会话 cursor 恢复或跨会话音频缓存。

## 4. 验证门

1. Windows 上关闭主进程 spool 句柄后，同一路径可由 Worker 的 `MemoryMappedFile` 以 ReadWrite 打开；租约仍报告 transient 占用，Dispose 后文件删除且占用归零。
2. disk mapping 失败时，Worker 只在 Preparing 建立冻结上限内的 RAM fallback；两者均失败不阻止初始播放，但实际 Buffering 恢复必须结构化失败。
3. Prepare 失败进入 Error/None 后可以设置 cursor；状态、`LastError` 与无活动任务身份不变；下次 Play 仍先 Reset 并从新 cursor 启动。
4. held-preview 期间 backend fault 后，Gate、活动任务和编辑锁必须一起清除；后续取消/Mouse Up 是幂等清理，不产生二次 “No held-preview task is active” 错误。
5. Native AOT Worker 使用真实 Program Change + NoteOn + NoteOff、正式缓存、recovery spool 和指定 SF2 时，播放位置持续推进且 underrun 为 0。
6. Desktop 等价链必须覆盖 Project → Compile → realtime plan → Native AOT Worker，并确认实际 Project note 被消费。
