# ADR：Arrangement 海量音符的 Skia GPU 批渲染层

状态：已接受（2026-09-21，产品所有者批准按"最小验证 → 显著出色则正式接入"执行；验证结果见
`misc/avalonia/Midora-macOS-Port-and-Rendering-Stack-Requirement-Trace.md` 的 Slice T）

## 1. 背景与问题

- 现状：Arrangement 的 Segment 预览（音符/事件）走 CPU 端 display list，**一个音符 = 一个图元**，
  成本 O(内容量)。实测天花板约 10 万图元/帧（50k ≈ 4.3 ms）。
- 目标规模：单工程可能达到**千万级**音符/事件，要求同屏可显示且不卡、不丢内容。
- Avalonia/Skia 不提供 instancing/storage buffer/compute/indirect draw；但提供 `ICustomDrawOperation`
  + `ISkiaSharpApiLeaseFeature` 直通 `SKCanvas`，可用 `SKCanvas.DrawVertices`/`DrawAtlas` 单次提交
  大量四边形。

## 2. 决策

采用**混合渲染**：

1. 轨道、网格、游标（Playback/Edit）、Time Range、选择、自动化面板、对话框等**继续用现有 Skia
   display list / 批量形状通道**（数据量小、需要精确交互）。
2. **音符与事件**（数量不确定且可能海量）走独立的 **GPU 批渲染层**：
   `Control.Render(DrawingContext)` → `context.Custom(op)`，`op : ICustomDrawOperation` 在
   `Render(ImmediateDrawingContext)` 中取得 `ISkiaSharpApiLeaseFeature` 的 `SkCanvas`，
   用 `DrawVertices`（每批 ≤ 16k 四边形，索引为 ushort）提交。
3. 切换阈值：**每帧**精确形状路径的图元预算（初值 20,000）。按 Arrangement 顺序累计，预算用尽后
   其余 Segment 预览一律走 GPU 批路径；小预览保持精确形状（抗锯齿最优），同时一帧内不可能累计出
   无界图元数。阈值可由 `MIDORA_GPU_NOTE_THRESHOLD` 覆盖用于评审对照。

## 3. 数据与坐标

- 顶点只存**音乐坐标**（tick、key），视口变换由 `canvas.Translate/Scale` 或 `SKRuntimeEffect`
  uniform 完成，因此**滚动/缩放不重建顶点数据**。
- 事件（瞬时点）与音符共用同一批通道：事件表达为固定宽度（≥1 device pixel）的竖条四边形。
- 颜色：按轨道 accent 颜色分组批（每批一个 `SKPaint` 颜色），避免每顶点颜色带来的带宽开销。

## 4. 缓存身份与失效

- 顶点批缓存键：`(Segment 稳定 Id, 内容指纹 NoteContentFingerprint/EventContentFingerprint,
  device-pixel 缩放, 颜色, 批序号)`。
- `ICustomDrawOperation.Equals` 比较**缓存对象引用 + 视口**：未变化时 Avalonia 直接复用上一帧结果，
  静止帧成本为零；滚动/缩放仅改变 op 的视口字段（矩阵/uniform），不重建顶点。
- 内容指纹变化（编辑、Undo、重新导入）或 device-pixel 缩放变化时重建对应批并释放旧批。
- 每 Segment 的批缓存设上限（初值 4 批/段 × 256 段），按 LRU 释放；Project 关闭时全部释放。

## 5. 内存预算

- 每音符 4 顶点 × 8 B + 6 索引 × 2 B ≈ 44 B；事件相同量级。
- 只对**可见子集**建批：10 万 ≈ 4.4 MB，100 万 ≈ 44 MB。
- 极端情况（视口覆盖全曲且内容千万级）允许按"仅可见子集"策略自然限制提交量；
  若确实需要全量批，按 44 B/音符 × 1,000 万 ≈ 440 MB 计入预算并在 ADR 记录上限。

## 6. 命中测试与语义

- 命中测试、选择、拖动、Snap、Diagnostics **继续使用同一份 CPU 数据**
  （`ITimelineSegmentPreviewSource` / `TimelineIntervalIndex`），不从顶点批或位图反推语义
  （满足 SRS 24.11 与 AGENTS"不从 bitmap 反推命中或音乐语义"）。
- grid/cursor/selection 不烘焙进顶点批；它们始终由独立图层绘制。

## 7. 分层与顺序

绘制顺序保持：底色/键色条 → 网格 → 轨道头/名称 → Segment 容器 → **音符/事件批（GPU op）**
→ 预览层 → Time Range → Edit Cursor → Playback Cursor → Marquee。GPU op 只覆盖音符/事件，
不改变其他层的 z 序。

## 8. 验证门

- 正确性：`RenderTargetBitmap` 出图非背景像素计数（1M 音符已验证 125,086）；
  与形状路径在同一视口下的像素/位置抽样一致。
- 性能：`MIDORA_TIMELINE_TRACE` 帧耗时（avg/p99/max）；`MIDORA_TIMELINE_SPAN` 非交互拉远；
  目标 1M 可见音符 ≥ 60 FPS、p99 < 16.7 ms。
- 确定性：同内容重复渲染像素一致；缓存命中与未命中路径像素一致。
- 交互：命中测试、选择、Time Range、游标在 GPU 路径下行为不变。

## 9. 实施顺序（本次批准的范围）

1. 本 ADR。
2. 单条 Arrangement 轨接入 `DrawVertices`（可见子集 + 批缓存 + `Equals` 复用），与现有路径按阈值切换。
3. 实测帧率/正确性（`MIDORA_TIMELINE_SPAN` + `MIDORA_TIMELINE_TRACE`）并记录到 trace。
4. 扩展到全部轨道与事件，补齐像素一致性与缓存失效测试。
5. 评估是否将 Piano Roll / 自动化面板纳入同一批通道（暂不纳入）。

## 10. 风险

- Skia 版本升级可能改变 `DrawVertices`/lease API（Avalonia.Skia 直通属于半公开接口），
  需在升级回归中验证；必要时回退到形状路径（阈值置零即可）。
- 大量四边形在放大时填充率上升；阈值切换保证放大走精确路径。
- 顶点批缓存若失效规则不严（例如漏掉颜色或 device-pixel 缩放）会出现脏数据，因此缓存键必须
  覆盖全部影响像素的输入。
