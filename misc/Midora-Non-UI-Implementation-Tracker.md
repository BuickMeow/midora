# Midora 初版非 UI 实施台账

状态：进行中
创建日期：2026-08-06
最近决策更新：2026-08-07；本次只更新文档，未修改代码或契约资产
上位规范：`misc/Midora-SRS-Initial-Release-v0.1/`
问题库：`misc/Midora-Non-UI-Decision-Question-Library.md`

本文用于跨任务、跨上下文持续记录 Midora 初版全部非 UI 能力的实施范围、需求追踪、验证证据和剩余风险。它不是 SRS；通常与 SRS 冲突时以 SRS 为准。对于问题库中已经由产品所有者明确批准、且目的就是修改现行 SRS 的决定，问题库构成变更授权；实施前必须先把对应 SRS/ADR 修订到一致，不能在规范仍冲突时直接改代码。

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
| NUI-01 | 仓库级构建、测试和兼容基线 | 非 UI 自动门与单实例内核完成；WPF 入口接线待 UI 阶段 | §2、§3.3、§21、INV-019/027～030 | 单命令 Release 构建；全部自动测试；固定工具链/原生 manifest 门；唯一主实例与启动请求转发 |
| NUI-02 | 完整领域源模型与编辑事务 | History/Modified 与十三批非 ID 分配对象命令完成；Q-NUI-005/Q-NUI-009 已确认，待实施；Q-NUI-024/Q-NUI-025 的 ID 全链契约已闭合，待实施 | §3～11 | Track/Instrument/Folder/Damaged/Segment、Conductor、Project Settings/Metadata、Track Color、External SoundFont、Note/Lane/Point、Instrument Lifecycle、SubVoice、Template Event、Value Curve、Initial/Reset State、Envelope、Mapping Function、Mapping Chain/Step，以及安全的 Logical Parameter Definition/Mapping/Target Settings 属性已覆盖；后续补 ID 全链、分配命令和 Lane 原子迁移 |
| NUI-03 | Semantic Validation 与诊断来源 | 进行中 | §3～12、§16.19 | 全错误/警告/Info 矩阵与稳定排序 golden；显式 Track 作用域和 Damaged Instrument 绑定 Error 已闭合 |
| NUI-04 | Full/Incremental Canonical Compiler | 强增量模型完成；全 §12 矩阵继续扩充 | §12、INV-009/010/015 | Segment checkpoint + dirty range + state hash 已实现；固定种子连续编辑逐字段等价 |
| NUI-05 | Playback/Preview 非 UI 状态机 | 进行中 | §13、§19 | 全状态、自动 Stop、Mute/Solo、设备故障和重复生命周期测试 |
| NUI-06 | MIDI Export 完整工作流 | 进行中 | §14、§19 | 三模式、routing、README、冻结命名、多文件原子事务、自校验 |
| NUI-07 | Audio Render 完整工作流 | 自动化与 AOT 文件链完成；首轮试听发现 SubVoice/Mapping 实例末尾音量突增，待分析修复和复测 | §15、§19 | Whole/Per Track、采样率/长度/RIFF 边界、取消、独立发布、零分配；M-AUD-001/003 通过，M-AUD-002 失败 |
| NUI-08 | `.midora` 完整持久化 | 进行中；开发期 v1 直接修订原则与单 `long` ID 精确编码均已确认，待实施 | §16、§19 | protobuf 对象图、损坏隔离、开发期 v1 契约重写、Embedded SF2、确定性与事务矩阵 |
| NUI-09 | BASS/BASSMIDI/WASAPI/Worker | 进行中；首轮进程内听感与 M-AUD-010 完成；正式子进程人工入口传入 managed `.dll`，M-AUD-007～009/011/012 被阻塞 | §13、§15、INV-018～028 | ABI/版本/生命周期/设备/underrun/IPC/零分配自动门；修复 Native AOT `.exe` 验收入口后补硬件矩阵 |
| NUI-10 | 非 UI 应用任务协调与偏好 | 非 UI 核心完成；WPF composition 待 UI 阶段 | §3、§13～17、§19～20 | 单任务/锁级、自动 Stop、Project switch guard、New/Open Project 候选事务、Save/Save Copy 会话事务、结构化报告、Application Preferences 及缓存失效均有自动测试 |
| NUI-11 | 系统加固与发布门 | 自动发布门完成；人工音频首轮存在失败/阻塞；产品已放行含 DLL 分发；压力、硬件与发布当日外部条款核验待闭合 | §21、INV-027/028/037/038 | 锁定 SDK/依赖、零 Skip 全测、AOT/hash/notices 已自动化；关闭人工失败/阻塞；发布包加入供应商原始许可文本并按发布当日条款复核 |

## 4. 当前已确认基线

