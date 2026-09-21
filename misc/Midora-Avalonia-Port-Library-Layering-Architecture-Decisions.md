# Midora Avalonia 移植的库分层架构决策（ADR）

状态：已记录；其中 ADR-PORT-LAYER-01/02 为产品所有者 2026-09-21 明确决定，其余为待确认建议。
适用范围：`src/midora-avalonia/*`、被其复用的核心库，以及 WPF 冻结约束。
上位规范：`misc/Midora-SRS-Initial-Release-v0.1` 与根 `AGENTS.md`。本文是实现决策，不是需求规范；冲突时以 SRS 为准。

## 0. 背景与依据

- 2026-09-21 开始执行 A 级接线（Slice M1）：`Midora.Avalonia` 首次引用 `Midora.Application`，落成真实工程会话、`.midora` 保存/打开、真实编译与真实诊断。
- 实测发现：`Midora.Application` 的 `ProjectReference` 闭包含 `Midora.Playback`、`Midora.Audio`、`Midora.AudioRender`；该闭包全部是平台中立的托管程序集（BASS/原生只存在于 `Midora.Audio.Bass`、`Midora.Playback.BassWasapi`、`Midora.Audio.Bass.Worker`），但一个只想存盘的消费者仍会编译整条音频抽象链。
- `Midora.Avalonia.Presentation` 与 `Midora.Desktop.Presentation` 是逐文件移植的副本，已出现分叉风险；其中相当一部分内容不依赖任何 UI 框架。
- 产品所有者决定（2026-09-21）：**WPF 源码冻结，不改动**；WPF 未来可能被弃用，因此不为 WPF 做迁移、同步或重构投入。

## 1. Requirement trace（本轮分层工作）

### 1.1 输入

- 既有平台中立库：`Midora.Domain`、`Midora.Compiler`、`Midora.Midi`、`Midora.Persistence`、`Midora.MidiExport`、`Midora.AudioRender`、`Midora.Playback`、`Midora.Audio`、`Midora.Common`、`Midora.Mapping.Contract.V2`。
- 既有 WPF 消费者：`Midora.Desktop`、`Midora.Desktop.Presentation`（冻结）。
- Avalonia 消费者：`Midora.Avalonia`、`Midora.Avalonia.Presentation`（可改）。

### 1.2 正式输出

- 新库 `Midora.Presentation.Core`：由 `Midora.Avalonia.Presentation` 中**无 UI 框架依赖**的投影、策略、几何与栅格代码组成，可被任何宿主（Avalonia、将来的 CLI、测试）引用。
- `Midora.Avalonia.Presentation` 只保留 Avalonia 控件、Skia 执行、位图上传与框架适配。
- `ProjectSessionHost` 等"应用会话门面"移出 UI 程序集。
- 对话框的选项校验规则下沉为可复用 validator。
- 开发/评审钩子不随正式产物分发。

### 1.3 边界

- WPF 源码（`src/midora-desktop/**`）必须保持字节不变；WPF 只允许被"传递引用变化"影响，且该影响必须经构建与既有测试验证。
- 抽取不得引入第二套业务语义：`Midora.Domain`/`Compiler`/`Persistence` 仍是唯一事实来源。
- 不得把 UI 框架类型（Avalonia、WPF、Skia 的绘制对象）放进共享库；共享库可产出像素缓冲，但不得产出框架位图对象。
- 不允许以"抽取"为名复制核心逻辑；重复实现应删除而不是搬家。

### 1.4 失败条件

- WPF 源码出现任何修改。
- WPF 构建或 `Midora.Desktop.Presentation.Tests`/`Midora.Desktop.Tests` 回归失败。
- 共享库引用了 `Avalonia*` 或 `System.Windows*` 类型。
- 正式产物包含评审钩子/窗口目录（如最终发布口径要求排除）。

### 1.5 诊断

