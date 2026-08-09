# Playback Worker 终态快照与 SoundFont 重开恢复 Requirement Trace

状态：已实现
日期：2026-08-09

## 1. 需求依据

- SRS 6.4.2、6.6、6.7、6.10：内嵌 SF2 应随 `.midora` 自包含；有效性属于打开后的派生资源状态；有效 SF2 才允许播放、预览和音频渲染。
- SRS 13.3、13.11、13.19：播放/预览使用正式 Worker 状态机；Worker 异常退出进入 Error，正常完成必须清理并回到 Stopped。
- SRS 16.15.1～16.15.4：内嵌 SF2 位于固定 package entry；settings、manifest 和实际资源字节必须一致；运行时解包路径不持久化。
- SRS 19.3.3、19.3.10：Open Project 成功提交后建立派生 SoundFont 状态；资源问题不阻止 Project 源数据打开，但必须准确限制音频功能。
- INV-008、INV-009、INV-019：SoundFont 不改变 canonical 编译语义；正式音频消费者只使用 canonical 结果；Native AOT Worker 不解释 Project。

本文不修改 SRS，只记录本次缺陷修复的输入、输出、并发边界和资源生命周期。

## 2. Requirement trace

| 范围 | 输入 | 正式输出/行为 | 边界 | 失败条件与诊断 |
|---|---|---|---|---|
| 重开内嵌 SF2 | 已通过 package 校验的 Project、Embedded 引用、解包资源租约 | 在新 Project 提交为当前会话前，通过正式 Worker 验证解包 SF2，并把验证后的绝对路径提交给 `ProjectCompilationSession.EffectiveSoundFontPath` | 绝对路径和解包目录仅属于当前 Project 运行时；不写回 `.midora`、不进入 Undo/Redo、不改变 canonical 内容 | 资源缺失/损坏映射为不可用状态；Worker/原生基线不可用映射为 BackendUnavailable；Project 源数据仍可打开 |
| 重开外部 SF2 | Project 文件路径、相对引用、保存的 hash | 执行既有精确大小写/唯一 ignore-case 解析、完整验证和 loadability 验证，再提交有效路径 | 被动变化不修改 Project 或保存 hash | Missing、Ambiguous、Unreadable、hash 变化和 BackendUnavailable 保持既有派生状态 |
| Worker 终态判定 | 当前 Worker session 的共享状态和子进程退出码 | 先读取退出码；若已观察到退出，再读取 Worker 不可能继续修改的最终共享状态，并以同一时间顺序判定正常完成或故障 | 不修改 Shared Control ABI v4，不放宽非零退出码或非终态成功码的故障判定 | 非零退出码、Faulted 状态，或退出后仍为非终态，继续进入 Playback Error 并保留 stderr/fault 诊断 |
| 连续播放/预览 | 第一次任务生成的 Unit/Playback-span cache、第二次新的 Worker session | 每个任务独立创建、完成和释放 Worker；第二次快速命中缓存也只能得到 Completed/Stopped，不能把旧 Playing 与新退出码拼成伪故障 | cache、Worker handle、共享 mapping 和解包 SF2 均为运行时资源 | Stop/Dispose 失败仍按原有聚合错误路径报告，不掩盖真实清理故障 |

## 3. 并发与所有权决定

1. 子进程退出码与共享状态不是一个原子字段，父进程必须按“退出码在前、状态在后”的顺序构造判定快照。若退出码尚不可见，读取到的活动状态仍合法；若退出码已经可见，后续状态读取必然是 Worker 的最终状态。
2. 不把“退出码 0”单独视为正常；仍要求共享状态是 `Completed`、`Stopped` 或 `OutputDeviceUnavailable`。
3. `ProjectOpenCandidate` 继续拥有打开结果及其解包资源；活动 `ProjectContext` 通过 owner 生命周期把该租约保持到 Project 关闭。`ProjectSoundFontRuntimeSession` 先于 Compilation 释放。
4. SoundFont loadability 验证基础设施不可用属于运行时 BackendUnavailable，不得误报为 SF2 损坏，也不得使其他 Project 源数据丢失。

## 4. 明确非目标

- 不改变 `.midora` v1、SoundFont settings、manifest 或 embedded resource 格式。
- 不改变 canonical 编译、tick→sample、缓存键、音频渲染结果或 Worker Shared Control ABI。
- 不把 Render Audio 的离线 Worker 生命周期与实时播放状态合并。
- 不引入 Worker 复用、并发播放、多 SoundFont、Pause 或 Scrub。

## 5. 验证门

1. 使用真实 embedded `.midora` 重开后，无需重新选择 SF2，`CanPlayback` 与 `CanPreview` 均恢复。
2. 同一重开 Project 连续两次主播放均自然完成到 Stopped，第二次允许命中 playback-span cache。
3. 同一重开 Project 连续两次 held Event Instrument Preview 均完成到 Stopped，不产生 `workerState=Playing; childExitCode=0`。
4. 使用正式 win-x64 Native AOT Worker、固定 BASS 原生基线和真实 SF2 执行上述集成测试。
5. 无 Worker 或原生基线时，Project 仍可打开；音频功能保持不可用并报告 BackendUnavailable。
