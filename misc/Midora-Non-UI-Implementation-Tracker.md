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
| NUI-01 | 仓库级构建、测试和兼容基线 | 非 UI 自动门与单实例内核完成；WPF 入口接线待 UI 阶段 | §2、§3.3、§21、INV-019/027～030 | 单命令 Release 构建；全部自动测试；固定工具链/原生 manifest 门；唯一主实例与启动请求转发 |
| NUI-02 | 完整领域源模型与编辑事务 | History/Modified 与十三批非 ID 分配对象命令完成；ID 分配命令等待 Q-NUI-005 | §3～11 | Track/Instrument/Folder/Damaged/Segment、Conductor、Project Settings/Metadata、Track Color、External SoundFont、Note/Lane/Point、Instrument Lifecycle、SubVoice、Template Event、Value Curve、Initial/Reset State、Envelope、Mapping Function、Mapping Chain/Step，以及安全的 Logical Parameter Definition/Mapping/Target Settings 属性已覆盖；需要 Lane 迁移的 Definition 结构编辑等待 Q-NUI-009 |
| NUI-03 | Semantic Validation 与诊断来源 | 进行中 | §3～12 | 全错误/警告/Info 矩阵与稳定排序 golden；显式 Track 编译诊断作用域已闭合 |
| NUI-04 | Full/Incremental Canonical Compiler | 强增量模型完成；全 §12 矩阵继续扩充 | §12、INV-009/010/015 | Segment checkpoint + dirty range + state hash 已实现；固定种子连续编辑逐字段等价 |
| NUI-05 | Playback/Preview 非 UI 状态机 | 进行中 | §13、§19 | 全状态、自动 Stop、Mute/Solo、设备故障和重复生命周期测试 |
| NUI-06 | MIDI Export 完整工作流 | 进行中 | §14、§19 | 三模式、routing、README、冻结命名、多文件原子事务、自校验 |
| NUI-07 | Audio Render 完整工作流 | 自动化与 AOT 文件链完成；试听待验收 | §15、§19 | Whole/Per Track、采样率/长度/RIFF 边界、取消、独立发布、零分配 |
| NUI-08 | `.midora` 完整持久化 | 进行中 | §16、§19 | protobuf 对象图、损坏隔离、迁移、Embedded SF2、确定性与事务矩阵 |
| NUI-09 | BASS/BASSMIDI/WASAPI/Worker | 进行中 | §13、§15、INV-018～028 | ABI/版本/生命周期/设备/underrun/IPC/零分配自动门与硬件验收 |
| NUI-10 | 非 UI 应用任务协调与偏好 | 非 UI 核心完成；WPF composition 待 UI 阶段 | §3、§13～17、§19～20 | 单任务/锁级、自动 Stop、Project switch guard、New/Open Project 候选事务、Save/Save Copy 会话事务、结构化报告、Application Preferences 及缓存失效均有自动测试 |
| NUI-11 | 系统加固与发布门 | 自动发布门完成；压力/硬件/许可待闭合 | §21、INV-027/028/037/038 | 锁定 SDK/依赖、零 Skip 全测、AOT/hash/notices 已自动化；实时性能/硬件与 Q-NUI-013 许可放行仍待闭合 |

## 4. 当前已确认基线

- 2026-08-06 提交 `4b69e72` 完成 `.midora` 空对象图基础垂直切片；Release 构建通过，累计 330 个自动测试通过。
- 2026-08-06 提交 `61b7042` 完成 Event Instrument / Logical Track protobuf v1、对象级损坏隔离与可撤销删除、Embedded SF2 正常资源流式 package 链。
- `misc/Midora-SRS-Code-Conformance-Audit-2026-08-05.md` 记录的 1A～25.1A 均视为已确认决定，不再询问。
- 当前有 7 个未闭合的产品所有者重大决定：Q-NUI-002（初版 `.midora` 历史格式基线）、Q-NUI-003（Project MIDI Export Settings v2 字段/默认值）、Q-NUI-005（ID 分配型 Undo 和 `nextStableId`/Modified 关系）、Q-NUI-009（Logical Parameter Definition 结构变更的既有 Lane 迁移）、Q-NUI-011（共享音频状态快照 ABI v2 并发契约）、Q-NUI-013（正式 BASS 二进制分发许可放行）与 Q-NUI-019（小节中途 Time Signature 变化的 Bar:Beat:Tick 规则）；分别只暂停成功迁移器、Export Settings schema/领域默认分支、创建/复制/分割类 History 命令、需要迁移 Lane 的 Definition 编辑、共享状态快照协议升级、含 BASS DLL 的正式对外分发和 Project 音乐位置/网格换算服务。Q-NUI-004、Q-NUI-006、Q-NUI-007、Q-NUI-008、Q-NUI-010、Q-NUI-012、Q-NUI-014、Q-NUI-015、Q-NUI-016、Q-NUI-017、Q-NUI-018 是已按推荐方案落地、仍待确认的小决定。Q-NUI-001 已按推荐方案确认并实现。

## 5. 当前实施顺序

