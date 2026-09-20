# Midora macOS 移植与大规模时间线渲染技术栈 — Requirement Trace / 待决策问题

状态：**进行中（已定：回贡上游 + Avalonia 外壳 + Skia 渲染，C# 重实现；SRS/ADR 仍待提交，验收门待定）**
创建日期：2026-09-20
参与者：

- 上游 Midora 作者：Zacksony（公开仓库 `https://github.com/Zacksony/midora`）
- 第三方移植开发者：节能降耗（`yinhe` 作者，Rust + egui + wgpu 高性能 MIDI 编辑器，仓库 `/Users/jieneng/Documents/GitHub/yinhe`）
- 本记录由移植工作区维护，不修改上游 SRS、不修改源码、不运行发布

依据：

- SRS §1.4.1（目标平台）、§21.2（产品级排除项）、§21.3（实现自由度）、§21.4（范围变更规则）、§21.6（第三方许可发布门）
- `AGENTS.md` §1（事实来源与待决问题记录）、§3（初版范围护栏）、§4（音频后端约束）、§8（GPU backend 需另立 ADR）
- ADR-UI-018:125（CPU raster backend 可替换；GPU 需基准证明）、ADR-UI-020:143（GPU instance renderer 需新 ADR + 性能门 + 许可证审计）
- `misc/Midora-Extreme-Timeline-Performance-Requirement-Trace.md`（Midora 现有大规模实测）
- 外部对照：`/Users/jieneng/Documents/GitHub/yinhe`（只读调查；AGPL-3.0；其数据只作为参考事实，不构成 Midora 需求）

---

## 0. 事实分类（SRS 规定 / 源码现状 / 外部对照 / 设计推断 / 未决问题）

### 0.1 SRS 已规定（上游正式范围，不得静默改变）

- 初版固定 Windows Desktop、.NET 10、WPF、`win-x64`；不发布 x86、Arm64 或 AnyCPU（SRS §1.4.1、§21.2）。
- 初版明确排除 macOS、Linux、Web、移动端、跨平台 UI 框架（SRS `01-...:149-154`、`21-...:27-28`）。
- 范围变更必须走 §21.4：记录验证事实与失败条件 → 明确受影响条款 → 提出替代方案和用户可见影响 → 修订 SRS → 保持历史可追踪。
- BASS / BASSMIDI / BASSWASAPI 版本与 SHA-256 固定，仓库不提交 DLL；第三方分发前必须完成许可核验（SRS §21.6；`AGENTS.md` §4）。
- 渲染实现属于实现自由度（SRS §21.3:63）；但平台/RID、确定性、canonical 与音频语义不属于。
- 无 Enabled SoundFont 时允许打开、保存、编译和 MIDI 导出，只阻止播放/预览/音频渲染（`AGENTS.md` §4）→ **macOS 首阶段可以完全不做音频而仍形成可用闭环**。

### 0.2 Midora 源码/仓库现状（核实）

1. 时间线内容渲染已是「后台 CPU 软件光栅（Pbgra32 `byte[]`）→ 冻结 `BitmapSource` tile → UI 线程贴图」，带 LOD 聚合、分页数据源、256 MiB LRU、2～4 worker、过期结果丢弃（`Midora.Desktop.Presentation/Rendering/TimelineRasterCache.cs:67-80, 568-1086, 2564-3087`；ADR-UI-018/019/020/021）。
2. WPF 承重点集中在宿主层：`OnRender(DrawingContext)`、`BitmapSource` 交付、`DispatcherPriority.Render`、`DependencyProperty + AffectsRender`、`VisualTreeHelper.GetDpi`/`DpiScale`、`RenderOptions`（`Controls/TimelineSurface.cs:1930` 等）。
3. Midora 已有实测（`misc/Midora-Extreme-Timeline-Performance-Requirement-Trace.md` §7/§8）：真实 1800 万 Note SMF 导入 ≈16 s；180 万 Note Segment 冷帧 15～30 ms，`OnRender` content p50 0.33～0.87 ms；60,000 Note 选择编辑约 0.1～1.5 s。**Midora 侧尚无「1000 万音符整体视图/连续 pan-zoom」的公开基准**。
4. 平台耦合（macOS 影响面）：Desktop UI ≈100,925 行 C# + 8,191 行 XAML；实时音频唯一生产实现为 BASSWASAPI；Worker 为 win-x64 Native AOT + Job Objects + Windows-only 命名共享内存/命名管道；路径策略假定固定盘/大小写不敏感/Windows 文件名合法化；大量测试为 WPF STA/Windows-only。
5. 本机环境：`global.json` 固定 SDK `10.0.400` 且 `rollForward: disable`；本机只有 10.0.201/302 → 当前无法构建。用户已表示可升级 SDK。

### 0.3 外部对照：yinhe 渲染栈（参考事实，非 Midora 需求）

来源：`/Users/jieneng/Documents/GitHub/yinhe` 只读调查（AGPL-3.0，无 CLA/双许可）。

- 栈：Rust 2024 + `eframe/egui 0.36.1` + `wgpu 30.0.0`；macOS 是一等目标（CI 出 dmg，Metal 路径），另有 Android。
- 集成：编辑器把内容渲染到 offscreen texture，再经 `register_native_texture` + `painter.image` 合成进 egui（`crates/yinhe-egui/src/render_context.rs:183-233, 339-379`）；网格/键盘/标尺/文字/选框仍由 egui painter 绘制。
- 大规模音符路径：**语义实例 + per-key GPU compute cull（无 atomics、Hillis-Steele 前缀和）+ 4B 可见索引 indirect multi-draw + 由 ppu 选择的 LOD 摘要段**（`crates/yinhe-wgpu/src/cull.wgsl:1-198`、`cull/state.rs:644-730, 867-908`、`pianoroll/summary.rs:18-50`；实例布局 12B/音符见 `vertex.rs:47-157`）。
- 文档化数据（注意口径）：headless 渲染 pass 基准 1 亿音符全曲视图 755 ms → 1.32 ms；真实端到端（含 UI/音频，macOS/Metal）44M 音符播放 40～61 fps、33 万音符 61 fps（`docs/perf-investigation-plan.md:5-12`；commit 记录见调查）。
- 资源：per-key cull 约 `2 × notes × 16B`（1 亿 ≈3.2 GB），GPU 总预算 8 GiB；LOD 摘要把 1.64 亿音符显存从约 670 MB 降到约 450 MB。
- 与 Midora 的结构差异：yinhe 不做 tile bitmap 缓存，靠 GPU per-frame cull + LOD；Midora 靠 CPU tile 缓存 + 输出像素聚合。两者都能把每帧成本与音符数解耦，但 yinhe 路线在连续 pan/zoom、全曲视图上无 tile 失效/重光栅停顿。

### 0.4 设计推断（不是需求）

- 用户所说的「羸弱」是**架构对照判断**：Midora 的 CPU tile 光栅在 `10^7` 量级、中等缩放连续 pan/zoom/整体视图下，会受 tile 失效与 CPU 光栅吞吐限制；yinhe 的 GPU instancing 路线在同一场景下没有这类停顿。这是否构成 Midora 需要换渲染栈的理由，取决于目标规模与验收门（见 D-PERF01/D-RENDER01）。
- Avalonia 是与现有 WPF 代码形状最接近的跨平台 .NET UI 栈；迁移主要是宿主控件与平台服务，tile/数据层可复用。Avalonia 11.x 在 macOS 上基于 Skia + Metal（`CAMetalLayer`），自定义绘制可用 `Control.Render` / `ICustomDrawOperation` / `ISkiaSharpApiLease` 直接操作 Skia（外部事实，需 spike 复核）。
- 对 GPU 渲染有两条现实路线：单语言（Avalonia + Skia GPU，无 compute，需 CPU cull）与双栈（Avalonia 外壳 + 自研 Rust/wgpu 渲染器经 native interop 嵌入，wgpu 可跨 Metal/D3D12/Vulkan）。后者能复用用户既有专长与 yinhe 已证明的思路，但不能复用 AGPL 代码，且引入第二工具链/FFI/输入与 DPI 桥接成本。

