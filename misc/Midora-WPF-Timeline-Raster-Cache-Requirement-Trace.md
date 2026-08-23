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

- Arrangement preview：最高精度固定为 `96 pixels / quarter note`，较低精度只使用半八度 `1 / 2^(n/2)` 固定 LOD；各层均为 64-pixel 高度、256-pixel 横向 tile。Segment tick 长度与 Project TPQN 决定各层总宽度，精确 viewport zoom 不直接进入 cache identity，内容或主题颜色变化才重建。
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
- Segment preview uses a TPQN-normalized maximum reference scale of 96 pixels per quarter note and fixed half-octave (`1 / 2^(n/2)`) lower LODs. Segment duration and LOD determine the horizontal tile count; no horizontal source gutter is permitted.
- Note fill and real object borders are rasterized together. Tile clipping never creates a synthetic note border.
- Piano selection is a separate cached tile layer. Velocity stems, selection color, outline, and onset marker use a horizontally tiled raster layer; freehand/line editing uses one bounded trajectory overlay, while direct single-Note adjustment may replace only that one stem transiently.
- Marquee drawing and hit query share directional snapped tick/lane bounds: the independently snapped Pointer Down edge remains fixed, only the moving edge changes, and either drag direction covers at least one operation step. Workspace selection range mutations increment the selection revision once.
- Project content notifications carry the frozen change scope. Desktop refreshes only affected workspaces and does not rebuild an unrelated extreme Segment after editing another Track.

### 3.2 Final-pixel correction (2026-08-12)

- Piano tile cache keys now use the exact current device-pixel scales. A completed tile is composed at exactly one source pixel per device pixel; quantized-LOD bitmap resampling is no longer permitted.
- Every Note edge is rounded from its absolute tick boundary. Adjacent Notes sharing a tick therefore share the same computed boundary; vertical edges use the same absolute lane-boundary rule across every horizontal tile.
- The one-pixel tile gutter remains only for cross-tile coverage. Core clips and gutter destinations are expressed in final device-pixel units, so neighboring tiles cannot acquire different scaling phases.
- Segment preview uses 256-pixel horizontal tiles at a maximum `96 pixels / quarter note` and 64-pixel height, with no horizontal source gutter. Start and end use nearest-boundary rounding, and reference pixel zero maps directly to the full Segment left edge. Viewport zoom selects the first fixed half-octave LOD whose source-pixel scale is not greater than the display scale; it does not create an exact-scale cache generation and never downsamples a one-source-pixel mark. Snapshot prewarm selects a complete LOD of at most four tiles per Segment, while visible detailed tiles remain non-blocking on-demand work. The full Segment destination is snapped once in device pixels and every tile boundary is derived from that same width, so pan cannot change the nearest-neighbor sampling phase.
- Each preview Note covers two adjacent source rows (edge-clamped). This preserves at least one visible row when the 64-row source is reduced to the normal Arrangement lane height with nearest-neighbor sampling; a one-row source mark can otherwise be skipped completely.
- These are UI runtime cache rules only. Hit testing continues to use stable IDs and semantic intervals; Project data, Undo/Redo, compilation, playback, export and persistence are unchanged.

### 3.3 Atomic presentation fallback (2026-08-12)

- Segment/SubVoice piano-roll Note and cached Selection layers retain only the cache keys of their last fully available visible frame. They do not copy pixel payloads or create another raster cache.
- When an edit or exact-scale zoom makes any visible replacement tile unavailable, the layer keeps presenting the previous complete frame instead of exposing a mixture of blank and completed replacement tiles. The replacement becomes visible atomically after every currently visible tile is ready.
- A previous frame is eligible only for the same projection key and only while every referenced bitmap still exists in the shared LRU. If it is incomplete or evicted, the renderer falls back to the existing partial-current/background behavior rather than blocking the UI or rasterizing Notes synchronously.
- During a zoom transition, previous exact-scale tiles are temporarily mapped from their original world tick/lane bounds into the current viewport. The completed replacement still obeys ADR-UI-020's exact device-pixel scale and 1:1 composition rule.
- Hit testing, interaction overlays, cursor, grid and active-range chrome always use the current viewport and semantic snapshot. The fallback is presentation-only and never becomes Project, Selection, Undo/Redo, compilation or persistent state.

