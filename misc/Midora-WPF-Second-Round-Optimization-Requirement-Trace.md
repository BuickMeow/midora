# Midora WPF 第二轮优化需求追踪

日期：2026-08-10
输入：`misc/user/第二轮大型 Midora Desktop WPF 端优化需求.md`

## 1. 正式输入与输出边界

- 正式输入仍是当前 Project 的 Logical Track、Segment、Logical Note、SubVoice Event、Logical Parameter Lane 与稳定 ID。
- 本轮只修改 Desktop UI 的视图投影、编辑手势和 Project Session UI State；所有编辑仍通过现有命令提交到正式 Project 模型，并继续触发验证、编译失效和 Undo。
- Arrangement 的 Segment Note Preview 是只读、手工渲染的视图缓存，不进入 Project、`.midora`、canonical compiled result 或 Undo。
- Velocity 编辑只改变既有 Note/Event 的 velocity；不得改变 tick、length、pitch 或选择集。
- 状态栏“已读”只清除当前 transient status message，不清除 Diagnostics，不修改 Project。

## 2. 交互与视图边界

- Velocity 普通点击按横坐标命中该时刻覆盖的柱；无选择时影响本次手势经过的全部柱，有选择时只影响经过且已选择的柱。只有单柱顶部边缘拖动使用上下调整语义和 `SizeNS` 指针；柱体及左右边缘保持默认指针。
- Velocity 左键自由轨迹和右键直线插值均在 Pointer Down 时立即应用首个采样点；一次完整手势只形成一次 Undo。
- Piano Roll 垂直视口严格限制在 MIDI pitch `0..127`。Velocity、Parameter 和 Event Lane 使用有界数值视口；缩放、滚轮、拖动与右侧滚动条必须操作同一视口状态，标尺随之更新。
- 数值标尺顶部和底部标签限制在可视区域内，不能裁切。
- Segment 下部 Velocity / Logical Parameter 区域可隐藏、恢复并调整高度；状态只属于当前 Project 会话。

## 3. Arrangement Segment Preview

- 每个 Segment 以固定 pitch `0..127` 投影 Note；横向位置和长度按 Segment active crop window 映射，纵向位置按 pitch 映射。
- Note 最小可见高度为 1 个设备无关像素，坐标执行布局取整；tick `0` 的 Note 不能因左边界裁切或索引查询而遗漏。
- Preview 使用单个 `TimelineSurface` 手工绘制，不为 Note 创建 WPF Control。
- 缓存以 Segment 稳定 ID 和预览相关内容指纹为键；只有 Segment 的 crop、length 或 Note 预览内容变化时重建该 Segment 缓存。缩放、平移、选择和播放指针变化只重绘，不重建预览内容。

## 4. UI 验收

- 顶部位置与 BPM 使用亮色并由竖向分割线隔开；Play 图标完整显示。
- Disabled Ghost Button 同时移除背景和边框。
- Timeline 选择框使用更亮的红色边框；Piano Roll 白键行较亮、黑键行较暗；Parameter / Event Lane 不出现额外白色外框。
- Grid / Snap 组合框的选择框只显示 `Bar` 或简写分数（例如 `1/8`），不得出现额外验证色块或空选择。工具栏顺序统一为 `Grid + 下拉 | Snap + 下拉 | Length [Vel] | - + | 工具`。
- Piano Roll、Velocity、Parameter 和 Event Lane 在右侧显示与当前纵向视口同步的滚动条。
- 状态栏 transient message 右侧提供“已读”按钮。

## 5. 失败条件、诊断与非目标

- 非法自定义 subdivision、越界数值和无效编辑仍由既有验证/命令边界拒绝；不得提交部分 Project 修改。
- 本轮不改变音频、Preview、MIDI、渲染、Mapping、Lifecycle 或 canonical 编译语义。
- 本轮不新增 `.midora` 字段，不把 UI 缓存或会话布局持久化到 Project，也不引入控件式 Note/Event 列表。
- 本轮不承诺非 100% DPI 的专项布局调整；但继续使用 WPF layout rounding，避免本轮引入明显的半像素模糊。