- 2026-08-06 提交 `4b69e72` 完成 `.midora` 空对象图基础垂直切片；Release 构建通过，累计 330 个自动测试通过。
- 2026-08-06 提交 `61b7042` 完成 Event Instrument / Logical Track protobuf v1、对象级损坏隔离与可撤销删除、Embedded SF2 正常资源流式 package 链。
- `misc/Midora-SRS-Code-Conformance-Audit-2026-08-05.md` 记录的 1A～25.1A 均视为已确认决定，不再询问。
- 2026-08-07，Q-NUI-002～Q-NUI-023 均收到产品答复；Q-NUI-024 进一步确认将 `MidoraId` 与 Project allocator 重构为单个正 `long`。Q-NUI-003 确认外部 Project 文件仍处开发期，字段变化直接修订 v1，不创建 v2；Q-NUI-011 的内部 ABI 允许独立升级；Q-NUI-023 选择备选 A，直接把开发期 v1 TPQ 收窄为 `1..32767`。
- 2026-08-07，Q-NUI-025 采用推荐方案，至此 Q-NUI-001～Q-NUI-025 均已收到产品答复。单 `long` ID 固定为 JSON canonical 十进制 integer、无符号无前导零的十进制对象文件名和 protobuf 标量 `int64`；直接修订开发期 v1，不提供 128-bit v1 迁移器。所有决定均进入待实施或既有实现已获确认状态。
- Q-NUI-013 记录的是产品所有者基于公开身份 `Zacksony`、零收入和 GitHub Release 渠道给出的含 DLL 分发放行；发布当日官方条款复核与供应商原始许可文本打包仍是外部发布硬门，不能把当前答复表述为永久法律结论。

## 5. 当前实施顺序

1. 按已闭合的 Q-NUI-024/Q-NUI-025 一次性重构 `MidoraId`/`nextStableId`、Mapping ABI、开发期 v1 持久化契约及所有受影响测试，避免在旧 128-bit 基线上继续扩张代码。
2. 按 Q-NUI-003、Q-NUI-005、Q-NUI-009、Q-NUI-019 与 Q-NUI-023 实施 Export Settings v1、ID 分配 History、Definition/Lane 原子迁移、Bar:Beat:Tick/截断 Warning 和 TPQ `1..32767` 全链。
3. 按 Q-NUI-011 与 Q-NUI-022 实施共享状态 ABI v2 和 held Preview 因果 Gate，并补进程内/子进程一致性、零分配与人工时延验收。
4. 先用 canonical trace 与 PCM 包络测量复现 M-AUD-002/M-AUD-005 的实例末尾音量突增；再依据 SRS 决定 Compiler/renderer 修复或人工示例调整，本轮快照不得覆盖。
5. 修复 Console 正式子进程验收入口，使其使用已发布并校验的 `win-x64` Native AOT `.exe`；依次复测 M-AUD-007～009、011～012。M-AUD-004～006 的进程内终端技术指标已补齐。
6. 继续 NUI-02/NUI-03 的剩余领域编辑命令、诊断矩阵和 §7～§12 组合覆盖；Full/Incremental oracle 持续作为每个增量的硬门。
7. 发布前按 Q-NUI-013 再核验当日官方条款，把供应商原始许可文本与 notices 纳入最终包；未到实际发布日期时不得把该动态核验误报为已完成。

## 6. 验证日志