### 0.5 未决问题

见 §3。在回答前不实施 macOS/GPU/UI 栈改动；本记录不修改 SRS。

---

## 1. 用户输入原始记录（2026-09-20）

### 1.1 用户明确回答（要点）

1. 用户不是原开发者，而是**准备开始移植的第三方开发者**（yinhe 作者）。
2. .NET SDK **可以升级**，用户尚未升级本机 SDK。
3. 「羸弱」是**相比 Rust wgpu（`/Users/jieneng/Documents/GitHub/yinhe`）而言**，不是对 Midora 现有实现的实测回归结论。
4. 已 fork 上游并计划「第一步在 macOS 上跑通」；倾向切 Avalonia；自认最难的可能是 BASSMIDI，上游作者确认音频后端是独立 worker 程序、BASSWASAPI 需替换。

### 1.2 用户提供的聊天记录（原文照录）

> 节能降耗: 09-20 14:22:32
> 但我真想偷看你仓库
>
> 节能降耗: 09-20 14:24:20
> 可以吗可以吗可以吗（星星眼
>
> Zacksony: 09-20 14:24:21
> 公开了
>
> Zacksony: 09-20 14:24:23
> https://github.com/Zacksony/midora
>
> 节能降耗: 09-20 14:24:45
> 好耶
>
> 节能降耗: 09-20 14:35:09
> fork一份，我自己捣鼓捣鼓
>
> 节能降耗: 09-20 14:35:26
> 第一步是在macOS上跑通
>
> Zacksony: 09-20 14:36:51
> 加油？
>
> 节能降耗: 09-20 14:37:03
> 嗯
>
> 节能降耗: 09-20 14:37:12
> 我网好卡我还在clone
>
> Zacksony: 09-20 14:37:41
> wpf好移植吗
>
> 节能降耗: 09-20 14:38:01
> 没什么不好移植的吧
>
> 节能降耗: 09-20 14:38:12
> 不如说是目标平台好移植吗
>
> 节能降耗: 09-20 14:38:25
> 比如macOS难不难，Avalonia难不难
>
> Zacksony: 09-20 14:38:38
> wpf不是windows only吗
>
> 节能降耗: 09-20 14:38:51
> 所以要切换成avalonia
>
> Zacksony: 09-20 14:38:57
> [惊讶]
>
> 节能降耗: 09-20 14:39:04
> 让我搓一下
>
> Zacksony: 09-20 14:39:05
> 大工程
>
> 节能降耗: 09-20 14:39:23
> 其实最难的都已经有了
>
> 节能降耗: 09-20 14:39:33
> 有源代码，很好搬的
>
> 节能降耗: 09-20 14:40:52
> 我两眼一瞄，最难的可能是BASSMIDI
>
> Zacksony: 09-20 14:41:15
> 我用了basswasapi，这块也得换掉
>
> Zacksony: 09-20 14:42:22
> 不过音频后端是独立的worker程序，好说
>
> 节能降耗: 09-20 14:42:37
> 比外接OmniMIDI好多了
>
> 节能降耗: 09-20 14:48:55
> 啊，看到了MD文档想起了我的公司项目
>
> 节能降耗: 09-20 14:48:58
> 比我严谨多了
>
> Zacksony: 09-20 14:50:28
> ai生成的
>
> Zacksony: 09-20 14:51:09
> ai看ai生成的约束写ai代码

### 1.3 2026-09-20 追加回答（用户对本记录问题的直接回复，原文）

> 1.打算回贡，因为大家都希望得到更强的性能
> 2.计划使用Skia，这个最贴近C#技术栈
> 3.不用管许可证，我允许了，yinhe没有任何其他rust代码的贡献（其他人唯一的贡献是改了一行toml），反正这个项目我觉得不会有Rust代码

对回答的准确含义（不改变原话）：

- **回贡**：移植目标是向上游 `Zacksony/midora` 提交，不只是个人 fork。
- **Skia**：时间线 GPU 渲染用 Skia（经 Avalonia 的 Skia 集成），不引入 Rust/wgpu 渲染器；即以 C# 重实现高性能渲染架构。
- **许可证**：用户声明其为 yinhe 全部 Rust 代码的版权人（他人唯一贡献为一行 toml），并允许使用；且 Midora 侧不会包含 Rust 代码。本记录按「不跨语言复制 AGPL 源文件，只以 C# 重实现架构思路」执行；若将来真要复制任何 yinhe 源文件，需在 D-LIC01 显式记录再许可。

---

## 2. 需求追踪（本记录自身）

- 输入：用户问题与回答；上游 SRS §1.4/§21；AGENTS.md；Midora 现状；yinhe 外部对照。
- 正式输出：事实分类、编号待决策问题、候选方案、分阶段建议。
- 不产生：SRS 修改、源码修改、格式变更、发布产物、测试结论。
- 失败条件：把 yinhe 的基准当 Midora 验收门；把建议当决定；在 D-LIC01 未定前复制 AGPL 代码。
- 非目标：改变 Domain/Compiler/canonical/音频语义；讨论 MIDI/格式变更。

---

## 3. 待决策问题

> 每题「建议」不等于决定；「用户回答」「确认状态」随用户回复更新，不得改写用户原话。

### D-MAC01：移植性质与范围（已答核心项，待上游确认）

- 已确认（用户 §1.1、§1.3）：以 fork 推进、**计划回贡上游**、第一步「在 macOS 上跑通」、SDK 可升级、目标是更强性能。
- 由「回贡」推导出的必然后续（记录，不是新问题）：
  1. macOS 进入上游范围需要按 SRS §21.4 提交范围修订，并需上游产品所有者（Zacksony）正式确认；用户当前意图不能替代该确认。
  2. UI 框架切换（WPF → Avalonia）与 Skia GPU 渲染需新 ADR；GPU renderer 另需性能门与许可审计（ADR-UI-020:143）。
  3. 上游长期必须保持 Windows 可用；「回贡」不应以牺牲 Windows 现有行为为代价。
- 仍需决定（用户/上游）：
  1. macOS 进入哪个版本（1.x 追加 / 2.0 / 未定），Windows 是否仍为一等平台？
  2. macOS 架构：仅 `osx-arm64`，还是 `osx-x64` 并存？
  3. 首阶段是否接受「无音频」的 macOS 闭环（打开/编辑/保存/编译/MIDI 导出），音频后置？
- 建议：fork 侧先按「无音频闭环 → 性能验证 → 音频」推进；SRS 修订、ADR 与上游确认在首次回贡 PR 前完成。
- 确认状态：fork 侧可继续；上游合并待产品所有者确认

### D-RENDER01：时间线渲染栈（已定 Skia，架构待 ADR）

- 已确认（用户 §1.3）：**Avalonia 外壳 + Skia 渲染**；以 C# 重实现，不引入 Rust/wgpu 渲染器。
- Skia 能力边界（事实，spike 需复核）：
  - Skia 是 2D 光栅 API，**没有 compute shader，也没有逐实例属性/indirect draw**；yinhe 的 GPU compute cull + indirect multi-draw 无法在 Skia API 层一一对应。
  - C# 侧对应手段：CPU cull（复用 Midora 现有区间索引/分页源，`O(log n + visible)`）后构建 GPU-ready 几何，再用 `SKCanvas.DrawVertices`（每音符 4 顶点，坐标/颜色烘焙）或 `SKCanvas.DrawAtlas`（共享 sprite + 每实例 `RSXform`/颜色）批量绘制；几何可在后台 worker 预构建并缓存，渲染帧只做 canvas transform。
  - Avalonia 集成：自定义绘制经 `ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature`/`ISkiaSharpApiLease` 取 GPU Skia canvas；`SKVertices`/atlas 对象的 GPU 资源复用与缓存行为需 spike 验证。
  - 缩小视图下限：沿用 Midora 现有「device px/tick ≤ 0.125 时按输出 device column 聚合」；若仍不足，再补 yinhe 式固定 tick 块 LOD 摘要（`1024/256/64/16` 量级），属于新增表现层缓存。
