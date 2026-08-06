# Midora 初版非 UI 实施台账

状态：进行中
创建日期：2026-08-06
上位规范：`misc/Midora-SRS-Initial-Release-v0.1/`
问题库：`misc/Midora-Non-UI-Decision-Question-Library.md`

本文用于跨任务、跨上下文持续记录 Midora 初版全部非 UI 能力的实施范围、需求追踪、验证证据和剩余风险。它不是 SRS；与 SRS 冲突时以 SRS 为准。

## 1. 总体范围

- 包含：Project 领域模型、编辑命令与 Undo/Redo 业务边界、语义验证、全量与增量编译、Canonical Compiled Result、播放与预览控制、MIDI 导出、实时/离线音频链、WASAPI/BASS 原生边界、`.midora` 持久化、非 UI 应用工作流、设置与发布验证门。
- 排除：WPF 视图、控件、窗口、布局、键鼠交互、DPI 与纯 UI 状态。
- 人工验证：只把无法由自动测试可靠替代的实际听音、物理设备切换和硬件时延测试集中列入最终手动验收清单。
- 非目标：继续服从《Midora SRS》第 21 章和 INV-001～INV-038，不扩展初版范围。

## 2. 跨系统 Requirement Trace

- 输入：完整 Project Source Data、CompileContext、应用偏好、已验证 SoundFont 资源状态、冻结输出计划，以及被消费者接受的 Canonical Compiled Result。
- 正式输出：确定性的 canonical tick-domain 结果；由其唯一派生的播放/预览计划、SMF Type 1 文件、float32 stereo RIFF/WAVE 文件和 `.midora` 源数据包。
- 边界：稳定 ID 是身份；范围统一为 `[startTick, endTick)`；最多 16 Port × 16 Channel；Channel 10 melodic；CC91/CC93 全链路拒绝；消费者不重新解释 Project 语义。
- 失败条件：结构、引用、值域、映射、生命周期、资源、文件完整性、原生后端或发布事务失败必须归入明确阶段；失败/partial canonical 结果不可消费。
- 诊断：保留 Error/Warning/Info/Debug 原级别、稳定来源和阶段；Warning-as-error 只影响成功判定；`Channel Unit >= 248` 固定为 Info。
- 持久化归属：`.midora` 只保存源数据；canonical、缓存、诊断、Undo/Redo、Mute/Solo、设备、播放位置、任务与 UI 状态不持久化。
- 运行时归属：设备、实际采样率、buffer、原生 handle、SoundFont 绝对解析路径、sample-domain 缓存和任务状态只属于打开会话或冻结任务。
- 明确非目标：MIDI 2.0、传统 MIDI OUT、VST/DAW host、录音、Pause/Scrub、多 Project、多 SoundFont、SFZ/DLS、语义级 Voice Stealing 和 SRS 其余排除项。

## 3. 实施工作包

| 编号 | 工作包 | 状态 | 主要需求 | 退出证据 |
|---|---|---|---|---|
| NUI-01 | 仓库级构建、测试和兼容基线 | 待复核 | §2、§21、INV-027～030 | 单命令 Release 构建；全部自动测试；固定工具链/原生 manifest 门 |
| NUI-02 | 完整领域源模型与编辑事务 | 进行中 | §3～11 | 所有对象和值域、复制/删除/分割/合并、原子命令与 Undo/Redo 属性测试 |
| NUI-03 | Semantic Validation 与诊断来源 | 进行中 | §3～12 | 全错误/警告/Info 矩阵与稳定排序 golden |
| NUI-04 | Full/Incremental Canonical Compiler | 进行中 | §12、INV-009/010/015 | checkpoint + dirty range + state hash；随机输入逐字段等价 |
| NUI-05 | Playback/Preview 非 UI 状态机 | 进行中 | §13、§19 | 全状态、自动 Stop、Mute/Solo、设备故障和重复生命周期测试 |
| NUI-06 | MIDI Export 完整工作流 | 进行中 | §14、§19 | 三模式、routing、README、冻结命名、多文件原子事务、自校验 |
| NUI-07 | Audio Render 完整工作流 | 进行中 | §15、§19 | Whole/Per Track、采样率/长度/RIFF 边界、取消、独立发布、零分配 |
| NUI-08 | `.midora` 完整持久化 | 进行中 | §16、§19 | protobuf 对象图、损坏隔离、迁移、Embedded SF2、确定性与事务矩阵 |
| NUI-09 | BASS/BASSMIDI/WASAPI/Worker | 进行中 | §13、§15、INV-018～028 | ABI/版本/生命周期/设备/underrun/IPC/零分配自动门与硬件验收 |
| NUI-10 | 非 UI 应用任务协调与偏好 | 待实施 | §3、§13～16、§19 | 单任务锁、Project switch guard、Application Preferences、报告对象 |
| NUI-11 | 系统加固与发布门 | 待实施 | §21、INV-027/028/037/038 | 压力/故障/兼容/许可证/可复现发布证据 |