| 日期 | 范围 | 证据 | 结果 |
|---|---|---|---|
| 2026-08-06 | 基础 `.midora` 垂直切片 | Release build；8 个测试项目累计 330 tests；format/diff | 通过 |
| 2026-08-06 | 重对象 protobuf / 损坏隔离 | Persistence Release build；对象切片后 49 tests | 通过 |
| 2026-08-06 | Embedded SF2 正常与损坏打开链 | 2 MiB+ 流式往返、确定性、原子失败、缺失/hash/size/orphan/lease；Persistence 58 tests | 通过 |
| 2026-08-06 | Q-NUI-001、版本预检与持久化事务故障门 | 结构化 Embedded 修复动作；未来三版本字段预检；打开/保存 7 阶段故障注入；Persistence 70 tests | 通过 |
| 2026-08-06 | MIDI Export 三模式与任务事务 | 单一导出 CompileContext；Whole/Per Track/Per Port；Port 归一化；Readme 快照；冻结路径/覆盖；取消、自校验、回滚与故障注入；MIDI Export 30 tests | 通过 |
| 2026-08-06 | 当前工作树仓库级回归 | common 68 + compiler 88 + persistence 70 + MIDI export 9 + playback 27 + MIDI 18 + audio 58 + audio-device 21 = 359 tests；6 个 solution Release build；限定改动文件 `dotnet format --verify-no-changes` | 通过，0 warning / 0 error |
| 2026-08-06 | Audio Render 冻结任务、文件 Worker 与事务 | Whole/Per Track；空 SubVoice 静音目标；5 类采样率；RIFF/WAVE/取消/部分成功/覆盖竞态/残留；真实 BASS 文件 Worker 非静音与零分配；正式 win-x64 Native AOT publish + manifest/DLL 门 + AOT 文件链；全仓 9 个测试项目累计 420 tests；限定改动文件 format 门 | 通过，0 failure |
| 2026-08-06 | 强增量编译收敛模型 | Segment-entry/track-terminal checkpoint、dirty range、exact state + hash 收敛、未变 source 后缀复用、Full oracle；Compiler 100 tests，全仓当时累计 429 tests | 通过，提交 `d164969` |
| 2026-08-06 | 非 UI 应用任务协调与本机偏好 | 单任务 admission/拒绝、自动 Stop、嵌套 lease、cleanup continuation、Project switch 顺序、嵌套 Save 不可取消、音频设置 Stopped-only、确定性/原子偏好 JSON、全部数值边界与失败默认；Application 32 tests；10 个测试项目累计 461 tests；6 个 solution Release build | 通过，0 warning / 0 error / 0 failure |
| 2026-08-06 | Project History、Modified 与 Undo/Redo 基础 | 未保存/已持久化来源、保存点、branch、external dirty、无操作、锁、canonical 同步、异常 rollback + Full Compile、通知重入；Application 42 tests；10 个测试项目累计 471 tests；Core Release build | 通过，0 warning / 0 error / 0 failure；ID 分配命令等待 Q-NUI-005 |
| 2026-08-06 | 首批具体领域 History 命令 | Track/Instrument/Folder/Damaged Placeholder 重命名、绑定、排序、删除与精确恢复；Segment 跨 Track 移动、裁剪窗口、删除、连接；名称 schema 上限与控制字符；每步 Full/Incremental 等价及 `nextStableId` 不变；Application 54 tests；10 个测试项目累计 483 tests；Core Release build；限定改动文件 format 门 | 通过，0 warning / 0 error / 0 failure；ID 分配命令仍只等待 Q-NUI-005 |
| 2026-08-06 | Conductor 与 Project Settings History 命令 | Tempo/Time Signature/Key Signature/Marker/现有 End Marker 的修改、移动、删除和 tick 0/冲突/值域门；Playback 与 Audio Render Settings 原子快照；SMF 24-bit Tempo canonical 语义验证补齐；Application 63 tests、Compiler 101 tests；10 个测试项目累计 493 tests；Core Release build；限定改动文件 format 门 | 通过，0 warning / 0 error / 0 failure |
| 2026-08-06 | Logical Note、Lane/Point 与 Instrument 基础属性 History 命令 | Note 裁剪区外编辑和值域；Lane 显式 Clamp/Discard 重绑定、Enum 最近值/Step、删除确认；Point 类型/值域/同 tick 门；Description/Color/Root Note 编译失效分类；精确对象/ID Undo；Application 69 tests；10 个测试项目累计 499 tests；Core Release build | 通过，0 warning / 0 error / 0 failure；Q-NUI-007 小决定待确认 |
| 2026-08-06 | Instrument Lifecycle 与 SubVoice History 命令 | Template Length 内容/Loop 下界；Isolation 数据保留；Loop restricted 修复；Overlap/Lifecycle enum 门；SubVoice 名称/Root/排序/最后一条保护/非空确认及 Mapping 引用原子删除恢复；Compiler 未定义策略枚举诊断；Application 75 tests、Compiler 105 tests；10 个测试项目累计 509 tests；Core Release build | 通过，0 warning / 0 error / 0 failure |
| 2026-08-06 | Template Event History 命令与损坏枚举防线 | Note/CC/Bank/Program/Pitch Bend/RPN/NRPN/Pitch Bend Range 值域和属性更新；Template Length 自动延长；同 tick/同目标及 PBR/RPN0 替换；Bank 活动映射组件保护；精确对象/ID Undo；未知 Template Event Kind/Curve Interpolation 诊断；Application 80 tests、Compiler 108 tests；10 个测试项目累计 517 tests；Core Release build | 通过，0 warning / 0 error / 0 failure |
| 2026-08-06 | Value Curve History 与首点边界修复 | Target Rounding/Overflow；Point tick/value/interpolation 更新及自动延长；Point/Curve 精确对象删除恢复；离散事件保留；首点前不输出隐式 0；编译期点集单次排序；Application 84 tests、Compiler 109 tests；10 个测试项目累计 522 tests；Core Release build | 通过，0 warning / 0 error / 0 failure |
| 2026-08-06 | Initial State / Reset Defaults History | Project/Instrument/SubVoice Initial precedence；Project Reset；九类 MIDI target 设置/删除和值域；null 缺失语义；对象/ID/Template Length 保持；未知 MidiValueKind 诊断；Application 104 tests、Compiler 110 tests；10 个测试项目累计 543 tests；Core Release build | 通过，0 warning / 0 error / 0 failure |
| 2026-08-06 | Envelope Preset History | ADSR 时长/值/名称原子更新；Isolation restricted 编辑；被引用删除确认；断裂 Step 引用保留；未引用 restricted 删除修复；精确对象/索引/ID Undo；Application 108 tests；10 个测试项目累计 547 tests；Core Release build | 通过，0 warning / 0 error / 0 failure |
| 2026-08-06 | C# Mapping Function History | 唯一名称、精确源码、Context 声明集合；ABI v1 保持；非法 Unicode/长度契约；编译错误可保存；被引用删除确认与断裂 ID；缓存修订经 canonical 会话刷新；Application 113 tests；10 个测试项目累计 552 tests；Core Release build | 通过，0 warning / 0 error / 0 failure |
| 2026-08-06 | Mapping Chain / Step History 与完整内置映射矩阵 | 事件参数/Logical Parameter Chain 稳定 ID 定位；Chain/Step Enable；Step 全快照、排序、删除；整链空 sentinel 删除/恢复；未完成配置保存与禁用诊断隔离；未知四类 Mapping 枚举防御；12 种内置 Source、12 种内置 Operation、Remap InputOverflow、两向 Divide 与四种 DivideByZero policy；Application 122 tests、Compiler 147 tests；10 个测试项目累计 598 tests；Core Release build | 通过，0 warning / 0 error / 0 failure |
| 2026-08-06 | Logical Parameter Definition / Mapping / Target Settings History | 安全 Definition 属性、display-only Track cache 复用、引用保留删除；Mapping source/target/order/共享 Target Settings 与确认删除；事件参数三类 Target Settings；未知 Parameter Type 与越界 Enum item 防御；Q-NUI-009 迁移分支保持暂停；Application 135 tests、Compiler 149 tests；10 个测试项目累计 613 tests；Core Release build | 通过，0 warning / 0 error / 0 failure |
| 2026-08-06 | Project Metadata 与 Logical Track 颜色 History | 六字段原子 Metadata 快照；原 Unicode/空白保留；256/4,096/65,536 scalar 和控制字符边界；只读系统时间/耗时不覆盖；Track nullable opaque sRGB override；空 change-set 与 Track cache 复用；Application 139 tests、Persistence 70 tests；10 个测试项目累计 617 tests；Core Release build | 通过，0 warning / 0 error / 0 failure |
| 2026-08-06 | External SoundFont 两阶段验证与 History | 允许目录/完整 hash、正式 BASSMIDI `FontInit` + 全 sample preload、提交前内容复核；source/effective path 同事务、Undo/Redo/clear/no-op；验证失败/内容竞态/锁冲突零变更；Application 144 tests、BASS 67 tests；10 个测试项目累计 625 tests；Core Release build | 通过，0 warning / 0 error / 0 failure；Embedded 编辑与文件监控仍待后续 |
| 2026-08-06 | External SoundFont 验证缓存与监控失效 | Windows volume/file ID + size + raw last-write FILETIME + absolute path + stored identity 完整缓存键；同句柄前后 stamp；强制复核；FileSystemWatcher 只失效；持续变化重试；同 size/mtime 文件替换检测；Persistence 74 tests；10 个测试项目累计 629 tests；Core Release build | 通过，0 warning / 0 error / 0 failure；Project 打开/首次音频任务状态机接线待后续 |
| 2026-08-06 | Project SoundFont 运行时可用状态与消费门 | open/refresh 状态；External hash/fallback Warning；Embedded lease loadability；预期 reference 并发提交；History/source 变化失效；启动前同步 stamp gate；监控自动 Stop 播放/预览并释放锁；Application 153 tests、Persistence 75 tests；10 个测试项目累计 639 tests；Core Release build | 通过，0 warning / 0 error / 0 failure；Embedded 选择 History 仍等待 Q-NUI-005 |
| 2026-08-06 | Canonical 失败结果与 CompileContext 防御 | 失败结果统一 partial/不可消费；五阶段 `FailureStage`；保留阶段性统计；非法 Purpose 和不参与编译的 SubVoice 选择确定性诊断；取消状态与 FileSystemWatcher 重试测试去竞态；Compiler 156 tests；10 个测试项目累计 646 tests；Core 全解 540 tests | 通过，0 failure |
| 2026-08-06 | Canonical CompileContext 冻结摘要 | Purpose、请求/解析范围、Explicit/End Marker/Natural end 来源、Track/SubVoice 全量或显式选择、Warning 策略及消费者类别；请求集合防别名；Full/Incremental 逐字段 oracle；Compiler 165 tests；10 个测试项目累计 655 tests；Core 全解 549 tests | 通过，0 failure |
| 2026-08-06 | 范围内资源分配与结构化峰值统计 | 范围相交实例过滤；allocation instance/group 双身份；Segment/Instrument/SubVoice/Port 统计；shortage 区间与相关稳定 ID 集合；Full/Incremental failure oracle；Compiler 169 tests；10 个测试项目累计 659 tests；Core 全解 553 tests | 通过，0 failure |
| 2026-08-06 | Canonical 细粒度来源链 | Parameter/Mapping/Step/C# Function/Curve/Envelope ID；Mapping 异常精确 Step；模板/曲线/逻辑参数成功来源；Initial/Restore/Reset/Boundary Origin；来源进入 fingerprint；Compiler 176 tests；10 个测试项目累计 666 tests；Core 全解 560 tests | 通过，0 failure |
| 2026-08-06 | 范围硬边界 Note FIFO 与确定全序 | 活动 Note 来源 FIFO；真实 NoteOff/velocity-0 逐实例释放；硬边界保留 Logical Note/Template Event 来源；活动音/Channel/target 显式排序；canonical 完全 tie-breaker；空 Voice Info 在范围/Voice 过滤后生成；Compiler 181 tests；10 个测试项目累计 671 tests；Core 全解 565 tests | 通过，0 failure |
| 2026-08-06 | CompileContext Debug 诊断收集 | 默认关闭的 Debug 门及冻结摘要；成功/语义失败/partial 的确定上下文和结果统计；不改 canonical/失败政策；执行遥测与正式诊断分离；Full/Incremental Debug 等价；Compiler 185 tests；10 个测试项目累计 675 tests；Core 全解 569 tests | 通过，0 failure |
| 2026-08-06 | 播放启动/停止/冷重启失败原子性 | 显式与 Loop 范围启动前预检；SF2 锁前预检/锁内复核；Prepare/Stop/Seek restart 失败清 active result/plan/task/锁并保留 cursor；Error 直接 Reset 恢复；Playback 32 tests；10 个测试项目累计 680 tests；Core 全解 574 tests | 通过，0 failure |
| 2026-08-06 | Mute/Solo 冷恢复活动路由 | RangeRestore 状态按 Track/Instance/SubVoice 身份重路由回活动 canonical Port/Channel；冷编译只供状态、不采用其紧凑分配；路由失败发送前原子回滚；Playback 33 tests；10 个测试项目累计 681 tests；Core 全解 575 tests | 通过，0 failure |
| 2026-08-06 | Reset Playback Engine 尽最大努力清理 | Stop 失败仍继续完整 backend Reset；Reset 成功恢复 Stopped；双失败聚合且保持 Error；Stopped 与 Error→Play 复用同一恢复门；Playback 37 tests；10 个测试项目累计 685 tests；Core 全解 579 tests | 通过，0 failure |
| 2026-08-06 | Preparing/Preview 源快照锁定 | Project Edit Lock 先于同步 Preparing 通知；PreviewCompiler 全程位于同一锁租约；编译失败清任务与锁、Error 后 Reset 可恢复；Playback 39 tests；10 个测试项目累计 687 tests；Core 全解 581 tests | 通过，0 failure |
| 2026-08-06 | 运行中关闭 Loop 冷重启 | 关闭时从当前 tick 重建到原请求/自然终点；已越过原显式终点直接完成；同值设置 no-op；不再消费旧 loopEnd 裁剪计划；Playback 42 tests；10 个测试项目累计 690 tests；Core 全解 584 tests | 通过，0 failure |
| 2026-08-06 | WASAPI 严格设备枚举与 UTF-8 模式 | BASS Init/设备枚举前启用并回读 UTF-8；只允许 BASS_ERROR_DEVICE 正常终止；异常不返回部分列表；enabled/present/output 过滤与 Open 时复核；空/重复 ID 拒绝；BASS 71 tests、WASAPI 32 tests；10 个测试项目累计 705 tests | 通过，0 failure |
| 2026-08-06 | Buffering 音乐位置冻结 | 本地/共享 ring underrun 整块静音且返回 0 consumed frame；read position 与已缓冲数据不动；WASAPI 拒绝 Buffering+非零消费协议；BASS 72 tests、WASAPI 33 tests；10 个测试项目累计 707 tests | 通过，0 failure |
| 2026-08-06 | WASAPI 设备变化故障传播 | 只把当前设备 disabled/fail 认定为丢失；系统默认选择跟随默认映射变化、显式设备不误停；正式 Worker 与对照 backend 并入不可恢复 fault；WASAPI 35 tests；10 个测试项目累计 709 tests | 通过，0 failure |
| 2026-08-06 | 实时音频拉取协议闭包 | 四种状态闭合集、帧数/零进展一致性门；Render-Ahead Buffering 重试不推进、未知状态故障且零分配；BASS 74 tests、WASAPI 35 tests；10 个测试项目累计 711 tests | 通过，0 failure |
| 2026-08-06 | 正式实时 Worker 启动与终态门 | 正式客户端只接受 Native AOT `.exe`，托管 `.dll` 仅内部测试放行；绝对启动路径；Stop/运行时同时校验共享终态和 exit code；BASS 80 tests、Playback 48 tests；10 个测试项目累计 723 tests | 通过，0 failure |
| 2026-08-06 | MDAP v2 严格有界解析 | 写前完整大小门；读前剩余 payload 计数门；reserved 必须为零；校验和有效的非法 Port/保留位/伪造大计数统一拒绝；BASS 83 tests；10 个测试项目累计 726 tests | 通过，0 failure |
| 2026-08-06 | 共享内存 ABI v1 损坏闭包 | ring 单调/容量/溢出门先于指针运算；Stop/Monitoring 全 payload 与 reserved 双向校验；整批失败无前缀；状态/映射/header/Dispose 边界；BASS 97 tests；10 个测试项目累计 740 tests | 通过，0 failure |
| 2026-08-06 | 实时 Worker 启动失败原子性与输出排空 | SF2/Worker/native 绝对路径门；计划目录/MDAP/共享区/管道/进程逐层反向回收；stdout/stderr 启动后并发排空；Faulted/Probe/Stop 有界退出；监控异常任务化；BASS 99 tests；10 个测试项目累计 742 tests | 通过，0 failure |
| 2026-08-06 | Native AOT Worker 输入协议门 | fully-qualified 现存输入/新输出；0/1 布尔；实时/文件 256-frame、buffer、Limiter v1 与文件采样率门；MDAP/策略先于原生加载；独立进程验证非法相对路径发布 Faulted；BASS 114 tests；10 个测试项目累计 757 tests | 通过，0 failure |
| 2026-08-06 | 正式实时 backend 清理故障聚合 | 最终状态读取失败仍释放进程/映射/计划目录；Stop/capture/release 按序聚合；单次 fault 判断只消费一个状态值；构造期拒绝非法 buffer/timeout/Limiter 算法；Playback 51 tests；10 个测试项目累计 760 tests | 通过，0 failure |
| 2026-08-06 | 非 UI 可复现发布门 | SDK 10.0.302 精确锁定；32 项目 NuGet lock；全仓 win-x64 RID；固定 BASS hash；6 solution Release；Native AOT Worker + manifest/MIT/notices；加入 Open Project 候选事务后 10 项目 TRX 精确 809 tests、0 Skip；`Test-NonUIRelease.ps1` 再次完整运行 | 通过，0 warning / 0 error / 0 failure / 0 skip；正式 BASS 分发仍等待 Q-NUI-013 |
| 2026-08-06 | 单应用实例与启动请求转发 | Session-scoped 命名 Mutex lease；CurrentUserOnly Named Pipe；严格有界 binary v1；真实子进程转发；唯一 Primary、Unicode/空参数、畸形/截断、队列满、取消、失联 owner、释放/重取；Application 167 tests；全仓基线 774 tests | 通过，0 failure；WPF 入口接线待 UI 阶段，Session 范围等待 Q-NUI-014 确认 |
| 2026-08-06 | Save / Save Copy 应用事务 | 首存路径/file info/保存基线原子提交；当前路径普通 Save；副本保持 Modified/History/内存时间；工程时间快照；Damaged/Embedded 门；覆盖/取消/并发矩阵；Application 179 tests；全仓基线 786 tests | 通过，0 failure；Save Copy 自身路径等待 Q-NUI-015 确认 |
| 2026-08-06 | New Project 非 UI 候选事务 | TPQ/Metadata/默认空领域图；Unsaved/Create and Save；首次 package 原子发布；External 受限相对 SF2 与两阶段验证；Embedded 快照所有权；失败不返回候选；提交前不累计工程时间；Application 193 tests；全仓基线 800 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip |
| 2026-08-06 | Open Project 非 UI 候选事务 | `.midora`/`.zip` 严格候选；当前 Project 保留边界；恢复 Modified/诊断；Damaged 保存门；Embedded lease/External runtime 状态；无长期源文件占用；Q-NUI-016 恢复时钟一致性；Application 202 tests、Persistence 75 tests；全仓基线 809 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip |
| 2026-08-06 | Close / Exit 工程时长生命周期 | Project Switch Guard 与 Save 期间继续累计；实际切换入口才 Begin Closing；成功保持暂停；实际切换失败恢复且不补计暂停窗口；Application 205 tests；全仓基线 812 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；精确暂停边界等待 Q-NUI-017 确认 |
| 2026-08-06 | Recent Projects 本机 MRU | 分离 JSON v1；成功激活后显式记录；10 项 Windows OrdinalIgnoreCase MRU；严格未知/重复字段；1 MiB 门；离线路径保留/可用投影；原子失败保持；Application 217 tests；全仓基线 824 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；列表策略等待 Q-NUI-018 确认 |
| 2026-08-06 | Native interop win-x64 ABI 快照 | BASS/BASSMIDI/BASSWASAPI 正式结构 size/offset；pointer/function pointer/handle 宽度；精确 LibraryImport DLL/entry point；BOOL/handle return；统一 Windows x64 默认调用 ABI；BASS 119 tests；全仓基线 829 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；固定 DLL baseline 与 Native AOT Worker 通过 |
| 2026-08-06 | 显式 Track 编译诊断作用域 | 未选 Track/未参与 Instrument 的 Stable ID 损坏、重复 Instrument ID 与断裂 Folder Warning 不污染 scoped compile；编译查找表同步收窄；Whole Project 仍完整捕获；Compiler 189 tests；全仓基线 833 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip |
| 2026-08-06 | Damaged Event Instrument 绑定编译边界 | Placeholder 不参与定义；绑定 Track 专用 MIDORA1305 Error 与 Track/Instrument 来源；普通断裂仍为 Info；Whole/健康选择/损坏选择作用域；Compiler 191 tests；全仓基线 835 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip |
| 2026-08-06 | Audio Render 损坏 Instrument 绑定 canonical 透传 | Whole Mix 不再把强制 Error 降为未绑定 Info；Per Logical Track 为损坏绑定保留独立失败项且健康项可继续；Audio Render 33 tests；全仓基线 837 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；产物 `non-ui-release-gate-a4005a7bf4e84f48a372cf0382ed981c` |
| 2026-08-06 | Preview 临时 Project 上下文完整性 | Segment Preview 保留损坏 Instrument placeholder 并输出 MIDORA1305；Event Instrument/Segment Preview 保留相关有效 Library Folder，消除虚假 MIDORA1021；Compiler 193 tests；全仓基线 839 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；产物 `non-ui-release-gate-371f13d69cb84b519079f79318b3c5bf` |
| 2026-08-06 | Segment Preview 绑定前置条件 | 未绑定与普通断裂绑定在 SegmentPreview context 中产生 MIDORA1306 Error，Damaged 仍使用 MIDORA1305；失败 Preview 在 Backend Prepare 前被拒绝并释放 edit lock；Compiler 195、Playback 52 tests；全仓基线 842 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；产物 `non-ui-release-gate-8549557fbe94435298edb7be6545b2df` |
| 2026-08-06 | Compiler Int64 裁剪与 Loop 极值 | Note/Gate/Lifecycle/Template Note Off 先裁剪后加法；Project↔Content 差值优先换算；窗外 Event 先排除；Loop 以剩余量终止且 tail 饱和；Compiler 198 tests；全仓基线 845 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；产物 `non-ui-release-gate-ffea5b4bc45045d1b02e7a2dc163540c` |
| 2026-08-06 | Event Instrument Preview Int64 范围 | Gate/Template/Release 容器长度使用饱和加法，非法负时长仍交由统一语义诊断；Compiler 200 tests；全仓基线 847 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；产物 `non-ui-release-gate-7f2e646808c74d82a6d3dda3af502376` |
| 2026-08-06 | Tempo Sample Map Int64 反向换算 | 上界二分中点改为不溢出的 distance 分解，保持 decimal 累加与一次 AwayFromZero；Playback 53 tests；全仓基线 848 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；产物 `non-ui-release-gate-cfd2b382b66b4e238fccaaa1d74e9094` |
| 2026-08-06 | BASS 不同工作块逐 sample 确定性 | 固定 256-frame 原生 decode 序列；事件/硬结束前短块；预分配 staging；consumer/render 双位置；复杂 Tempo/Loop 106-event 回归；BASS 120 tests；全仓基线 849 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；进程内/子进程逻辑 WAVE hash 相同；产物 `non-ui-release-gate-4bfddba55a254b1da6a2b39350a7a33a` |
| 2026-08-06 | SubVoice 编辑器预览 Mute/Solo | 临时请求集合；多 Solo；Mute 优先；无 Solo 过滤；全 Mute 合法静音；重复/外部/单 SubVoice 混用拒绝；仍经完整 Preview canonical 管线；Compiler 203 tests；全仓基线 852 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；产物 `non-ui-release-gate-89b36a856e2a49059f81fd017bfc5960` |
| 2026-08-06 | Compiler 无序源容器确定性 | Global/Instrument/SubVoice Initial State、Reset Defaults、C# Mapping Context 字段、CompileContext Track/SubVoice 集合反向插入；全部正式结果逐字段等价；显式用户 List 顺序保持语义；Compiler 204 tests；全仓基线 853 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；产物 `non-ui-release-gate-101f16d56a684539bb404186989bbc72` |
| 2026-08-06 | Damaged Placeholder 稳定 ID 闭包 | Whole Project 纳入两类占位 ID；显式 Track 只纳入被选/被引用占位；正常对象碰撞、零、`nextStableId` 上界和未选隔离；Compiler 211 tests；全仓基线 860 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；产物 `non-ui-release-gate-86a76d7a595949789295a381823d306b` |
| 2026-08-06 | Library Folder 编译诊断作用域 | 显式 Track 只验证参与 Instrument 引用的 Folder 依赖闭包；Whole Project 保持全量；未选保留名称/重复 ID 隔离与参与 Folder 严格失败；Compiler 213 tests；全仓基线 862 tests | 完整发布门通过，0 warning / 0 error / 0 failure / 0 skip；产物 `non-ui-release-gate-c7ab8b35c36e489ab813d7508e09eba8` |
| 2026-08-07 | Q-NUI-002～Q-NUI-025 决策闭合记录 | 仅更新问题库与实施台账；Q-NUI-025 固定 JSON canonical integer、十进制文件名、protobuf `int64` 与直接修订开发期 v1；未修改源码、SRS、schema、descriptor、golden 或测试 | 全部现有问题均已回答；本轮未运行构建/测试，既有自动基线仍为 862 tests，不把未运行验证写成通过 |
| 2026-08-07 | 人工音频验收首轮快照 | M-AUD-001～012 产品所有者结果；M-AUD-002/005 音量突增；M-AUD-003 实例/音符间隔源码核对；M-AUD-007～009/011/012 完整 managed Worker 拒绝堆栈；M-AUD-010 端点与 0 B callback | 仅记录/分析，未修改代码且未运行新自动测试；完整快照见 `misc/Midora-Manual-Audio-Acceptance-Snapshot-2026-08-07.md` |
| 2026-08-07 | M-AUD-004～006 控制台指标补录 | Beats Flex、48 kHz；三项 callback/render-thread allocations 均为 0 B、underruns=0、callback fault=False、renderer fault=None；原始输出追加到同日快照 | 004/006 完整通过；005 技术指标通过但听感失败保持；仅文档补录，未修改代码或运行新测试 |