- 提议的目标架构（提案，不是已批准设计）：
  1. 保留：canonical 只读输入、区间索引、分页/revision 快照、CPU 命中与编辑路径、SRS 24.11 的预览层规则。
  2. 替换：tile bitmap 光栅器 → per-chunk GPU 几何构建器（顶点/atlas），后台 worker + 有界 LRU + 局部失效；cache key 继续包含语义指纹/投影 key/DPI/颜色身份。
  3. 渲染：Avalonia 自定义 draw op 内按 transform 一次绘制可见 chunk；网格/标尺/文字/光标/选框等 chrome 仍由 Avalonia DrawingContext 绘制（与 yinhe「egui 管 chrome、wgpu 管音符」分层一致）。
  4. 大选区/播放光标等 overlay 不进几何缓存，仍走独立层。
- Spike 验收（用户 2026-09-20 决定**暂缓性能门**：「现在暂时不用想性能，Skia 用得巧妙就没问题」）：先验证正确性/可用性/架构（S1/S2/S3 作为候选实现路径保留）；同数据对照 yinhe 的帧时/首次可用/内存测量推迟到正式回贡或实测出现问题后补。
- 失败条件：若 S2/S3 在目标门内不达标且确需 compute/indirect，另立 ADR 讨论原生 Metal/wgpu 旁路；默认不引入第二工具链。
- 确认状态：**已答（Skia）**；架构细节待补

### D-STRUCT01：Avalonia 解决方案与共享呈现层的结构（待确认）

- 场景：用户提议在 `src` 下新增解决方案 `midora-avalonia`。目前 `src/midora-desktop` 为 WPF 专用（`Midora.Desktop`、`Midora.Desktop.Presentation` 均 `net10.0-windows` + `UseWPF`）；`Midora.Desktop.Presentation` 约 24,737 行，渲染数据层无 WPF 引用，但光栅器/放置计算大量使用 `System.Windows.Media.Color`、`Rect`、`Point`、`DpiScale`。
- 建议（提案）：
  1. **新增独立解决方案** `src/midora-avalonia/`，不要并入 `midora-desktop.slnx`（该解决方案面向 WPF TFM，混入后 macOS 无法构建，也容易动到现有 WPF 路径）。名字可接受；若 Avalonia 将来取代 WPF 成为唯一外壳，再一次性改名（Avalonia → `midora-desktop`，WPF → legacy），现在不要来回改。
  2. **不要复制 `Midora.Desktop.Presentation`**：先抽出框架中立的 `Midora.Presentation`（`net10.0`），承载 `TimelineRenderModel`、区间索引、cache key/LRU、光栅器与调度；把 `Color/Rect/Point/DpiScale` 换成本项目结构或 `System.Numerics`。WPF 与 Avalonia 各保留宿主/适配层。否则会产生 24k 行的沉默分叉，回贡不可行（AGENTS.md §8 的禁止分叉原则同样适用）。
  3. 初始形状建议：
     ```text
     src/midora-avalonia/
       midora-avalonia.slnx
       Midora.Avalonia/                 # Avalonia 应用（先 macOS，可跨 Windows）
       Midora.Avalonia.Presentation/    # Avalonia 宿主控件/适配
       Midora.Avalonia.Tests/
     src/midora-presentation/
       Midora.Presentation/             # net10.0，框架中立（从 Desktop.Presentation 迁移）
       Midora.Presentation.Tests/
     ```
  4. 构建注意：`Directory.Build.props` 全局 `RuntimeIdentifiers=win-x64`（`Directory.Build.props:7`），新项目需按项目覆盖；Avalonia 项目用 `Microsoft.NET.Sdk` + `net10.0`，不引用任何 WPF 程序集。
- 影响：决定回贡的可合并性与两条 UI 路径的长期维护成本。
- 确认状态：待用户确认命名与是否先抽中立层（建议先抽 spike 所需最小子集，再逐步迁移）

### D-PERF01：性能对照口径与验收门（部分回答）

- 已确认（用户 3）：「羸弱」= 与 yinhe wgpu 对照的架构判断，非 Midora 实测回归。
- 仍需明确（做架构决定前的最小集合）：
  1. 目标规模与场景：编辑态可见音符数、全曲视图、连续 pan/zoom、播放指针、导入/编辑事务，哪些是主目标？
  2. 同数据对照样本：准备用哪个 `.mid`/`.midora`（音符数、最大 Segment）与 yinhe 同机对测？
  3. 验收门：帧时 p95、操作延迟、内存上限、首次可用时间。
- 影响：原为 D-RENDER01 spike 的进入条件；用户已决定暂缓，不作为当前阻塞。
- 建议：先用同一份 10M 级数据在 macOS 上测 Avalonia+Skia 路径；性能门在正式回贡前或实测出现退化时补冻。
- 用户回答：2026-09-20 暂缓（§1.3 语境）
- 确认状态：**用户决定暂缓**（spike 不设性能门，先跑通与正确性）

### D-MAC03：macOS 实时音频后端（沿用，优先级后置）

- 场景：唯一生产设备工厂为 BASSWASAPI；macOS 需新实现（BASS 核心 CoreAudio 设备输出或替代方案）；Worker 的 win-x64 Native AOT、Job Objects、命名共享内存/管道均需 macOS 方案。
- 需要决定：继续 BASS 体系（新设备工厂 + macOS 二进制基线 + 许可核验）还是换音频后端；首阶段是否允许无音频。
- 建议：首选 BASS 核心设备输出实现 `IAudioOutputDeviceFactory`；音频作为第二阶段里程碑；macOS 二进制版本/SHA/许可由移植方冻结。
- 用户回答：见 §1.2（上游作者确认需替换 BASSWASAPI；worker 独立）
- 确认状态：待回答（具体方案）

### D-MAC04：输出命名与路径策略（沿用）

- 场景：SRS 14.17.4 固定 Windows 合法化规则；macOS 规则不同。
- 建议：沿用同一合法化规则以保持跨平台确定性与 golden 复用，仅把固定盘/大小写敏感探测改为平台适配。
- 用户回答：
- 确认状态：待回答

### D-MAC05（设计，需 ADR）：macOS 进程隔离与 IPC

- 场景：Job Objects、Windows-only 命名共享内存、命名管道语义需替换（POSIX 进程组、Unix domain socket、文件/共享内存）。
- 说明：属并发/进程模型变更，按 AGENTS.md §5 需独立 ADR；范围决定后开工。
- 状态：待范围决定

### D-LIC01：许可证边界（已答，按用户声明记录）

- 已确认（用户 §1.3）：用户声明其为 yinhe Rust 代码的版权人（他人唯一贡献为一行 toml），**允许**使用；且 Midora 侧不会包含 Rust 代码。
- 据此执行：
  1. 只以 C#/Skia 重实现架构思路（compute cull、LOD 摘要、语义实例等思想不受版权保护）；**不复制** yinhe 的 `.rs`/`.wgsl`/数据结构文件。
  2. fork 与上游保持 Midora 现有 MIT；新增 C# 渲染器为全新 MIT 实现，并保留 Midora 上游 MIT notices。
  3. 若将来确需复制任何 yinhe 源文件，必须在回贡前把「版权人声明 + 显式再许可 + 上游接受」补入本记录。
- 遗留提示（非阻塞）：若 yinhe 仓库仍公开分发，其 AGPL 与字体 notices 问题与 Midora 无关，但属于用户自身项目合规，本记录不代为处理。
- 确认状态：**已答（用户声明）；复制任何 AGPL 源文件前需再确认**

---

## 4. 建议分阶段路线（建议，不是已批准计划）

