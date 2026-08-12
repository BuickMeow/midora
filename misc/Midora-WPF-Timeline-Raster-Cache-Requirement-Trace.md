# Midora WPF Timeline Raster Cache Requirement Trace

状态：Accepted for implementation
日期：2026-08-11
范围：Arrangement Segment Note Preview、Segment/SubVoice piano roll 的 UI 渲染性能；不改变音乐语义

## 1. 权威依据

- 《Midora SRS》§17.2.3～17.2.5：viewport、Selection 与 cursor 属于 Project Session UI State，运行缓存不得进入 `.midora`。
- 《Midora SRS》§18.1.4：Arrangement Preview 固定投影 MIDI pitch `0..127`、最小 1 px、手工渲染，并按 Segment 稳定 ID/内容指纹复用；zoom/pan/Selection/play cursor 不得重建未变化 preview。
- 《Midora SRS》§18.2、§18.4：Segment/SubVoice 使用 rendered piano roll，并保持正式 Note、crop、Pitch Ruler 和编辑语义。
- 《Midora SRS》§20.1、§20.3～20.5：viewport、Selection、hit ordering、marquee、drag preview 和一次手势一次 Undo。
- 《Midora SRS》INV-020：满足正确性、确定性和资源上限后，时间性能优先于最小内存。

## 2. Requirement trace

| 项目 | 约束 |
|---|---|
| 输入 | 当前不可变 `TimelineRenderSnapshot`、Segment preview 内容、viewport/DPI、共享 theme token、独立 Selection/Transient state |
| 正式 UI 输出 | Arrangement 每个可视 Segment 一次缓存图像绘制，图像映射到完整 Segment 世界矩形后按 viewport 裁剪；piano roll 只组合可视/预取 tile；Selection、drag、cursor 独立覆盖 |
| 命中 | 始终查询原始稳定 ID + lane/pitch interval index；不得按 bitmap 像素反推对象 |
| 失效 | piano tile 使用局部视觉内容指纹，Note 编辑只轮换相交 tile；平移及未受影响 tile 跨 workspace revision 复用；Selection/cursor/hover 不失效基础内容 |
| 并发 | 最多两个后台 raster worker、64 个不同 in-flight raster key；只读取不可变快照；UI 原子接收冻结 bitmap；过期结果丢弃 |
| 资源 | 256×256 device-pixel Pbgra32 tile；共享 256 MiB LRU；预取一圈；Project 关闭/替换清空 |
| 失败 | raster 失败不修改 Project、不阻塞输入、不回退为逐 Note 热路径；记录 runtime trace并允许后续重试 |
| 持久化 | bitmap、tile、LOD、LRU、formatted drawing 和索引均不持久化，不进入 `.midora`、Undo/Redo 或 fingerprint |
| 非目标 | 本轮不引入 SkiaSharp、D3D/D3DImage，不改变 Note/Segment 编辑、Selection 排序、可听结果、编译或音频缓存 |

## 3. 初始实现参数

- Arrangement preview bitmap：`512 × 64` device pixels；按目标矩形拉伸，内容变化才重建。
- Arrangement 的目标矩形始终是完整 Segment 的未裁剪矩形；viewport 只负责 clip，禁止把完整 bitmap 拉伸到可见切片。
- Piano roll tile：`256 × 256` device pixels、Pbgra32。
- 水平/垂直 LOD：以 device-pixel scale 的量化值作为 cache key；pan 不改变 scale key。
- 每个后台 piano raster request 按值冻结 LOD、tile X/Y、snapshot 和颜色；同一组值同时用于局部指纹、cache key、raster 内容和屏幕放置。
- 后台 raster 并发：2；不同 in-flight raster key 上限：64。队列满时不得阻塞 UI，可视块在后续绘制中重试。
- 共享内存预算：256 MiB；LRU 回收 completed bitmap。
- 预取：可视矩形外一圈 tile；可视 tile 优先排队。
- 缓存所有权：进程内共享、Project session 生命周期；Project 关闭或替换时清空。

这些参数是可替换的 UI 实现小决定，不是 Project setting。后续只能依据基准调整，不能改变本 trace 的状态、命中、持久化和失败边界。

### 3.1 Corrective implementation parameters (2026-08-12)