- 分层改动必须给出：哪些类型移动、移动前后命名空间、引用方变化、WPF 闭包是否变化。
- 任何"暂不拆"的结论必须写明触发条件，不得只写"以后再说"。

### 1.6 持久化归属

- 分层不改变 `.midora` 内容、格式版本或 fingerprint；共享库不得接触持久化格式的内部结构。

### 1.7 运行时归属

- 共享库不得持有窗口、控件、Dispatcher 或平台句柄；不得要求在 UI 线程调用。
- Avalonia 侧会话状态（工作区、选中、视图）仍属宿主，但其中"可选中的模型/策略"（如工具策略、播放跟随策略、值轨迹采样）属共享库。

### 1.8 明确非目标

- 本轮不迁移或改写 WPF 界面、不为 WPF 建立共享适配器。
- 本轮不重命名/移动 `Midora.Domain`、`Midora.Compiler`、`Midora.Midi`、`Midora.Persistence` 的公共类型。
- 本轮不决定 macOS 音频后端（属 B 级，另立 ADR）。
- 本轮不引入跨进程/多 UI 线程架构。

## 2. 决策

### ADR-PORT-LAYER-01（已确认）：WPF 冻结

- `src/midora-desktop/**` 的源码、XAML、测试在本轮分层工作中不动。
- 允许的影响只有两类：① WPF 引用的库新增传递引用；② 类型在程序集间移动但保留原命名空间，使 WPF 源码不变。
- 两类影响都必须通过 WPF 侧构建与既有测试验证；若某方案无法满足，则该方案不采用。
- 理由：WPF 可能弃用，为弃用候选做迁移投入无回报；冻结还能把改动面限制在 Avalonia 侧。

### ADR-PORT-LAYER-02（已确认）：共享库只服务 Avalonia，接受 WPF 副本继续存在

- `Midora.Desktop.Presentation` 与 `Midora.Avalonia.Presentation` 的重复不再追求合并；新的 `Midora.Presentation.Core` 只被 Avalonia 引用。
- WPF 侧的同类代码保持原状，明确不再同步；将来 WPF 弃用时随之退役。
- 理由：合并两侧会要求 WPF 改动与双端回归，与 ADR-PORT-LAYER-01 冲突且无收益。

### ADR-PORT-LAYER-03（建议待确认）：新增 `Midora.Presentation.Core`

- 内容（从 `Midora.Avalonia.Presentation` 迁出，命名空间保持 `Midora.Avalonia.Presentation.*` 或改为 `Midora.Presentation.*` 需确认）：
  - 纯逻辑/策略：`TimelineTickMath`、`TimelineGridPresentation`、`TimelineIntervalIndex`、`Interaction/TimelineToolPolicy`、`TimelineSelectionActions`、`TimelinePlaybackFollowPolicy`、`TimelineValueTraceSampler`、`CompressedMidoraIdSet`、`WorkspaceState`（选择/快照部分）、`ConductorTimelineProjection`、`TimelineValueAxisTick`、`PianoKeyPresentation`、`TimelineAccentPalette`。
  - 栅格/缓存：`TimelineRasterCache`、`TimelineSegmentPreviewRasterizer`、`TimelineTempoTileRasterizer`、`TimelineConductorMetaRasterizer`、`TimelineOnionSnapshot`、`TimelineStepSignal`（仅产出像素/命令数据）。
  - 投影模型：`TimelineRenderModel`、`TimelineShapeBatch`（命令列表）。
  - 契约：`ITimelineEditHost`（或其纯数据版本）。
- 不迁出：所有 `Controls/*`、`TimelineShapeDrawOperation` 的 Skia 执行部分、`PixelBufferBitmap`、`DpiScale`、`CancelablePresentationDispatch`、`DispatcherShutdownState`、`EmptyTimelineSource`/`DemoTimelineSource`（属宿主/评审数据）。
- 依赖约束：只允许依赖 `Midora.Domain`（如需要）与 BCL；不得依赖 `Avalonia*`、`SkiaSharp`、`Midora.Avalonia*`。
- 待确认：库名与命名空间；是否连带把 `ProjectSessionHost` 一并放入（见 ADR-PORT-LAYER-04）。