1. **环境（等用户指示）**：安装 .NET SDK 10.0.400（macOS）；拆分 `win-x64` 全局 RID 与 `net10.0-windows` TFM（新 macOS 平台工程），先构建/测试非 UI 解决方案（`midora-core`/`midora-midi`/`midora-common`/Persistence），记录 Windows-only 跳过与失败。**SDK 安装仅在用户明确指示后执行。**
2. **结构（D-STRUCT01）**：新建 `src/midora-avalonia/` 解决方案；先抽 `Midora.Presentation`（net10.0）的 spike 最小子集（model/index/cache key），不要复制 `Midora.Desktop.Presentation`。
3. **macOS 无音频闭环**：Avalonia 外壳；垂直切片——Arrangement/Piano Roll 一个 `TimelineSurface` + Skia 渲染 + 打开/保存/编译/MIDI 导出。此阶段不做 BASS、不设性能门。
4. **Skia 实现**：按 D-RENDER01 的预构建 chunk 路线实现（S1/S2/S3 作为候选），先正确性与可用性；性能测量推迟到回贡前或实测退化时。
5. **音频第二阶段**：macOS `IAudioOutputDeviceFactory`（BASS 核心设备输出）+ Worker 平台适配（D-MAC05 ADR）+ macOS 原生基线与许可核验。
6. **回贡**：按 SRS §21.4 提交 macOS 范围修订、UI/Avalonia ADR、Skia 渲染 ADR、性能证据与上游产品所有者确认。

---

## 5. 状态汇总

| 问题 | 主题 | 状态 |
|---|---|---|
| D-MAC01 | 移植性质与范围 | 已答核心项（fork + 回贡 + macOS 优先）；上游产品所有者确认与版本待定 |
| D-RENDER01 | 时间线渲染栈 | **已答：Avalonia + Skia，C# 重实现**；架构/ADR 待补，性能门暂缓 |
| D-STRUCT01 | Avalonia 解决方案与共享呈现层结构 | 待确认（建议独立 `src/midora-avalonia/` + 抽取 `Midora.Presentation`） |
| D-PERF01 | 性能对照口径与验收门 | **用户决定暂缓**；先跑通与正确性，回贡前补 |
| D-MAC03 | macOS 实时音频后端 | 待回答（方案未定，优先级后置） |
| D-MAC04 | 输出命名/路径策略 | 待回答 |
| D-MAC05 | 进程隔离/IPC ADR | 待范围决定 |
| D-LIC01 | 许可证边界 | 已答（用户声明版权与允许；不复制 AGPL 源文件） |

## 6. 用户回答（原始记录区）

见 §1.1、§1.2（原文照录）。后续回答继续按日期追加，不得改写。

## 7. 补充问答

（等待补充。）

---

## 8. 实施进度

### 2026-09-20

- **SDK**：用户级安装 .NET SDK 10.0.400 到 `~/.dotnet`（`dotnet-install.sh --no-path`，未 sudo、未改 PATH），`~/.dotnet/dotnet --version` = 10.0.400，满足 `global.json`（`rollForward: disable`）。后续命令统一用 `$HOME/.dotnet/dotnet`。
- **非 UI 构建**：`src/midora-core/midora-core.slnx` Debug 构建 20 s 完成 15 个项目；唯一失败为 `Midora.Audio.Bass.Worker`（win-x64/AOT 专用，macOS 无 assets；音频后置，暂不处理）。首次 restore 耗时约 30 分钟，原因是 nuget.org 元数据/包下载极慢（实测包下载约 289 KB/s，元数据 5～22 KB/s）。
- **Avalonia 骨架**：新建 `src/midora-avalonia/midora-avalonia.slnx` + `Midora.Avalonia`（`net10.0`、覆盖全局 RID 为 `osx-arm64`）；Avalonia 版本暂定 **11.3.22**（存在 12.1.2；11.3 为成熟稳定线，降低大规模 WPF 移植的 API 风险，后续可评估升级）。
- **运行 smoke 成功**：restore 完成（慢网导致多次超时，最终 178+ 包落盘）；Debug 构建 0 警告 0 错误；`Midora.Avalonia` 进程启动并已在 macOS 窗口服务器注册（`lsappinfo` 可见），屏幕显示暗色最小窗口。
- **用户指示**：可以开始逐步复刻 UI，并预留 macOS 与 Windows 适配。
- **结构决定（覆盖 D-STRUCT01 原建议）**：用户明确允许「抄 WPF 的 UI」。执行方式：Avalonia 侧复制/适配，**不改动 WPF 侧**（WPF 保持上游原样，避免双向分叉失控）；框架中立的 `Midora.Presentation` 抽取延后到回贡前再评估。
- **UI 移植切片计划（提案，按依赖顺序）**：
  1. **Slice A 外壳与主题**：移植 StyleGallery `Palette/Controls/FluentSystemIcons/WindowControlIcons` 到 Avalonia 资源与 Styles；主窗口标题栏、菜单、Global Command Bar、Workspace Tab、Status Bar；嵌入 Sora/JetBrainsMono 字体。
  2. **Slice B 平台适配层**：窗口 chrome（macOS 红绿灯 / Windows 自绘按钮）、快捷键修饰（⌘ vs Ctrl）、菜单与文件对话框（StorageProvider）、剪贴板/拖放/IME 的 Avalonia 实现；以接口隔离，Windows 条件引用预留。
  3. **Slice C 呈现核心**：复制 `TimelineRenderModel`/区间索引/tick math/cache 结构，替换 WPF 几何类型（`Rect/Point/Color/DpiScale`）；先跑 CPU tile 正确性。
  4. **Slice D 时间线表面**：`TimelineSurface` 自绘控件（Avalonia `Render`）+ Skia 自定义绘制；Arrangement/Conductor → Piano Roll → Velocity/Event Lane。
  5. **Slice E 工作流**：打开/保存/编译/MIDI 导出与各对话框；Project/Workspace 命令接线。
  6. **Slice F 音频**（后置）：macOS 设备工厂与 Worker 适配。
- **待办**：非 UI 测试尚未在 macOS 跑（`Midora.Audio.Bass.Worker` 阻塞部分测试项目）；Slice A 未开始。

### 2026-09-20（续）Slice A 首次落地

- **主题**：新增 `Themes/Palette.axaml`（移植已批准色板）、`FluentIcons.axaml` / `WindowControlIcons.axaml`（由 WPF 资源机械转换：`Geometry`→`StreamGeometry`，76+4 个图标）、`Themes/Controls.axaml`（Button/Text/Menu/FluentIcon/Caption 子集，触发器改为 Avalonia 伪类选择器）。
- **控件适配**：`Controls/FluentIcon.cs` 以 `Path`（`Stretch=Uniform`）子类实现，`Foreground` 映射 `Fill`，替代 WPF Viewbox+Path 模板。
- **字体/资源**：链接仓库 `assets/fonts/Sora`、`JetBrainsMono` 与 `midora-note-transparent-256x256.png` 为 `AvaloniaResource`；`avares://` 字体 URI 已验证资源名正确嵌入。
- **外壳**：`MainWindow.axaml` 按 WPF 结构重建 36px 标题栏（应用标 + 主菜单 + 项目名 chip）、46px Global Command Bar（导航/文件/编辑/偏好 + 传输/位置速度 + Compile/MIDI Export/Audio Export）、欢迎页、25px 状态栏。菜单条目与结构对齐 WPF，但命令未接线（除 Exit/窗口按钮）。
- **平台适配预留**：macOS 用 `ExtendClientAreaChromeHints.PreferSystemChrome` 保留原生红绿灯并左移 72px；Windows caption 按钮已存在但用 `CaptionButtons.IsVisible=false` 隐藏，标题栏拖动/双击仅 Windows 路径生效。
- **验证**：Debug 构建 0 警告 0 错误；应用启动且窗口服务器注册成功；等待产品/用户视觉确认。
- **未完成**：菜单命令、Workspace Tab（`TabControl` 模板）、大量控件样式（TextBox/ComboBox/TabItem/ListBoxItem/ScrollBar/Switch 等）、Windows 平台实机验证。
- **用户视觉反馈修复（同日）**：
  1. 标题栏拖动/双击：之前 macOS 被显式禁用；现改为调用 `BeginMoveDrag`（菜单/按钮来源除外），双击切换最大化/还原。
  2. 红绿灯垂直居中：设置 `ExtendClientAreaTitleBarHeightHint = 36` 对齐 36px 标题栏。
  3. Fluent 图标全部消失：根因是自定义 `Foreground` 不参与 Avalonia 属性继承导致 `Fill` 为空；`FluentIcon` 改为 `TemplatedControl` 子类，使用继承的 `Foreground` 并在 `Render` 中按 `Geometry.Bounds` 等比缩放绘制。
  4. 几何转换说明：WPF `FluentSystemIcons.xaml` 只做机械转换（默认 xmlns → Avalonia、`<Geometry>` → `<StreamGeometry>`，路径数据语法兼容），不引用 `System.Windows.Media.Geometry`；新增图标可直接把 WPF 路径数据粘贴进 `<StreamGeometry>`。