1. Q-NUI-002 回答后补 `.midora` 成功迁移器分支；未来版本预检和保存/打开故障注入已完成。
2. MIDI Export 三模式、Readme 和文件事务已形成可运行垂直切片；Q-NUI-003 回答后补 Project 默认设置。
3. Audio Render Whole/Per Track、SF2 快照、正式文件 Worker 协议、WAVE 校验、逐文件事务及 Native AOT 文件链已完成；试听归入 NUI-11 统一人工验收。
4. 用 Full Compile 作为 oracle，实现 SRS 强制的 checkpoint/dirty range/state hash 增量模型并建立属性测试。
5. 继续 NUI-02/NUI-03 的完整领域编辑命令、Undo/Redo、Modified 和诊断矩阵；其中 Q-NUI-002/003/005/009 对应分支继续等待决定。
6. 加固原生音频 Worker、WASAPI 硬件矩阵和发布门；最后生成统一人工试听/手动测试清单。

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

## 7. 未解决风险

- 当前领域模型和编译器虽已有大量覆盖，但尚未逐条证明 §7～§12 全矩阵完成。
- 当前 `.midora` 已支持完整 Event Instrument/Logical Track 对象图、完整性正常的 Embedded SF2，以及 Q-NUI-001 规定的损坏资源结构化修复门；旧格式迁移尚未实现，因此 NUI-08 仍未完成。
- External/Embedded SF2 已接入 Project 打开后的运行时可用状态；External 缓存键包含 Windows 文件身份，播放/预览启动前同步复核 stamp，监控失效会自动 Stop 当前播放。WPF composition 尚需在 UI 阶段构造并展示该非 UI 会话；Embedded 选择 History 仍等待 Q-NUI-005。
- §12.21 的 Segment checkpoint、dirty range 与 state-hash 收敛模型已替换旧 Track 整片段缓存；当前剩余风险是继续扩大随机 Project 生成器和 §7～§12 全语义组合矩阵，而不是已知的增量架构缺口。
- Project MIDI Export Settings 仍是空 v1 占位；schema v2 与默认值等待 Q-NUI-003，不能回写修改已发布 v1。
- 十三批不分配稳定 ID 的结构/设置/音乐内容编辑命令已接入统一 History；Project Metadata/Track Color、External SoundFont 与安全的 Logical Parameter Definition/Mapping/Target Settings 属性已覆盖，需要迁移既有 Lane 的 Definition 类型/Enum 结构/range 分支等待 Q-NUI-009，Embedded SoundFont 与创建/复制/分割类命令等待 Q-NUI-005。
- Audio Render 的 canonical/输出事务、正式 Native AOT 文件链、应用级单音频任务锁和开始渲染前自动 Stop 已完成；实时硬件压力与集中人工试听仍属于 NUI-09/NUI-11。
- 实际 BASS DLL、物理 WASAPI 设备、设备移除和人耳听音不能只凭无设备 CI 结论替代。
- 共享控制 ABI v1 已闭合字段/ring 损坏边界，但整组状态快照仍可能跨两次同状态发布混合；ABI v2 seqlock/双缓冲选择等待 Q-NUI-011，不能把逐字段原子误报为整快照原子。
- 非 UI 发布门已经能生成并测试本地 Native AOT Worker 产物，但这不是 BASS 重新分发授权；实际发布主体、收入、渠道、届时条款与供应商许可文本等待 Q-NUI-013，当前不得把本地测试产物作为正式发行包上传。
- 单应用实例所有权和启动 IPC 已形成 WPF 无关的运行时组件；当前按交互登录 Session 隔离，范围等待 Q-NUI-014 确认，主窗口激活与 Project Switch Guard 接线属于后续 UI composition。
- Save/Save Copy 已通过统一非 UI 协调层绑定 package 事务、打开会话工程时间、current path/file information 与 Document 保存基线；WPF Progress/Result 接线待 UI 阶段，Save Copy 自身路径策略等待 Q-NUI-015 确认。
- New Project 已能在旧 Project 之外构建完整候选，覆盖 Unsaved/Create and Save 及 External/Embedded SF2；WPF 后续只负责在 Project Switch Guard 成功分支提交候选并导航 Arrangement，不能提前启动工程时间或暴露半创建候选。
- Open Project 已能在旧 Project 之外严格建立候选并绑定恢复/损坏/资源状态；旧格式成功迁移仍等待 Q-NUI-002，WPF 后续只能在 Project Switch Guard 成功分支接管候选资源并启动打开会话。
- Bar:Beat:Tick 的 1-based Bar/Beat、0-based Tick 与分母拍单位已经由 SRS 固定，但拍号可位于任意 tick；中途拍号变化是否截断并开启新小节等待 Q-NUI-019，决定前不实现会影响全局网格/坐标的换算服务。
- 显式 Track 编译已按 §12.6.3 将普通语义与 Stable ID 诊断统一限制到选择作用域；Whole Project compile 仍是发现未选内容损坏的正式入口，后续消费者不得自行扩大诊断范围。