### 3.4 Velocity stem and deferred-gesture parameters (2026-08-12)

- Every Note projects to a fixed 3-pixel stem at the Velocity tile's horizontal cache LOD and a 7-pixel square onset marker in the tile source. The raster query interval is start-tick-local; Note length is not part of the visual width.
- Velocity tile items carry pitch as their visual Z key. Equal-start items rasterize low pitch first and high pitch last; equal pitch uses stable ID order. Hit testing uses the reverse order, so the visible top item is also the direct-edit target.
- Freehand and straight-line gestures retain only pointer trace points while captured. They do not enumerate touched Notes, grow a per-Note WPF overlay, mutate Project, or invalidate Velocity tiles on MouseMove.
- `Alt + Left Drag` always selects the freehand trace route before direct stem/marker hit testing; selection-set filtering remains unchanged.
- Draw-mode Segment / Note hover uses one indexed hit and one transient outline draw. It does not enter a raster key, rebuild a tile, or enumerate the visible Note set.
- On release, the immutable snapshot/index resolves the trace into one stable-ID → velocity map, which is committed as one Project edit. Direct stem/marker dragging keeps a one-entry transient map and bypasses the trace.
- The Velocity layer retains the previous complete visible tile-key frame and switches to edited tiles only when the full current visible set is cached. This adds no bitmap copy and remains inside the shared LRU budget.

### 3.5 Direct-edit edge and SubVoice parity parameters (2026-08-12)

- Timeline pointer positioning and half-open interval containment use separate coordinate conversions. Hit testing floors the continuous world tick into `[startTick,endTick)`; snapping and placement may continue to use nearest-tick rounding.
- Draw-mode Segment/Note resize uses a `5 DIP` edge affordance. The interval index query expands by the corresponding tick tolerance, while final candidate choice uses current screen coordinates; the half-open semantic interval remains unchanged.
- At a shared boundary, the pointer side disambiguates End versus Start. Exact ties prefer Primary, then Selected, then End. Hover cursor and Pointer Down use the same resolver.
- A Select gesture below the marquee threshold sets the snapped Edit Cursor and preserves Object Selection; it does not restore single-object click selection.
- Completed marquee set operations are `none = Replace`, `Ctrl = Add`, `Alt = Remove`, `Ctrl+Alt = Toggle`; Shift remains an Add alias. Empty modifier-assisted ranges preserve existing selection, while an effective unmodified empty marquee clears it.
- Timeline context menus expose `Deselect All` for the Workspace and `Invert Selection` for all hit-testable stable IDs in the context surface snapshot.
- SubVoice Note Piano Roll binds the same marquee completion route, Edit Cursor, active range and blue-gray piano Note palette as Segment Piano Roll. Hit testing remains stable-ID/interval based and `TemplateNote` edits remain domain commands.

### 3.6 Selection-set drag preview and Event Lane pointer readout (2026-08-18)

- Note 与 Event/Parameter Point 的 Move/Copy-Move/Note Resize 在拖动期间显示全部相关 Selection 的目标位置；Pointer Up 前不修改 Project，仍只提交一个正式命令和一个 Undo。
- Logical Parameter Point 的 Ctrl+拖动使用独立的原子复制命令创建新稳定 ID，不能只显示复制预览却在提交时移动原对象；同 tick 结果继续交给统一 later-wins point collision policy。
- Note Move 优先把已完成的 Piano Selection tile 作为一个缓存图层做共享 tick/lane 平移。Event/Parameter Point Move 使用独立 selection-only point tile 做共享 tick/value 平移。缓存未完成或 Note Resize 时，只通过 interval index 查询变换后会落入当前 viewport 的对象，并把全部轮廓合并为一个冻结 `StreamGeometry`；禁止逐对象 WPF `Shape`、Binding 或 `DrawRectangle/DrawEllipse` 热路径。
- `Shift` 时间锁定在 Pointer Down 冻结：Note 放置保持默认 length，Note 与 Point Move/Copy-Move 的 tick delta 为零；Event Lane 空白 `Shift + Left Drag` 只生成一个 tick 的 Point。`Shift + Right Drag` 继续生成水平直线并冻结 value。
- Event/Parameter Lane 的 `(t, y)` 读数属于 transient UI state。tick 显示当前 Operation Grid 的目标 tick，y 映射到当前正式显示值域；水平线手势期间 y 固定为起点常量。该读数、预览 geometry/tile 和修饰键锁存均不进入 Project、Undo/Redo、编译或持久化。