- **第二轮视觉反馈修复（同日）**：
  1. 红绿灯垂直居中：新增 `Platform/MacWindowChrome.cs`，用 AppKit `standardWindowButton:` + `frame/setFrame:` interop（仅 Apple Silicon、best-effort）把三个按钮下移 4px，窗口打开与状态变化时重施；失败则保持原生位置。
  2. 标题栏误拖动：点击 File→New 会移动窗口的根因是事件来自菜单 Popup 而 `FindAncestorOfType<Menu>` 失败；改为独立透明 `TitleBarDragSurface`（在内容之下）承载拖动/双击，菜单与按钮不再触发。
  3. 菜单高亮：按 WPF 指标移植一级项 `Height=29`、`Padding=9,0`、`CornerRadius=3`、hover `Surface.4`/Primary、submenu `Padding=9,5`。
  4. 按下动画：不是 WPF 基线特性，是 Avalonia FluentTheme 的默认按压过渡；已在 Button 样式清空 `Transitions` 与 `RenderTransform`，保持 WPF 的“背景/边框变化 + 内容 1px 下移”。

### 2026-09-20（续）Slice B：批量窗口移植

- **方法**：先写共享指南 [Midora-Avalonia-Window-Porting-Guide.md](Midora-Avalonia-Window-Porting-Guide.md)，再分 8 个并行子 Agent 按组移植；子 Agent 只写各自窗口文件，不构建、不改共享文件。
- **产出**：`src/midora-avalonia/Midora.Avalonia/Windows/` 下 44 个窗口 + 1 个 UserControl（`ValueTraceShapeSelector`，附预览窗口）+ `WindowCatalog.cs`；覆盖 WPF 侧除 Workspace 视图与 MainWindow 外的全部窗口。
- **集成**：`MainWindow` 增加临时 `Windows` 菜单（按 5 组列出全部窗口，模态打开）；新增 `--smoke-windows` 启动探针，逐个创建/显示/关闭全部窗口并报告失败。
- **验证**：Debug 构建 0 警告 0 错误；冒烟 `WINDOW-SMOKE total=44 failures=0`。
- **本轮修复**：窗口缺公共无参构造（5 个）、事件处理器签名与 Avalonia 事件委托不匹配（InstrumentSelection/Quantize/Scale）、`OnionSettingsDialog` 的 `PropertyChanged` 隐藏基类成员、`ScaleSelectionDialog` 在 `InitializeComponent` 期间 TextChanged 触发导致的 NRE。
- **已知近似（子 Agent 报告汇总）**：AvalonEdit 全部替换为等宽 TextBox；Avalonia 无 `DialogResult`，统一 `Close()` + 结果属性；WPF ViewModel/Domain 用本地占位数据；触发器/`VirtualizingPanel`/`DisplayMemberPath` 等按指南降级；各窗口重复手写标题栏；TextBox/ComboBox/TabItem/ListBox/ScrollBar 等仍为 FluentTheme 默认样式，未对齐 StyleGallery 基线。
- **未移植**：`ConductorWorkspaceView`、`AllTracksView`、`LaneTabHeader`、`InstrumentChangeLane`（Workspace/时间线宿主，属 Slice C/D）。

### 2026-09-20（续）Slice B2：外壳业务接线

- **会话层**：新增 `Session/ShellSession.cs`（项目存在/名称/modified、`Workspaces` 与 `ActiveWorkspace`、前进后退历史、播放/编译占位、Loop/Follow/Snap/Grid 状态、状态栏字段与 Issue 颜色）与 `Session/WorkspaceTab.cs`。
- **占位视图**：`Views/ArrangementView`（含工具/缩放交互骨架）、`Views/DiagnosticsView`（搜索/筛选/空态）、`Views/AllTracksPlaceholderView`。
- **主窗口接线**：全部菜单与命令栏按钮接上会话命令；New/Open Project/Open MIDI 用 StorageProvider 建立会话；Save/Save Copy/Reset Playback/Undo 等用 `MessageDialog` 明示“尚未接线”；Preferences/Catalogs/About/MIDI Export/Audio Render/新建轨道对话框接上；工作区 Tab 可切换、关闭与前进后退；欢迎页按钮生效；状态栏/标题栏/项目名绑定。工具栏 Snap/Grid/Follow/Loop 为会话状态。
- **验证**：构建 0 警告 0 错误；`--smoke-windows` 44/44；新增 `--smoke-shell`（建项目 → 开三个工作区 → 前后导航 → 播放/停止 → 标记修改 → 编译 → 关 Tab → 关项目）failures=0。
- **诚实边界**：真实 Project/Domain、`.midora` 持久化、Undo/History、Selection/Clipboard、编译与导出实际执行均未接线，需 Slice C（呈现核心）与 Application 适配。

### 2026-09-20（续）Slice C：呈现核心移植（已提交）

- 新增 `src/midora-avalonia/Midora.Avalonia.Presentation`（`net10.0` + Avalonia 11.3.22，引用 `Midora.Domain`）。
- 机械移植：`TimelineRenderModel`（2634 行，除命名空间零改动）、`TimelineRasterCache`（3171 行，`WriteableBitmap`/Avalonia Dispatcher/`DpiScale` 替换）、13 个 Rendering 文件、5 个 Interaction 文件、`WorkspaceState`（1015 行）。
- 新增 shim：`PixelBufferBitmap`（Pbgra32 → `WriteableBitmap`）、`DpiScale`、`DispatcherShutdownState`（Avalonia 未公开 `HasShutdownStarted`）、`TimelineSurfaceModes`（把 WPF 内嵌在 `TimelineSurface.cs` 的三个枚举拆出）。
- 差异记录：`CancelablePresentationDispatch.Post` 默认参数改为重载（Avalonia `DispatcherPriority` 非 const）；移除被相邻条件蕴含的 `Rect.IsEmpty` 守卫。
- 验证：构建 0 警告 0 错误；`--smoke-windows`/`--smoke-shell` 通过。

### 2026-09-20（续）Slice D：Arrangement 时间线第一版（已提交）

- 新增 `Controls/TimelineSurface.cs`（约 1095 行，Arrangement-only 自绘控件）：bar 网格与顶部 ruler、lane 背景与名称、Segment 圆角矩形、Conductor 点、Note/Event、Segment preview 音符/事件线、hover/选中、编辑光标、marquee 选择、滚轮平移、Ctrl+滚轮缩放、指针 tick 读数事件。
- 新增 `Rendering/DemoTimelineSource.cs`（约 502 行确定性演示工程：6 轨 × 8 Segment、512 个 Logical Note、64 个参数点、Conductor tempo/拍号/marker）。
- `ArrangementView` 接入 surface 与演示数据（缩放按钮、tick 读数、选中提示）。
- 验证：构建 0 警告 0 错误；shell 冒烟通过。
- **与 WPF 的差异（诚实记录）**：本版是用移植后核心**新实现**的 Arrangement-only 第一版，尚未接线 tile raster cache、Piano Roll/Velocity/Event Lane/Conductor 模式、编辑手势与真实 Project 数据源；这些依赖 Slice E 的 Application/Domain 适配。

### 2026-09-20（续）Slice E1：真实 MIDI 导入（已提交）