- Piano core tile remains `256 × 256`; the actual raster is `258 × 258`, with a 1-pixel world-coordinate gutter on every side. Screen placement includes that gutter and adjacent images overlap.
- Segment preview keeps a 512-pixel content span mapped directly to the full unclipped Segment bounds. No horizontal source gutter is permitted.
- Note fill and real object borders are rasterized together. Tile clipping never creates a synthetic note border.
- Piano selection is a separate cached tile layer. Velocity bars, selection color, outline, and onset marker use a horizontally tiled raster layer; transient edited values remain a bounded overlay.
- Marquee drawing and hit query share snapped tick/lane bounds. Workspace selection range mutations increment the selection revision once.
- Project content notifications carry the frozen change scope. Desktop refreshes only affected workspaces and does not rebuild an unrelated extreme Segment after editing another Track.

### 3.2 Final-pixel correction (2026-08-12)

- Piano tile cache keys now use the exact current device-pixel scales. A completed tile is composed at exactly one source pixel per device pixel; quantized-LOD bitmap resampling is no longer permitted.
- Every Note edge is rounded from its absolute tick boundary. Adjacent Notes sharing a tick therefore share the same computed boundary; vertical edges use the same absolute lane-boundary rule across every horizontal tile.
- The one-pixel tile gutter remains only for cross-tile coverage. Core clips and gutter destinations are expressed in final device-pixel units, so neighboring tiles cannot acquire different scaling phases.
- Segment preview uses a `512 × 64` bitmap with no horizontal source gutter. Start and end use nearest-boundary rounding, and source pixel zero maps directly to the full Segment left edge.
- Each preview Note covers two adjacent source rows (edge-clamped). This preserves at least one visible row when the 64-row source is reduced to the normal Arrangement lane height with nearest-neighbor sampling; a one-row source mark can otherwise be skipped completely.
- These are UI runtime cache rules only. Hit testing continues to use stable IDs and semantic intervals; Project data, Undo/Redo, compilation, playback, export and persistence are unchanged.

### 3.3 Atomic presentation fallback (2026-08-12)

- Segment/SubVoice piano-roll Note and cached Selection layers retain only the cache keys of their last fully available visible frame. They do not copy pixel payloads or create another raster cache.
- When an edit or exact-scale zoom makes any visible replacement tile unavailable, the layer keeps presenting the previous complete frame instead of exposing a mixture of blank and completed replacement tiles. The replacement becomes visible atomically after every currently visible tile is ready.
- A previous frame is eligible only for the same projection key and only while every referenced bitmap still exists in the shared LRU. If it is incomplete or evicted, the renderer falls back to the existing partial-current/background behavior rather than blocking the UI or rasterizing Notes synchronously.
- During a zoom transition, previous exact-scale tiles are temporarily mapped from their original world tick/lane bounds into the current viewport. The completed replacement still obeys ADR-UI-020's exact device-pixel scale and 1:1 composition rule.
- Hit testing, interaction overlays, cursor, grid and active-range chrome always use the current viewport and semantic snapshot. The fallback is presentation-only and never becomes Project, Selection, Undo/Redo, compilation or persistent state.

## 4. 验证门

1. `TestProject.midora` 的 Arrangement 稳态绘制不枚举两个极端 Segment 的 43,008 个 Note，只绘制两个已缓存 preview bitmap。
2. Segment/SubVoice piano roll 稳态基础层不逐 Note 调用 WPF drawing primitive；可视范围只组合 tile。
3. 普通 mouse move、播放 cursor 和单纯 Selection/Primary 变化不重建基础 snapshot/index/tile。
4. 编辑一个 Note 只轮换与其编辑前后范围相交的 piano tile，并失效对应 Segment 的 Arrangement preview；同一 workspace 的其他 tile 与其他 Segment preview 保持可复用。
5. 命中顺序继续为 `Z → shortest span → stable ID`；marquee 和 Ctrl/Shift/Alt selection 继续使用原始对象。
6. cache 不超过 256 MiB completed bitmap 预算；Project close/replace 后 completed cache 为零。
7. Desktop Presentation、Desktop session tests 和 Release solution build 为零 failure、零 warning、零 error。
8. 编辑或缩放轮换可视 piano tile key 时，在新可视集合全部完成前继续呈现上一完整集合；旧集合仍按世界 tick/lane 边界映射，且不得阻塞 UI 或同步逐 Note rasterize。