### 3.7 Semantic marquee、可辨识拖动预览与交互热路径（2026-08-18）

- Select marquee 在 Pointer Down 时冻结实际 tick、MIDI lane 或 normalized event value；显示矩形和最终查询每帧都把该世界坐标锚点投影到当前 viewport。滚轮改变 `FirstLane` / value viewport 后，起点不得跟随旧屏幕像素漂移，终点使用当前 pointer 在新 viewport 下对应的实际 tick/lane/value。
- Note Move/Copy-Move 的 selection-only tile 使用透明填充和 `Brush.Info` 蓝色 1 px 实线轮廓，与 Note Resize 的合并 geometry 共用同一视觉 token；原始 Note 层保持不变，以便明确区分源对象和目标预览。
- 已选对象上的 Ctrl Pointer Down 先冻结 Selection revision。只有未越过拖动阈值的 Ctrl Click 才在 Pointer Up 执行 Toggle；一旦进入 Copy-Move，Selection 不做 remove/add 往返，也不触发两次大 Selection tile generation。普通 Move 与 Copy-Move 使用同一 selection-only point/note tile 路线。
- Piano Roll 纵向最小 lane height 从 8 DIP 调整为 4 DIP；超过 128 键所需高度的剩余区域继续为空白，MIDI key 仍严格 clamp 到 `0..127`。
- Note 纵向拖动的纯音高试听热更新不得等待 generation 响应文件；其运行时协议详见 `Midora-Persistent-Audio-Worker-Event-Lanes-and-Pitch-Audition-Design.md` §7。该优化不把音频状态写进 UI raster cache，也不改变 Project edit 提交时机。

### 3.8 SubVoice Lane 布局与选框视觉裁剪（2026-08-18）

- SubVoice 工具栏的 `Lanes` toggle 仅控制下方 Velocity/Event Lane 区域；水平 overview/scroll 固定位于 Piano Roll 与下方 Lane 区域之间，关闭 Lane 后仍可导航时间视口。
- SubVoice Lane 高度与 Segment 共用 `110..520 DIP` pixel clamp，默认 `190 DIP`。高度和可见性是 Instrument Workspace-local session state，切换其他 Workspace 后不得串用，也不得持久化到 Project。
- Marquee 的语义矩形不能先 `Intersect` 成 viewport 矩形后再描边。保留实际投影边界并在 DrawingContext 上施加 lane-content clip，使出界的真实边缘不可见，同时避免在 viewport 顶/底/左右制造合成边框。
- 本节只改变布局和 transient 绘制。框选最终使用的 tick/lane/value 范围、Selection 集合运算、tile cache、正式 Project edit 和消费者链路均不变。

## 4. 验证门