- 新增 `Import/MidiImportModel.cs` + `MidiImporter.cs`：基于 `Midora.Midi.StandardMidiFile.ParseType0Or1` 的真实 SMF 导入（FIFO 同 channel/key 配对、NoteOn-0、轨道尾收口、CC/RPN 归类、PitchBend 14-bit、tempo/拍号/调号/marker、严格 UTF-8 → CP932 文本回退、512 MiB 上限与 `MidiImportException`）。
- 新增 `Import/MidiTimelineSource.cs`（806 行）：Arrangement 源（lane 0 = Conductor，每轨每 8 小节一个 Segment + 真实 preview）+ 每轨钢琴卷帘源（Note/Channel Event）+ 指纹与区间查询。
- 新增 `--smoke-shell` 的 `MIDORA_MIDI_SMOKE` 环境变量导入路径；用本地生成的 217 字节 Type 1 SMF 验证 parse → session → arrangement 源全链路，failures=0。
- `File → Open MIDI as New Project` 现为真实导入（含错误对话框）；`ArrangementView` 支持 `SetSource` 切换演示/真实数据。

### 2026-09-20（续）Slice E2：视图模式与音轨编辑器（已提交）

- `TimelineSurface` 拆分为 partial：core / Arrangement / Helpers；新增 PianoRoll / Velocity / EventLanes / ConductorView 四种模式渲染与 `SurfaceMode`、`FirstPitch`、`PitchCount`、`ValueMinimum/Maximum` 属性、`LaneActivated` 事件。
- 新增 `Views/MidiTrackView`：轨道选择、Notes/Velocity/Events/Conductor 模式切换、缩放与读数。
- 新增会话 `WorkspaceKind.MidiTrack` 与 `OpenMidiTrackWorkspace`；Arrangement 双击某轨 lane（`LaneActivated`）打开该轨编辑器工作区。
- 验证：构建 0 警告 0 错误；shell 冒烟（含真实 MIDI 导入与打开音轨工作区）failures=0。

### 2026-09-20（续）Slice E3：编辑模型与手势（已提交）

- 新增 `Editing/EditableMidiProject.cs`（603 行）：可变 Note/Event/Track 模型（确定性 `MidoraId` 分配、排序维护、值域 clamp、拆分、批量变换）与有界 512 条命令的 Undo/Redo。
- 新增 `Editing/EditableMidiSource.cs`（204 行）：以 `project.Version` 参与指纹、每次查询从活模型重建 Item，编辑后渲染自动失效。
- 新增 `Controls/ITimelineEditHost.cs` + `TimelineSurface.Editing.cs`（928 行）：Draw 创建/延展、Select 移动与边缘 Resize、Erase、Split、Velocity 涂改、Event 点拖动；事务 Begin/End、Escape 取消与指针捕获。
- 新增 `Editing/EditableMidiEditHost.cs`（246 行）：Item Id → 可变对象映射、批量移动/擦除/拆分/赋值。
- `MidiTrackView` 接入可编辑工程与工具（Select/Draw/Erase/Split、S/D/E 快捷键、macOS ⌘Z/⇧⌘Z）、`Edited` 事件与 Undo/Redo；会话持有 `EditableMidiProject`，打开音轨工作区时绑定，编辑即标记 Modified；Edit 菜单 Undo/Redo 路由到活动编辑器。
- 验证：构建 0 警告 0 错误；shell 冒烟新增真实编辑脚本（AddNote → Transform → Undo → Redo → SetVelocity → 活动编辑器 Undo/Redo）failures=0。
- **已知缺口**：一次拖动会按增量产生多条 Undo 记录（事务尚未合并命令）；Event tick 平移未进入 Undo；Arrangement 的 Segment preview 仍读导入快照，编辑后不刷新；Velocity/Event 编辑为单点命中，多选收集依赖可见项。

### 2026-09-20（续）Slice E4：编辑事务合并与实时预览（已提交）

- `EditableMidiProject` 增加 `BeginTransaction/EndTransaction`：同一事务内同类同对象集的 `TransformNotes`/`SetVelocity`/`SetEventValue`/`ResizeNote` 合并为一条 Undo 命令（保留首个旧值、以最后新值覆盖），通知与 Version 在事务提交时各触发一次；`Undo/Redo` 会先关闭打开的事务。新增可撤销的 `TransformEvents`。
- `EditableMidiEditHost` 转发事务并改用 `TransformEvents`；事件拖动现在可撤销且触发 `Changed`。
- `MidiTimelineSource` 支持 `liveProject` 叠加：Arrangement 的 Segment preview 与总览按 `liveProject.Version` 读取当前可编辑轨道内容；`ArrangementView.SetSource(..., preserveView: true)` 让编辑后刷新不重置 zoom/选择；会话在 `Changed` 时刷新 Arrangement 并标记 Modified。
- 验证：构建 0 警告 0 错误；冒烟新增「两次增量拖动合并且一次 Undo 回到起点」断言，failures=0。

### 2026-09-20（续）Slice F：视觉对齐轮（已提交，截图驱动）

- **验证方法（用户要求）**：`MIDORA_MIDI_OPEN=<path>` 启动即导入真实 MIDI、`MIDORA_OPEN_TRACK=<n>` 直接打开音轨编辑器；用 `screencapture -x` 截图并以 PIL 做像素采样（确认轨道色带、音符预览像素、排查默认蓝色）。
- **全局强调色**：`App.axaml` 覆盖 `SystemAccentColor` 系列为 Midora 红，FluentTheme 的 Tab 选中、ToggleButton、ComboBox 等不再出现默认蓝；像素扫描确认主窗口蓝色像素只来自轨道色带（非 UI 默认色）。
- **状态栏/通知**：状态栏改为 `0 Errors, 0 Warnings | Not compiled | No SoundFonts Enabled | Saved/Unsaved | Stopped/Playing`；新增通知条（绿色对勾 + 文本 + `View Full Message`/`Dismiss`），编译与导入会写入通知。修复诊断摘要按钮被 `.icon` 固定宽度截断的问题。
- **Arrangement 视觉**：新增 190px `LaneHeaderStrip`（轨道色条、名称、`P1 ChN Melodic`、M/S 芯片、空轨不再画头）；Segment 按 `AccentColor` 着色（暗化 0.55/选中 0.8）并带 1px 间隙；内容区不再重复画 lane 名（`ShowTrackNames=false`）。
- **工具栏**：工具改为选中红底的 ToggleButton，新增指针 tick 读数 `(tick)`、`Grid`、`Snap`、`1/8` 细分、`Length 1920`、缩放与 Bar 读数；默认显示 16 小节。
- **钢琴卷帘修复**：lane 约定改为绝对音高（`FirstLane = FirstPitch`，`GetPitchForLane(lane) = lane`），截图确认音符矩形恢复显示；`⌘Z/⇧⌘Z` 撤销、Select/Draw/Erase/Split 工具均已在音轨编辑器工作。
- **仍存差距（像素级复刻未完成）**：钢琴卷帘缺小节标尺与左侧钢琴键盘；Arrangement 仍缺 WPF 的 Segment tabs（`MIDI Segment: …`）、Conductor 编辑器、ruler marker 标签、All Tracks/Onion 按钮；M/S 芯片不可点击（运行期 Mute/Solo 未接线）；Arrangement 工具按钮无编辑行为（编辑目前只在音轨编辑器）；Notice 语义与 WPF 的编译/任务流程仍未完全一致。

### 2026-09-20（续）Slice G：继续截图对齐（已提交）

