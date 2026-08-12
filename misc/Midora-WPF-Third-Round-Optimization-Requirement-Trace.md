# Midora WPF 第三轮优化需求追踪

日期：2026-08-10
输入：`misc/user/第三轮优化需求.md`

## 1. 正式输入与输出边界

- 正式输入仍是当前 Project 的 Logical Track、Segment、Logical Note、SubVoice Event、稳定 ID 和既有 Timeline 编辑设置。
- 本轮只修改 Desktop UI 的工具路由、鼠标手势、transient 预览和 WPF 控件呈现。合法编辑仍通过既有命令提交到 Project 模型，并继续遵守验证、编译失效和一次完整手势一次 Undo。
- 创建、移动和 Resize 预览只属于 transient UI state，不进入 Project、`.midora`、canonical compiled result 或 Undo。
- Current Tool、Grid / Snap 显示文本、滚动条位置和控件布局仍属于会话或视图状态，不新增持久化字段。

## 2. 工具模式与编辑手势

- Arrangement、Segment Piano Roll 和 SubVoice Piano Roll 的工具按钮互斥且始终恰有一个激活；重复点击当前工具不清空工具状态，新 Workspace 仍默认 Select。
- Draw 是 Segment / Note 的直接编辑工具：空白处创建，主体拖动，左右边缘 Resize；命中对象或执行直接编辑时隐藏创建预览，并显示提交后的位置与长度。
- Draw 模式下在 Arrangement Segment、Segment Logical Note 或 SubVoice Template Note 主体执行 `Ctrl+Drag` 时，复制当前选择集并以同一个时间及 Track/pitch delta 放置副本；原对象不移动。复制意图在越过拖动阈值时确认，普通 `Ctrl+Click` 仍切换选择，边缘 `Ctrl+Drag` 仍是 Resize。成功后只选择副本，完整手势形成一个 Project Undo。
- Select 在 Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 中只通过框选改变对象选择；单次左键按下即使位于 Segment / Note 上也从该点开始框选，不再保留单对象点击选择。Draw 仍保留对象点击选择，并负责直接拖动、Resize 与创建；既有双击导航不受影响。
- Draw 在空白处使用默认指针，在对象主体和边缘使用移动与水平 Resize 指针；Select 使用十字指针且命中对象时不切换为编辑指针；Split 命中 Segment 时使用 I-beam 指针。
- 无修饰键 `D`、`S`、`E` 分别切换活动 Timeline Workspace 的 Draw、Select、Erase；文本/代码输入、ComboBox、菜单、Popup、内联编辑和 Modal 优先处理输入，不切换背景工具。

## 3. 视图与控件验收

- Arrangement Segment 使用更深的低饱和蓝灰色，以提高内部 Note Preview 对比度；Note Preview 使用更亮的蓝灰色，选中 Segment 使用同色系边框和更深背景。Segment Piano Roll active range 保留原界外基础底色，界外范围进一步压暗，未选中 Note 使用更亮的蓝灰色，选中 Note 保留当前红色填充与边框。
- Velocity 未选中柱使用与 Piano Roll Note 一致的较亮蓝灰色；对应选中 Note 的柱使用红色。该早期“按 Note 长度画柱体”的形状已由 2026-08-12 的 ADR-UI-022 取代：每个 Note 在 start tick 显示固定窄柱与更大的方形 onset marker。
- Grid / Snap 组合框选择后，显示文本立即更新为实际生效的 `Bar` 或简写分数。
- ComboBox 下拉指示使用 Fluent System Icons chevron，垂直居中；不使用字体符号。
- Segment 与 SubVoice Piano Roll 的显式垂直 ScrollBar 必须完整显示 Track / Thumb，并继续与现有纵向视口同步；Thumb 长度按当前可见 pitch 行数相对 MIDI `0..127` 的比例计算，不得使用固定 `ViewportSize`。
- Segment 下部 Velocity / Parameter Lane 的分隔条必须在“Piano Roll + Timeline Overview”整体上部区域与下部编辑区之间调整高度；不得把固定高度的 Timeline Overview 当成单独调整目标。拖动后实际改变下部编辑区高度，并保留该会话高度。
- Bottom Panel Diagnostics 的三个 ComboBox 和 Segment Piano Roll 顶部左侧两个文本不得裁切，并保持垂直居中。
- Timeline 为空时不显示 `No timeline content` 覆盖框；空白画布本身就是可编辑空状态。
- 右下角错误 Status Message 保留省略显示，但必须提供“详情”按钮；详情窗显示完整只读文本，支持选择、`Ctrl+C` 和显式 Copy。
- Segment 与 SubVoice Piano Roll 的左侧 Pitch Ruler 使用完整白键底板和较短黑键叠层的真实钢琴键外观；音名只标在每个八度的 C 上，并按 MIDI 60 = C4 显示 `C-1` 至 `C9` 范围内实际存在的 C。

## 4. 规格变更与兼容边界

- 本轮产品需求明确批准 `D` / `S` / `E`，因此修订 SRS 20.12.13 原有“禁止全部 Letter-only tool shortcuts”条目；这三个键是唯一例外，其余单字母工具快捷键仍禁止。
- 工具快捷键不改变 Project 数据、可听结果、文件兼容性、canonical 编译、播放、预览、MIDI 导出或音频渲染语义。
- 本轮不改变 Grid / Snap 的 tick 计算、默认值、会话共享范围或持久化归属，只修复选择后的文本投影。

## 5. 失败条件、诊断与非目标

- 播放或全局任务锁期间的编辑禁用、非法 overlap、越界 tick / pitch 和无效 Resize 仍由既有命令与验证边界拒绝，不得提交部分 Project 修改。
- 可预期的工具快捷键 No Action 不弹窗；文本或弹出层拥有输入焦点时，字母键按本地输入处理。
- Copy Drag 的时间、pitch 与 Track 越界请求按选择集计算一个共同 clamp；Segment overlap、选择集不兼容或共同 clamp 后仍非法时整体拒绝，不产生部分副本。Escape、Pointer Capture 丢失或未越过拖动阈值均不创建副本。
- 本轮不修改音频 Worker、held Preview、SoundFont、Mapping、Lifecycle、持久化格式或高性能手工渲染架构。
- 本轮不扩大初版 DPI 验收范围；继续以 Windows 100% Display Scaling 和 Midora Built-in Theme 为正式视觉验收环境。