## 4. 当前已确认基线

- 2026-08-06 提交 `4b69e72` 完成 `.midora` 空对象图基础垂直切片；Release 构建通过，累计 330 个自动测试通过。
- 2026-08-06 提交 `61b7042` 完成 Event Instrument / Logical Track protobuf v1、对象级损坏隔离与可撤销删除、Embedded SF2 正常资源流式 package 链。
- `misc/Midora-SRS-Code-Conformance-Audit-2026-08-05.md` 记录的 1A～25.1A 均视为已确认决定，不再询问。
- 当前有 2 个未闭合的产品所有者重大决定：Q-NUI-002（初版 `.midora` 历史格式基线）与 Q-NUI-003（Project MIDI Export Settings v2 字段/默认值）；分别只暂停成功迁移器和 Export Settings schema/领域默认分支。Q-NUI-001 已按推荐方案确认并实现。

## 5. 当前实施顺序

1. Q-NUI-002 回答后补 `.midora` 成功迁移器分支；未来版本预检和保存/打开故障注入已完成。
2. MIDI Export 三模式、Readme 和文件事务已形成可运行垂直切片；Q-NUI-003 回答后补 Project 默认设置。当前转入 Audio Render 工作流，两者复用同一冻结命名服务。
3. 用 Full Compile 作为 oracle，实现 SRS 强制的 checkpoint/dirty range/state hash 增量模型并建立属性测试。
4. 完成非 UI 任务协调、播放/预览状态机和 Application Preferences。
5. 加固原生音频 Worker、WASAPI 硬件矩阵和发布门；最后生成统一人工试听/手动测试清单。

## 6. 验证日志

| 日期 | 范围 | 证据 | 结果 |
|---|---|---|---|
| 2026-08-06 | 基础 `.midora` 垂直切片 | Release build；8 个测试项目累计 330 tests；format/diff | 通过 |
| 2026-08-06 | 重对象 protobuf / 损坏隔离 | Persistence Release build；对象切片后 49 tests | 通过 |
| 2026-08-06 | Embedded SF2 正常与损坏打开链 | 2 MiB+ 流式往返、确定性、原子失败、缺失/hash/size/orphan/lease；Persistence 58 tests | 通过 |
| 2026-08-06 | Q-NUI-001、版本预检与持久化事务故障门 | 结构化 Embedded 修复动作；未来三版本字段预检；打开/保存 7 阶段故障注入；Persistence 70 tests | 通过 |
| 2026-08-06 | MIDI Export 三模式与任务事务 | 单一导出 CompileContext；Whole/Per Track/Per Port；Port 归一化；Readme 快照；冻结路径/覆盖；取消、自校验、回滚与故障注入；MIDI Export 30 tests | 通过 |
| 2026-08-06 | 当前工作树仓库级回归 | common 68 + compiler 88 + persistence 70 + MIDI export 9 + playback 27 + MIDI 18 + audio 58 + audio-device 21 = 359 tests；6 个 solution Release build；限定改动文件 `dotnet format --verify-no-changes` | 通过，0 warning / 0 error |

## 7. 未解决风险

- 当前领域模型和编译器虽已有大量覆盖，但尚未逐条证明 §7～§12 全矩阵完成。
- 当前 `.midora` 已支持完整 Event Instrument/Logical Track 对象图、完整性正常的 Embedded SF2，以及 Q-NUI-001 规定的损坏资源结构化修复门；旧格式迁移尚未实现，因此 NUI-08 仍未完成。
- 当前 Track 级增量缓存不符合 §12.21 的强制 checkpoint/state-hash 收敛模型。
- Project MIDI Export Settings 仍是空 v1 占位；schema v2 与默认值等待 Q-NUI-003，不能回写修改已发布 v1。
- 实际 BASS DLL、物理 WASAPI 设备、设备移除和人耳听音不能只凭无设备 CI 结论替代。