- **钢琴键盘与方向**：新增 `PianoKeyboardStrip`（白/黑键、C 标签、按下高亮、`KeyPressed/KeyReleased`），接入音轨编辑器 Notes/Velocity 模式；修正钢琴卷帘音高方向（高音在上，`FirstLane = FirstPitch`，lane == 绝对音高），Shift+滚轮纵向滚动；Track/模式切换同步键盘。
- **Segment 标签页**：Arrangement 双击 Segment 打开 `MIDI Segment: <track>@<bar>` 会话（lane→track、chunk 起点/跨度），与原版命名一致；双击 lane 仍打开整轨编辑器。
- **播放指针**：`PlaybackTick` 属性 + 红色光标与顶部标记；会话用 `DispatcherTimer` 按 tempo/TPQN 推进位置，命令栏读数格式对齐为 `0000 : 00 : 0000`，状态栏 Playing/Stopped 联动，Loop 开启时回绕。
- **标尺 Marker 标签**：Conductor Marker 文本投影到 Arrangement/模式视图的 ruler 带。
- **空工程语义**：新增 `EmptyTimelineSource`；New Project 不再显示演示工程（只显示 Conductor 行）。
- **M/S 运行期过滤**：轨道头 M/S 芯片可点击（含选中配色），Arrangement 泳道按 Mute/Solo 变暗并隐藏内容；仅运行期，不持久化、不进 canonical。
- **评审入口**（仅用于截图验收）：`MIDORA_MIDI_OPEN`、`MIDORA_OPEN_TRACK`、`MIDORA_OPEN_SEGMENT`、`MIDORA_TRACK_MODE`、`MIDORA_AUTOPLAY`、`MIDORA_NEW_PROJECT`。
- **验证**：构建 0 警告 0 错误；shell 冒烟（真实 MIDI、编辑、Undo/Redo、合并拖动）failures=0；逐项截图确认布局、颜色（无 Fluent 默认蓝）、钢琴键盘、Velocity 渲染（像素采样 3,736 命中）、播放指针与位置读数。
### 2026-09-20（续）Slice H：按 WPF 原版纠偏布局与状态文案（本轮）

对照产品所有者提供的 WPF 原版截图，逐项纠正此前自造/错位的界面结构：

1. **Notice 与状态消息分家**：`Notice` 只在真正需要横幅提示时出现（红色 `Brush.Red.Subtle` 底 + `Brush.Red.Dark` 底线 + 右侧 X 关闭），不再有伪造的 “Project created”。`StatusMessage` / `View Full Message` / `Dismiss` 回到底部状态栏右侧（`HasStatusMessage` 时显示），详情用 `TextDetailsDialog`（标题 + 等宽正文 + Copy/Close）。
2. **工作区标签栏**：改为 WPF `SingleLineWorkspaceTabs` 结构（32px `Surface.1` 条 + 右侧 32px ▾ 工作区列表按钮），TabItem 采用 WPF 基线（12,8 内边距、选中红色 2px 下划线、悬停 `Surface.2`），不再另起一行“多余栏”。
3. **Arrangement 头部**：按 WPF `PanelHeader`（34px）实现：左侧上下文文案 `N tracks · N segments`，右侧依序为指针 tick 文本、`Grid`（44 宽，选中红）、`Snap`、可编辑细分 `1/8`、`Length 1920`、`Zoom out/in`、`All Tracks`、分隔线、`Draw/Select/Split/Erase`（30×24）。新增通用 `ToggleButton` 基线样式（WPF `Toggle.Segment`：28 高、1px `Brush.Border`、选中 `Red.Subtle/Red.Dark/Red.Hover`）。
4. **轨道高度与列宽**：`LaneHeight` 由 28 改为 WPF 的 56，轨道头列宽由 190 改为 `ArrangementLaneHeaderWidth = 232`；标题上移、`P1 Ch.N Melodic` 明细、M/S 芯片按 `Toggle.TrackState`（25×22、10 号加粗、居中）绘制。
5. **删除多余底栏**：移除 Arrangement 与音轨编辑器的 “Tick … · Wheel: pan …” 提示条与占位页脚，仅保留 WPF 的 25px 状态栏。
6. **导入文案**：改为 WPF 原文 `MIDI import completed with {w} warning(s) and {i} information notice(s).` + 详情报告（`Warnings:`/`Information:` + 逐条 `[Severity] Code`、`Source: MTrk n`），并新增真实导入诊断（未闭合音符=Info、无匹配 NoteOff=Warning、文本解码失败=Warning、SysEx 未保留=Info）。
7. **状态栏字段**：`CompileState` 使用 WPF 文案（`Not Compiled` 等），`ProjectState` 使用 `No Project / Unsaved / Modified · Unsaved / Modified / Saved`，标题栏修改标记由 `●` 改为 WPF 的 ` *`。
8. **Arrangement 左上角**：补上 WPF 的轨道头角块（Event Instruments 开关、Add Track 菜单、纵向缩放、重置全部 Mute/Solo）；纵向缩放范围 28–112，重置同时清空渲染过滤与芯片状态。标尺 Marker 改为 WPF 的带边框圆角 chip（高 15、9 号 SemiBold），小节号下移，避免与 chip 重叠。
9. **菜单**：View 菜单去掉自造的 All Tracks/Grid 项（All Tracks 走 Arrangement 工具栏按钮），开发用的窗口目录移入 Application 菜单的 “Windows (port catalog)” 子菜单。

验证：构建 0 警告 0 错误；`SHELL-SMOKE failures=0`、`WINDOW-SMOKE total=44 failures=0`；截图核对空工程、真实 MIDI 导入与 Chords 音轨编辑三种场景（标签栏、头部工具栏、56px 轨道、M/S 芯片、Marker chip、状态栏按钮、无横幅/无多余底栏）。

- **仍存的功能级差距（需新子系统，不是纯视觉）**：Conductor 专用编辑器工作区（事件列表 + Tempo 阶梯图）、Track/All Tracks 概览条与导航、钢琴卷帘对象列表面板、Event/Parameter Lane 的增删与管理栏、播放键盘试听（依赖音频引擎）、Diagnostics 实际内容。

### 2026-09-20（续）Slice I：P3 Skia 形状批处理 + 预览瓦片 + 控件基线（本轮）

产品所有者要求“开始 3”并继续按 WPF 原版校正，本轮：

1. **P3 渲染路径（新增）**：`Presentation/Controls/TimelineShapeDrawOperation.cs` 实现 `ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature`，把泳道底纹、栅格线、标尺刻度、Segment 主体/选中框、钢琴卷帘音符、力度条、事件步进线与事件点、Conductor 点全部批量成一次 `SKCanvas` 绘制（`TimelineShapeBatch`，AA 关闭、圆角按 WPF 半径 2）；文本、Marker chip、瓦片位图仍在 `DrawingContext` 上按序绘制，保证主题文本与图片位置不变。`TimelineSurface.Tiles.cs` 提供 `AddFill/AddShape/AddLine/AddEllipse/FlushShapes` 与颜色转换（含 WPF 的 0.88 Segment、0.78 Note 不透明度）。
   修复过程记录：首版把线段端点存进 `Rect(x,y,width,height)` 导致所有批处理线画成斜线，已改为独立 `X1/Y1/X2/Y2`，截图确认恢复水平/垂直。