1. `TestProject.midora` 的 Arrangement 稳态绘制不枚举两个极端 Segment 的 43,008 个 Note，只绘制两个已缓存 preview bitmap。
2. Segment/SubVoice piano roll 稳态基础层不逐 Note 调用 WPF drawing primitive；可视范围只组合 tile。
3. 普通 mouse move、播放 cursor 和单纯 Selection/Primary 变化不重建基础 snapshot/index/tile。
4. 编辑一个 Note 只轮换与其编辑前后范围相交的 piano tile，并失效对应 Segment 的 Arrangement preview；同一 workspace 的其他 tile 与其他 Segment preview 保持可复用。
5. 命中顺序继续为 `Z → shortest span → stable ID`；marquee 和 Ctrl/Shift/Alt selection 继续使用原始对象。
6. cache 不超过 256 MiB completed bitmap 预算；Project close/replace 后 completed cache 为零。
7. Desktop Presentation、Desktop session tests 和 Release solution build 为零 failure、零 warning、零 error。
8. 编辑或缩放轮换可视 piano tile key 时，在新可视集合全部完成前继续呈现上一完整集合；旧集合仍按世界 tick/lane 边界映射，且不得阻塞 UI 或同步逐 Note rasterize。
9. 密集 Velocity 自由/直线拖动期间，覆盖层复杂度只与采样后的指针轨迹点数相关，不与已触及 Note 数量相关；松开前不得重建 Velocity tile。
10. Velocity 柱宽与 Note length 无关；同 tick 多音的 raster 与 direct hit 都必须以高 pitch 为最上层。
11. `Alt + Left Drag` 从任意 Velocity 内容位置开始时均不得进入 direct single-Note edit；Direct Timeline 的 Select 单次左键从对象内部开始时仍必须形成 marquee，而不得发出单对象选择。
12. 启用 Snap 后，向左框选时右侧 Pointer Down 边界必须在整个手势中保持同一吸附 tick；向右框选时左侧边界同理。预览与最终查询不得发生一个 operation step 的周期性跳动或分歧。
13. 高缩放下，指针即使换算到对象 `endTick`，仍可命中其右侧 Resize 边缘；相邻对象共享边界时，边界左/右两侧分别命中左对象 End 与右对象 Start。
14. Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 的 Select 短点击只更新 Edit Cursor，不改变 Object Selection；SubVoice 框选必须返回 `TemplateNote` 稳定 ID，并使用与 Segment Piano Roll 相同的 Note palette 路径。
15. 高缩放下，一个 tick 的后半段仍命中包含该屏幕位置的左侧半开区间对象；不得因最近 tick 四舍五入而提前命中右侧对象或空白。
16. Ctrl/Alt/Ctrl+Alt marquee 分别执行 Add/Remove/Toggle 集合运算；无修饰键的有效框选执行 Replace，即使结果为空也清空原选择。短点击不清空选择。
17. `Deselect All` 清空当前 Workspace Selection；`Invert Selection` Toggle 当前右键 surface 的全部可命中对象且保留其他 scope 的选择。
18. 大 Selection 的 Move/Copy-Move 预览不得逐对象发出 WPF drawing primitive；已缓存 Selection tile 可用时每层只做共享二维变换，fallback/Resize 每帧最多提交一个合并 geometry。
19. `Shift` 在 Pointer Down 后冻结时间锁定：Note 创建不改 length，Note/Point Move 不改 tick；Event Lane `Shift + Left Drag` 只提交一个 Point，`Shift + Right Drag` 的 `(t, y)` 读数 y 始终等于起点常量。
20. Logical/Template Note 的精确 tick+key 冲突仍删除 newcomer；Logical Parameter/SubVoice MIDI Point 的同 target+tick 冲突改为 newcomer 覆盖 incumbent，且 Undo/Redo 恢复各自原对象和值。
21. 框选期间滚动 lane/value viewport 后，Pointer Down 的实际 tick/lane/value 保持不变；终点按当前 pointer 在新 viewport 下重新换算，预览矩形与 MouseUp 查询必须一致。
22. Note Move/Copy-Move 目标只显示蓝色实线轮廓；透明内部不得遮盖或伪装成已提交 Note，跨 tile 边界不得增加伪边框。
23. 已选 Point/Note 的 Ctrl Copy-Move 从 Pointer Down 到 MouseUp 不改变 Selection revision；未形成拖动的 Ctrl Click 仍只 Toggle 一次。
24. 已配置音高试听流上的每次 pitch 变化不得同步读取/写入 `response-*.maws`；纵向拖动 UI 成本不随跨进程文件 I/O 延迟增长。
25. Piano Roll lane height 可缩小到 4 DIP；任意 viewport 高度仍不得生成 key `<0` 或 `>127`。
26. SubVoice 的水平 overview/scroll 必须位于 Piano Roll 与下方 Lane 编辑器之间；`Lanes` 关闭后 Lane 行高度为 0，重新开启时恢复此前已 clamp 的 `110..520 DIP` 高度。
27. 框选实际上边界/下边界滚出 lane viewport 后，相应虚线不得重新出现在 viewport 边缘；滚回后边界位置必须仍由原 tick/lane/value 投影得到，最终 Selection 结果不变。
