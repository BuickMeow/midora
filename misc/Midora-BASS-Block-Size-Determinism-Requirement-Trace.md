# BASS 工作块确定性需求追踪

状态：已实现  
日期：2026-08-06

## 1. 需求依据

- SRS 13.19.2：初版正式实时合成与 Render-Ahead producer 的最大工作块固定为 256 frames；事件边界、任务末尾和容量不足时允许短块，不得越过事件或硬结束边界。
- SRS 13.19.9：256-frame producer 工作区独立于按毫秒精确换算的 Render-Ahead ring 容量，工作区在 Preparing 分配并复用。
- SRS 15.19.4：buffer / chunk 大小不得改变音乐时间、事件顺序或可感知输出语义；BASSMIDI / SF2 自身不受控随机行为不承诺重复文件 hash 相同。
- `Midora-Audio-Backend-Architecture-Decisions.md` 验证门：固定原生基线和未触及 sample-voice 上限时，相同事件计划用不同工作块渲染必须逐 sample 相同。

## 2. 输入与正式输出

- 输入：同一冻结 `MidiRenderPlan`、同一正式 BASS/BASSMIDI 基线、同一 SF2、采样率、Master/Limiter 配置和未触顶的 sample-voice 上限；外层消费者可使用不同合法 Pull / WAVE chunk 大小。
- 正式输出：事件仍在精确 sample frame 前提交；每次原生 BASSMIDI decode 最多推进固定 256 frames，并在下一事件或硬结束前缩短。外层请求边界只决定从预分配 staging 工作区复制多少 frame，不再改变传给 BASSMIDI 的 decode 分块序列。
- `PositionFrames` 仍只报告已经交付给消费者的 frame；原生合成前沿最多领先一个 256-frame 固定工作区，已生成但未交付的 PCM 保存在 renderer 私有 staging 中。

## 3. 边界、运行时命令与失败条件

- staging、Port scratch 和 MIDI batch buffer 均在 Preparing 一次性分配，Rendering / Playing / Buffering 热路径只复用，不产生托管堆分配。
- 固定 decode 工作块不得越过下一 canonical 事件或任务硬结束；末块和事件前短块由事件/sample 边界决定，而不是外层 Pull 大小决定。
- Mute / Solo 与预览运行时命令在下一次尚未生成的 producer 工作块前应用；已经生成到 staging 或 Render-Ahead ring 的 PCM 不被追溯改写。
- 原生 decode 失败、短读、事件提交失败和 NaN / Infinity 继续形成原有 `AudioRenderFault`；故障 sample frame 记录原生合成前沿，不把尚未交付的 consumer 位置伪装成故障位置。

## 4. 持久化与非目标

- 固定工作区、staging 内容、consumer / render 双位置和 BASS stream 状态全部属于当前音频任务运行时，不写入 `.midora`，不进入 Undo / Redo。
- 本实现不扩大 Render-Ahead ring 的按毫秒容量，不改变 canonical 结果、MIDI 导出、WAVE 格式、Master/Limiter 算法或 sample-voice 配置。
- 不把 SRS 对第三方 BASSMIDI / SF2 不受控随机行为的免责扩大为允许 Midora 自身用不同 chunk 改变原生调用序列。

## 5. 自动验证

- 保留单音、不同内部工作块与非 2 次幂 Pull 大小的逐字节一致性测试。
- 新增冻结复杂 Tempo/Loop 等价计划：两组 Channel Unit、96 个以上事件、120/90/150 BPM 对应的非均匀 sample-frame 间隔，以 2048/1003 和 256/1003 两种内外工作块组合渲染，断言逐 sample 完全一致且活动线程分配均为 0。
- 逻辑模型 `tempo-loop` 的进程内 2048-frame 测试路径与正式子进程 256-frame 路径生成相同 WAVE SHA-256，覆盖本次实测发现的旧缺陷。