### ADR-PORT-LAYER-04（2026-09-21 已实施，方案 A）：`ProjectSessionHost` 移出 UI 程序集

- 现状：原 `Midora.Avalonia/Session/ProjectSessionHost.cs` 无 Avalonia 依赖，纯 Application/Persistence 适配。
- **已实施**：新建 `src/midora-core/Midora.Session`（`net10.0`，引用 `Midora.Application`），把 `ProjectSessionHost` 与 `ProjectActivation` 移入并公开；`Midora.Avalonia` 改引该库，`ShellSession` 只加 `using Midora.Session`。既有库（含 `Midora.Application`）零修改。
- 命名说明：库名/命名空间暂定 `Midora.Session`；若后续确定 `Midora.Application.Session` 等命名，改名成本仅为项目名与 `using`（尚未发布）。
- 后续（ADR-05）validator 也放本库，以避免修改既有 `Midora.Application`。

### ADR-PORT-LAYER-05（建议待确认）：对话框校验下沉，但不是本轮全部

- 需要下沉的规则：导出选项（模式/路由/tick 范围/子集）、音频渲染选项（采样率 8k–192k、voices、范围）、量化网格与 Humanize 边界、Split 表达式与 Batch Create 候选上限、偏好约束（含 SoundFont 列表与目标映射）、轨道路由模式校验。
- 目标形态：在 `Midora.Session` 中新增 options + `Validate`（返回结构化错误），UI 只绑定与显示；这样连 `Midora.Application` 也不必改动（2026-09-21 起执行）。
- 表达式相关（Mapping ABI v3、Batch 表达式、Split 表达式）必须调用 `Midora.Compiler`/`Midora.Mapping.Contract.V2`，禁止在 UI 复刻语法检查。
- 边界：WPF 自己的校验保留，不同步。

### ADR-PORT-LAYER-06（建议待确认，暂缓）：`Midora.Application` 的音频闭包拆分

- 现状证据（2026-09-21）：146 个文件中只有 5 个触及音频命名空间——`ProjectDocumentSession`、`ProjectOpenCoordinator`（都只因 `ProjectCompilationSession` 类型）、`ApplicationPreferences`（音频缓存偏好）、`ApplicationSessionStorageCleanup`（音频缓存清理）、`ApplicationTaskCoordinator`（预览/渲染任务类型）。
- 结论：真正卡点是 `ProjectCompilationSession` 位于 `Midora.Playback` 且本身使用 `Midora.Audio` 的存储/计划类型；要得到"无音频闭包"必须先拆编译会话与音频缓存计划，属于跨 WPF 与核心测试的重构。
- 决策建议：**暂不执行**。当前代价仅为多编译若干平台中立托管程序集（不含原生 BASS），收益不足；且 WPF 可能弃用。
- 重新评估的触发条件（满足任一）：
  1. WPF 正式退役，可无顾虑移动类型；
  2. 出现非桌面正式消费者需要无音频闭包（CLI/测试宿主/插件）；
  3. 发布裁剪或构建时间成为实际瓶颈；
  4. 有人试图把平台专属 API 加进 `Midora.Audio`/`Midora.Playback`（此时应立即拆）。
- 若执行：保留 `Midora.Application` 作为聚合/兼容程序集（保留命名空间），Avalonia 改引 `Midora.Application.Core`；WPF 源码不变，靠传递引用与 WPF 构建/测试验证。

### ADR-PORT-LAYER-07（建议待确认）：评审/开发工具与产物分离

