# Midora 共享音频 Worker 状态 seqlock 需求追踪

日期：2026-08-07
状态：已实施并通过专项自动测试
关联决定：Q-NUI-011、ADR-AUDIO-005
需求依据：SRS §13.30、INV-018～INV-028

## 输入与正式输出

- 输入：单一音频 Worker 对共享控制区发布的完整运行状态，以及主进程对该状态的无锁轮询。
- 正式输出：来自同一次发布代次的 `SharedAudioWorkerStatus`；不得把两次同 State 发布的 Position、Fault、计数或设备标志混合为一份快照。
- 协议版本：seqlock 在共享控制 ABI v2 引入，当前 ABI v4 原样保持；offset 68 为 `Int32 statusSequence`，offset 88 的 held preview plan generation 也属于同一状态快照。Create/Open 只接受当前 v4，不接受 v1～v3 或其他版本。

## 并发、边界与失败条件

- 单 Writer 先以 compare-exchange 把偶数序列变为奇数，再发布全部字段，最后以 release 写下一偶数。
- Reader 只有在前后读到相同偶数序列时接受字段；最多重试 1024 次，过程不得分配托管对象。
- 并发 Writer、前次中断留下的奇数序列、稳定快照始终无法取得、字段值域损坏或 ABI 不匹配均为结构化 IPC 错误。
- 序列允许从 `Int32.MaxValue - 1` 经下一偶数 wrap 到 `Int32.MinValue`；只用位模式相等和奇偶判断，不使用有符号大小顺序。
- command ring 与状态 seqlock 相互独立；ABI v3 新增的 held preview pause/apply-plan/resume 及 ABI v4 新增的 Buffering recovery prepare 命令都不改变 seqlock 算法。本改动不改变 Project、canonical 持久化、MIDI/WAVE 正式输出或 `.midora`。

## 实现与验证

- `SharedAudioWorkerControl` 的 Prepared、State、Runtime Status 与 Fault 四个发布入口共用同一 seqlock 发布边界。
- `ReadStatus` 复制完整字段后复核序列，再执行闭合集和值域验证；不稳定的临时字段不会被误判为正式损坏快照。
- 自动测试覆盖 100,000 次并发发布压力、跨代组合禁止、奇数序列有界失败、并发/中断 Writer、序列 wrap、非法状态和值域、Dispose 边界，以及发布/读取零托管分配。

## 明确非目标

- 不提供多 Writer 仲裁、跨进程锁、双缓冲状态槽或 ABI v1 兼容层。
- 不改变 MDAP 文件格式、设备选择工作流或音频渲染算法；ABI v3 的 held Preview 命令/generation 与 ABI v4 的 Buffering recovery 命令分别由对应需求追踪规定。
