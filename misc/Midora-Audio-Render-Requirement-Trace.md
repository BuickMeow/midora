# Midora 初版音频文件渲染 Requirement Trace

状态：自动化实现与正式 Native AOT 文件链已通过；人工试听待统一验收
需求基线：SRS 第 15 章、第 19.7～19.8 节、第 13.17.6 节、第 13.30 节及第 22 章跨系统不变量。

## 输入

- 当前内存 `MidoraProject` 的冻结语义视图；不重新读取磁盘 `.midora`。
- 专用 `AudioRender` / `LogicalTrackAudioRender` `CompilationRequest`，范围为 `[startTick, endTick)`。
- Whole Mix 的选中有效 Logical / Pure MIDI Track 集合，或 Per Logical Track 的逐 Logical Track 独立集合。
- 已解析且完成任务级验证的 SF2 绝对运行时路径及其冻结资源身份。
- Project 当前正式生效的 Playback Master Volume；文件渲染强制使用 Limiter v1。
- 本次冻结的 8,000～192,000 Hz 整数采样率和每 Stream 1～16,777,216 的离线 sample voice 上限。
- 冻结的最终输出路径、目标存在状态和覆盖授权。

## 正式输出

- Whole Mix：一个普通 RIFF/WAVE、stereo、interleaved IEEE float32 little-endian 文件。
- Per Logical Track：每个已选且有效 Track 一个独立 WAV；按 Project 手动顺序串行处理，每个 Track 独立编译、资源分配、后端和 Limiter 状态。
- 每个文件的 frame 数精确对应冻结范围；非零起点不补前导静音，硬结束不追加 tail。
- 稳定、可定位的编译、资源、Worker、WAVE、文件系统与清理诊断，以及逐输出最终状态。

## 边界与确定性

- 正式消费者只接收 canonical compiled result，经统一 `TempoSampleMap` 一次 `AwayFromZero` 映射为 sample frame。
- 实际使用的每个 Port 建一个干净 BASSMIDI decode stream；先混合 Port，再应用 Master Volume 与 Limiter v1。
- Mute / Solo 不参与渲染；显式 Track 选择是唯一运行输入。
- 输出命名复用 MIDI 导出的公共 Windows 合法化与稳定冲突算法。
- 普通 RIFF 上限在创建任何临时输出前，对整个冻结任务一次性精确预检；任一文件越界则任务不开始。
- Worker 固定为 `win-x64` Native AOT 子进程；它不读取 Project，只接收冻结渲染计划、SF2、输出链设置和已授权临时路径。

## 失败与事务

- 无 SF2、SF2 公共验证/加载失败、Limiter/公共 Worker 环境失败：任务级失败，不创建正式输出。
- Whole Mix 任一步失败：整体失败。
- Per Logical Track：编译、渲染、写入或发布失败只影响当前 Track；已发布文件保留并继续下一 Track。
- 每个输出先写目标目录内的独立 Midora 临时文件，完整最终化并校验 WAVE 后，再移动或替换到冻结最终路径。
- 已存在目标必须有冻结覆盖授权；冻结时不存在但发布前新出现的目标不得静默覆盖。
- 取消停止当前输出、清理其临时文件且不开始后续 Track；已发布输出保留；最终发布短阶段不可中断。
- 清理失败不伪装成有效 WAV，必须保留并报告残留路径。

## 持久化与运行时归属

- `AudioRenderProjectSettings` 已按 SRS 作为 Project 源数据持久化；本次输出路径、覆盖决定、进度、结果和临时文件不持久化。
- canonical result、sample-domain plan、Worker 状态、WAVE 临时路径和渲染诊断均为运行时数据。
- 音频渲染成功、失败或取消本身不修改 Project，也不进入 Undo / Redo。

## 明确非目标

- UI、MP3/FLAC/OGG、RF64/WAVE64、按 Port 分轨、多声道/mono、位深选择、重采样、归一化、Dither、Fade、静音裁剪、Pre-roll、断点续渲、任务历史和并发多渲染任务。

## 自动验证门

- 8,000、44,100、48,000、192,000 Hz 及自定义整数采样率。
- 固定长度、非零起点、Tempo 变化、同 sample 多事件、硬结束无 tail。
- RIFF/fmt/fact/data 大小、float32 stereo 与 frame 对齐、RIFF 上限全任务拒绝。
- 全静音成功、NaN/Infinity 失败、不同工作 block 等价、热路径零托管分配。
- Whole Mix 单文件事务；Per Track 独立编译/发布、部分失败继续、取消保留已完成产物。
- 冻结路径、覆盖授权分离、中途新目标、替换失败、临时文件清理及残留诊断。
- Worker 参数冻结、无 WASAPI 依赖、退出/超时/取消故障传播。

## 当前验证边界

- 已由真实 BASS/BASSMIDI 开发环境验证：主进程经固定文件 Worker 协议启动独立进程，生成非静音 WAVE，结构/长度正确，Worker Rendering 热路径托管分配为零。
- 已由纯自动测试覆盖：Whole/Per Track、空 SubVoice 静音目标、范围与 Tempo 映射、五类采样率、RIFF 上限、覆盖竞态、取消、部分成功、WAVE 校验、临时/备份事务和稳定诊断。
- 正式 `win-x64` Native AOT publish 已按仓库 manifest 逐文件验证操作员提供的 BASS DLL；AOT `.exe` 已通过外部固定 DLL 目录的精确句柄解析、非静音文件渲染和零分配集成测试。
- 尚待统一验收的是最终集中人工试听，以及 NUI-09/NUI-11 的实时 WASAPI 硬件、压力、延迟和正式分发 notices；它们不改变本工作流的 canonical/事务契约。

## Pure MIDI Whole Mix 修正（2026-08-20）

- Whole Mix 目标发现、显式 Track 选择和自然范围同时纳入有效 Pure MIDI Tracks；纯 MIDI-only Project 不再错误返回 `NO-TARGETS`，其范围由所选 Direct MIDI Segment 内容与既有显式范围规则决定。
- `Per Logical Track` 保持仅 Logical Track，UI 在该模式下不把 Pure MIDI Track 伪装为可独立渲染目标。
- 输入仍是冻结 Project source 与正式 compilation request；输出仍只来自 canonical compiled result。该修正不建立 per-Pure-Track synth 语义，也不改变同 Root 单流合成、缓存身份、命名、WAVE 事务或持久化格式。