2. **Arrangement 音符预览改用移植瓦片管线**：`MidiTimelineSource` 增加 `TrackDetails`（`P.{port} Ch.{ch} {mode}`，对齐 WPF `PresentationModels.cs:1951`），`TimelineSegmentPreviewRasterizer` 的 CPU 栅格器按 96 px/quarter 固定内容宽度 + LOD 出瓦片，经 `PixelBufferBitmap` 变成位图后 `DrawImage` 平铺；未就绪时保留旧的实时细线回退，因此任何时刻都能看到音符噪声。
3. **控件基线补齐**：`TextBox`（34 高、10,3 内边距、圆角 3、Surface.0/Border.Strong、Caret Red.Hover）、`ComboBox`（32 高、圆角 3、9 右侧 chevron）、`ComboBoxItem`、`ScrollBar`（10 宽、`Border.Strong` 圆角 4 滑块、悬停 Text.Tertiary、拖动 Red）、TabItem `MinHeight=0`（修复标签条偏高）。
4. **Arrangement 标签图标**改为 WPF 的 `Fluent.MoviesAndTv20Regular`（`WorkspaceTabIconConverter`）。
5. **标尺/网格颜色**按 WPF：bar 线 = `Brush.Border`，beat 线 = Border × 0.32，刻度 = `Brush.Text.Primary` 2 px 且高 5，小节号 10 号 Text.Primary 位于标尺底部；轨道头分隔线、标尺底线同色。
6. **轨道头**改为 WPF 栅格逻辑：20×20 类型图标（Conductor=`Wrench`，其余=`Midi`）在 x=5、主标签 11 号 Text.Primary、副标签 9 号 Text.Tertiary 外加圆角 2 边框 chip、M/S 为 16×16 无圆角方块（未选中透明+Border 边框+Secondary 文字；M 选中 `Red.Subtle`/`Red.Dark`/`Red.Hover`，S 选中 `Success.Subtle`/`Success`），命中区按 WPF 的 `width-41/-21` 与 `width-21/width`。
7. **滚动条**：`TimelineSurface` 新增只读度量（`LaneCount`、`VisibleLaneCount`、`MaximumFirstLane`、`ExtentEndTick`、`MaximumStartTick`，在 `Render` 内刷新），Arrangement 与音轨编辑器增加纵向（泳道）与横向（时间）滚动条。
8. 钢琴卷帘音符补齐 WPF 语义：圆角 2 + `Brush.Border` 描边 + 0.78 不透明度 + 选中 `Red.Dark`/`Red.Hover` 双描边；Segment 编辑器默认 `LaneHeight` 由 12 改为 WPF `PianoEditorDefaults.LaneHeight = 15`。

验证：构建 0 警告 0 错误；`SHELL-SMOKE failures=0`、`WINDOW-SMOKE total=44 failures=0`；最大化截图核对 Arrangement（预览噪声、标尺/网格、轨道头、双滚动条）、钢琴卷帘、力度条三种场景。

- **仍存的功能级差距（需新子系统，不是纯视觉）**：Conductor 专用编辑器工作区（事件列表 + Tempo 阶梯图）、Track/All Tracks 概览条与导航（WPF `TimelineOverviewSurface`）、钢琴卷帘对象列表面板、Event/Parameter Lane 的增删与管理栏、播放键盘试听（依赖音频引擎）、Diagnostics 实际内容。P3 路径后续可扩展为 `SKPicture`/`DrawVertices` 与瓦片缓存合并，并按 ADR-UI-020 补性能门。

### 2026-09-20（续）Slice J：钢琴卷帘与标签条按 WPF 校正（本轮）

产品所有者指出钢琴卷帘与标签条的实际差异，本轮修复：

1. **标签条横排**：放弃 Avalonia `TabControl`（其模板/ItemsPanel 不可靠，实际渲染成竖排），改为自绘单行标签条：`ItemsControl` + 水平 `StackPanel` + `ContentControl`，标签为 `Border.wstab`（12,8 内边距、选中红色 2 px 下划线、悬停 `Surface.2`），`WorkspaceTab` 新增 `IsActive`（INPC）由 `ShellSession.ActivateWorkspace/CloseWorkspace/CloseProject` 维护。截图确认 `Arrangement` 与 `Chords` 水平并排。
2. **钢琴卷帘音符**：去掉圆角（批处理半径 0，与 WPF 瓦片栅格一致），保留 0.78 不透明度与 Border 描边；选中用 Red.Hover 描边。
3. **钢琴键盘**：按 WPF `DrawPianoKeyboardCore` 重写——白键整宽矩形 + Border 描边；黑键宽 `max(12, round(width*0.68))`、上下各 1 px、圆角 1、带描边，因此黑键右侧露出白键；C 标签右对齐（`x = width - text - 6`）、`fontSize = clamp(laneHeight*0.56, 8, 11)`、SemiBold、`Brush.PianoKey.Label`。
4. **删除重复音名**：移除 surface 的 `DrawPitchLabels`（WPF 只在键盘上画 C 标签；EventLanes/Velocity 模式也不画 lane 标签）。
5. **卷帘角块**：音轨编辑器左上补上 WPF 的竖排缩放角块（52×18，`Surface.1` 底、底部/右侧 Border 分割线、`Zoom out/in` 按钮 22×18），范围 3–128（WPF `MinimumPianoLaneHeight/MaximumPianoLaneHeight`）。
6. **卷帘底纹与网格**：白键行使用 `Surface.1` 底纹、黑键行用 `Surface.0`（对齐 WPF `shaded = !IsBlackKey`），每个半音仍有 1 px 分隔线；标尺刻度只在 bar 线绘制（WPF `DrawBarRuler` 只在 bar 线写刻度与小节号），不再每格都画，消除“繁琐”的密集刻度。

验证：构建 0 警告 0 错误；`SHELL-SMOKE failures=0`、`WINDOW-SMOKE total=44 failures=0`；最大化截图核对标签条横排、键盘黑键右露白、C 标签位置、角块、方形音符、白键底纹。

- **钢琴卷帘仍未接入的 WPF 功能**（下一批）：底部 Lane Tab Host（Inst./CC7/Program Change 等 lane 标签页与各自工具条）、`List/Lanes` 切换与对象列表面板、下半区值编辑 lane、`TimelineOverviewSurface` 水平概览+视口拖动、`Onion` 按钮、PianoNotes/Selection 瓦片缓存（需要 WPF 的 `TimelineRenderSnapshot` 投影层；当前音符走批处理实时绘制）。这些属于新子系统，不是纯视觉微调。

### 2026-09-20（续）Slice K：macOS 标题栏高度平台化，移除红绿灯 interop（本轮）

**问题**：红绿灯偶发落到默认（偏上）位置，必须最大化/还原一次才回到自定义居中位置；有时启动即错位。

**根因**（详见当轮分析）：`MacWindowChrome.CenterTrafficLights` 用**相对位移**（`frame.Origin.Y -= delta`）一次性挪动三个 `standardWindowButton:`，而 `delta` 以“36px 自定义标题栏 / 原生 28px”为假设。AppKit 在 zoom、还原、resize、换屏、DPI 变化、进入/退出全屏等时都会重新布局红绿灯，把我们的位移丢掉；我们只在 `OnOpened` 与 `WindowState` 变化时重施，且相对位移不幂等（重复施加会叠加）。`WindowStartupLocation=CenterScreen` 等启动期布局还会把 `OnOpened` 里做的那一次冲掉，所以启动即错位。

**决定**（产品所有者确认）：macOS 使用**平台化标题栏高度**，不再与 AppKit 抢布局：

- macOS：标题栏行高 = `ExtendClientAreaTitleBarHeightHint` = 原生 **28**，红绿灯由系统自己在原生栏里居中；Windows 仍保持 WPF 基线 **36**。两者**必须严格相等**（行高与 hint），否则又会出现偏移。
- 删除 `Platform/MacWindowChrome.cs` 及 `RecenterTrafficLights`/`OnOpened`/`WindowState` 分支的全部 interop 调用；`TitleBarHeight` 改为 `OperatingSystem.IsMacOS() ? 28 : 36`，由 `ApplyPlatformChrome` 写 `RootLayout.RowDefinitions[0].Height` 与 `ExtendClientAreaTitleBarHeightHint`。
- 内容（应用图标、菜单行、工程名 chip）与按钮高度**保持原样不动**（菜单仍 29 高、chip 仍 8,2 边距，28px 行内居中，无裁切）；`TitleBarContent.Margin = 72,0,0,0` 继续给红绿灯让位。菜单栏仍留在窗口内（现阶段不迁到系统菜单栏）。
- 复核方式：`MIDORA_WINDOW_CYCLE=1` 评审钩子（+5s 最大化 → +15s 还原 → +10s 改尺寸 1200×700），逐状态截图并测量红绿灯：窗口顶边 78 device、红灯中心 103.5 device（按钮高 12 logical）→ 距顶 12.75–13.75 logical，对应 28px 栏的几何中心（14）在测量误差内；**启动态与最大化态数值完全一致**（修复前两者相差 4 logical）。

验证：构建 0 警告 0 错误；`SHELL-SMOKE failures=0`、`WINDOW-SMOKE total=44 failures=0`；`MIDORA_WINDOW_CYCLE` 截图 + 像素测量核对。