## 7. 未解决风险

- 当前领域模型和编译器虽已有大量覆盖，但尚未逐条证明 §7～§12 全矩阵完成。
- 当前 `.midora` 已支持完整 Event Instrument/Logical Track 对象图、完整性正常的 Embedded SF2，以及 Q-NUI-001 规定的损坏资源结构化修复门。Q-NUI-002 已确认初版 v1 不支持不存在的历史格式，迁移注册表为空是正式结果；NUI-08 的主要剩余工作转为 Q-NUI-024/Q-NUI-025 的开发期 v1 ID 契约重写及其完整回归。
- External/Embedded SF2 已接入 Project 打开后的运行时可用状态；External 缓存键包含 Windows 文件身份，播放/预览启动前同步复核 stamp，监控失效会自动 Stop 当前播放。WPF composition 尚需在 UI 阶段构造并展示该非 UI 会话；Embedded 选择 History 已按 Q-NUI-005 确认规则，尚待实施。
- §12.21 的 Segment checkpoint、dirty range 与 state-hash 收敛模型已替换旧 Track 整片段缓存；当前剩余风险是继续扩大随机 Project 生成器和 §7～§12 全语义组合矩阵，而不是已知的增量架构缺口。
- Project MIDI Export Settings 仍是空 v1 占位；Q-NUI-003 已确认字段/defaults 方案并明确直接修订开发期 v1，不创建 v2 或迁移器。实现与完整持久化/History/任务默认测试尚待完成。
- 十三批不分配稳定 ID 的结构/设置/音乐内容编辑命令已接入统一 History；Project Metadata/Track Color、External SoundFont 与安全的 Logical Parameter Definition/Mapping/Target Settings 属性已覆盖。Q-NUI-009 的 Lane 原子迁移与 Q-NUI-005 的 Embedded SoundFont、创建/复制/分割规则均已确认但尚待实施；它们还必须适配 Q-NUI-024 的单 `long` ID。
- Audio Render 的 canonical/输出事务、正式 Native AOT 文件链、应用级单音频任务锁和开始渲染前自动 Stop 已完成；不同工作块的复杂 BASS 输出已达到逐 sample 一致。2026-08-07 首轮人工结果发现 M-AUD-002/M-AUD-005 第一、第三、第四和弦末尾音量突增；CC11 在实例末尾 Reset 到 127 并放大仍可听 SF2 release 是高可信推断，尚待 canonical/PCM 自动复现确认。
- M-AUD-003/M-AUD-006 中两个实例范围 `[0,2400)`/`[2400,4800)` 首尾相接，但当前示例事件会在跨实例边界产生 140 tick（约 194 ms）可听音符空隙；该听感符合当前构造，人工清单措辞需要后续澄清。
- M-AUD-007～009/011/012 尚未测试目标行为：Console `GetWorkerPath` 返回 managed Worker `.dll`，被正式 Native AOT `.exe` 保护门在播放前拒绝。必须修复验收入口后复测，不能把这些堆栈误报为子进程音频或设备故障处理失败。
- M-AUD-004～006 已在 Beats Flex 48 kHz 完成进程内听感与技术指标：三项 callback/render-thread allocation 均为 0 B、underrun 为 0、无 callback/renderer fault；004/006 完整通过，005 仍因音量突增失败。M-AUD-010 同端点通过，27 callbacks、callback allocation 0 B、无 fault。实际设备切换/移除和其余人耳失败不能只凭无设备 CI 结论替代。
- 共享控制 ABI v1 已闭合字段/ring 损坏边界，但整组状态快照仍可能跨两次同状态发布混合；Q-NUI-011 已确认升级内部 ABI v2，具体 seqlock/双缓冲协议及回归尚待实施，不能把逐字段原子误报为整快照原子。
- 非 UI 发布门已经能生成并测试本地 Native AOT Worker 产物；Q-NUI-013 已给出产品层含 DLL 分发放行，但发布当日官方条款复核、供应商原始许可文本打包及实际产物验收尚未发生。当前本地测试产物仍不是可直接上传的正式发行包。
- 单应用实例所有权和启动 IPC 已形成 WPF 无关的运行时组件；Q-NUI-014 已确认按交互登录 Session 隔离，主窗口激活与 Project Switch Guard 接线属于后续 UI composition。
- Save/Save Copy 已通过统一非 UI 协调层绑定 package 事务、打开会话工程时间、current path/file information 与 Document 保存基线；Q-NUI-015 已确认同路径拒绝策略，WPF Progress/Result 接线待 UI 阶段。
- New Project 已能在旧 Project 之外构建完整候选，覆盖 Unsaved/Create and Save 及 External/Embedded SF2；WPF 后续只负责在 Project Switch Guard 成功分支提交候选并导航 Arrangement，不能提前启动工程时间或暴露半创建候选。
- Open Project 已能在旧 Project 之外严格建立候选并绑定恢复/损坏/资源状态；Q-NUI-002 已确认没有受支持的旧格式成功迁移分支，WPF 后续只能在 Project Switch Guard 成功分支接管候选资源并启动打开会话。
- Bar:Beat:Tick 的 1-based Bar/Beat、0-based Tick 与分母拍单位已经由 SRS 固定；Q-NUI-019 已确认中途 Time Signature 变化立即截断旧小节并开启新 Bar，且每次这种截断产生 Warning。换算服务、诊断稳定排序和边界/往返测试尚待实施。
- 显式 Track 编译已按 §12.6.3 将普通语义与 Stable ID 诊断统一限制到选择作用域；Whole Project compile 仍是发现未选内容损坏的正式入口，后续消费者不得自行扩大诊断范围。
- 正常 Track 对 Damaged Event Instrument 占位的绑定已按 §16.19.3 作为编译 Error；普通断裂引用仍按未绑定 Track 的 Info 语义保留，消费者不得把两者合并成同一级别。
- Q-NUI-020 已确认 Global Event Scope Defaults 在初版保持不可编辑的版本化空 marker；现有固定事件作用域继续有效，不新增 schema/History/compiler 配置分支。后续需把该产品解释写入正式规格，消除 SRS “可修改 Project Content”的字面冲突。
- Q-NUI-021 已确认 SMF 单个 delta 超过 `0x0FFFFFFF` 时保持结构化失败且不发布文件；不得向 canonical 事件之外插入 Meta spacer。现有实现仍需按决定补 exporter/task 级完整边界证据。
- Q-NUI-022 已确认 held Preview 的因果 Gate、`Int64.MaxValue` 未结束哨兵和未渲染 frontier 规则；held Preview 尚未实现，不能用裸 MIDI、猜测 Gate Length 或回写已缓冲 PCM 冒充。
- Q-NUI-023 已选择备选 A：把开发期 v1 的领域、创建和 schema 全部收窄到 `1..32767` 并拒绝高 TPQ v1；当前代码仍接受到 `Int32.MaxValue`，因此实现、descriptor/schema/golden 和 32767/32768 边界回归尚待完成。
- Q-NUI-024/Q-NUI-025 已确认 `MidoraId`/Project allocator 改为单个正 `long`，并固定 JSON canonical integer、十进制文件名和 protobuf 标量 `int64`。这与当前 `Guid`/`UInt128`、32 位十六进制、protobuf high/low 及现行 SRS 明确冲突；后续必须先同步修订 SRS/ADR，再一次性实施开发期 v1 契约与代码全链，不能继续把 128-bit 表示视为冻结发布承诺。
- Audio Render 对损坏 Event Instrument 绑定的选择已交还 canonical 编译器判定：Whole Mix 整体失败，Per Logical Track 仅对应项失败并允许其他项继续；普通未绑定/断裂引用仍保持 Info 与无输出目标语义。
- Preview 临时 Project shell 已复制所选对象所需的损坏绑定身份与有效 Library Folder 结构；后续新增任何 Project 外壳式 CompileContext 时都必须审计同类引用闭包，不能让临时上下文制造虚假断裂或降级真实损坏。
- Segment Preview 的绑定要求严于主时间线的一般未绑定 Track 语义：`MIDORA1306` 只属于该 Preview context；Playback Preview 必须先验证 canonical 可消费性，再初始化 Backend。
- 合法但靠近 `Int64.MaxValue` 的 Segment/Note/Loop 源数据必须按硬边界先裁剪再做 Project-domain 加法；不能依赖“先 checked 溢出、再取 Min”的实现顺序。