- 现状：`MIDORA_*` 环境变量钩子、`WindowCatalog`（44 个窗口目录）、`RunShellSmokeAsync`/`RunWindowSmokeAsync`、`DemoTimelineSource`、预览窗口与占位默认数据都在产品程序集内。
- 选择（待确认）：① `#if DEBUG` 条件编译；② 独立 `tools/Midora.DevHarness` 项目 + `InternalsVisibleTo`；③ 保持现状并在发布说明中标注。
- 优先级低于 ADR-PORT-LAYER-03/04。

### ADR-PORT-LAYER-08（建议待确认）：`Midora.Common` 暂不拆平台

- 现状：`MidoraProgramData.EnsureReadyAndProbe` 的固定盘判定与事务探针已在 macOS 实测通过（`Data/`、`.tmp/` 建立成功），无需为本轮改动。
- 记录：`Midora.Common` 内的 Windows 语义（`WindowsOutputFileNamePlanner`、`MidoraWindowsApplicationIdentity`、CP932 回退、单实例 `Mutex`/`NamedPipe`）在出现 macOS 等价需求时，新增 `Midora.Common.Mac` 或 `Midora.Platform.Mac`，不修改既有类型。
- 输出命名按 SRS 是产品规则（Windows 安全文件名），即使运行在 macOS 也保持既有计划器语义。

## 3. 既有库需要改什么（除 Avalonia 外）

| 库 | 本轮是否改 | 具体内容 | WPF 影响 |
|---|---|---|---|
| `Midora.Application` | **不改**（2026-09-21 起） | ADR-04 改走新库；ADR-05 的 validators 改放 `Midora.Session` | 无 |
| `Midora.Playback` | 暂不改 | ADR-06 若执行才拆 `ProjectCompilationSession` | 无（暂缓） |
| `Midora.Audio` / `Midora.AudioRender` / `Midora.MidiExport` / `Midora.Compiler` / `Midora.Persistence` / `Midora.Midi` / `Midora.Domain` | 不改 | — | 无 |
| `Midora.Common` | 暂不改 | ADR-08 | 无 |
| `Midora.Desktop*`（含 WPF 测试） | 不改 | ADR-01/02 | 无（冻结） |
| 新 `Midora.Presentation.Core` | 新建 | ADR-03 | 无（WPF 不引用） |
| 新 `Midora.Session` | **已新建并接入** | ADR-04-A | 无 |

## 4. 验证门

- 每次类型移动后：`midora-core.slnx`、WPF `Midora.Desktop`、`Midora.Application.Tests`、`Midora.Desktop.Presentation.Tests` 全部构建通过。
- Avalonia 侧：`midora-avalonia.slnx` Debug/Release 0 警告 0 错误；`SHELL-SMOKE`/`WINDOW-SMOKE` 全通过。
- 共享库检查：`rg 'Avalonia|SkiaSharp|System\.Windows' src/<新库>` 必须为空。
- 行为门：移动类型不改变 `.midora` 字节、fingerprint、诊断码或编译结果（用 Slice M1 的保存/编译冒烟与 golden 测试核对）。

## 5. 未决问题（需要确认）

1. ADR-PORT-LAYER-03：新库名与命名空间用 `Midora.Presentation.Core` + `Midora.Presentation.*`，还是保留 `Midora.Avalonia.Presentation.*`？
2. ADR-PORT-LAYER-04：已于 2026-09-21 按方案 A 实施（新库 `Midora.Session`）；库名命名空间如需调整仍可低成本改名，待确认。
3. ADR-PORT-LAYER-05：validators 的返回形态（异常 vs 结果对象）与是否要求 WPF 未来可选采用。
4. ADR-PORT-LAYER-07：评审钩子/窗口目录按①/②/③哪种处理。
5. ADR-PORT-LAYER-06 的触发条件是否接受；是否要把"`Midora.Audio`/`Midora.Playback` 保持平台中立"写成 CI 检查（例如禁止引入 `win-x64`/原生依赖）。
