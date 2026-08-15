# Midora SubVoice 事件入口与编辑器会话状态决定

日期：2026-08-15

## 1. 状态

已由产品所有者明确决定，并在当前开发版本实施。

本记录不修改 SRS 原文。它记录开发期的新决定，以及与现行 SRS 的已知差异，供后续统一修订规格时追踪。

## 2. 决定

### 2.1 SubVoice 事件入口

- “Add Event...”只为当前 SubVoice 创建指定 MIDI 事件类型的空入口。
- 空入口由一个具有稳定 ID 的 `SubVoiceEventMapping` 表示，因此属于 Project Source Data，并随 `.midora` 持久化。
- 创建入口时不得创建任何 `TemplateEvent`，尤其不得在 tick 0 自动插入事件。
- 后续在 Event Lane 中显式绘制或添加点时，才创建正式 `TemplateEvent`。
- 同一 SubVoice、同一 `TemplateEventMappingTarget` 只允许一个入口；重复创建必须拒绝。
- 创建和撤销入口是单个原子 Project Undo 操作；失败不得留下部分数据。

这项决定取代当前 SRS 18.4.5 的“空 Lane 不持久化”规则。初始状态仍与 tick 0 普通事件严格分离，不受本决定影响。

### 2.2 Grid / Snap 会话状态作用域

- Arrangement 保持自己的 Grid / Snap 会话状态。
- 所有 Segment Editor 继续共用 Segment 钢琴卷帘的一套会话状态。
- Event Instrument Editor 不再复用 Segment 钢琴卷帘状态。
- Event Instrument 内每个 SubVoice 各自持有一套钢琴卷帘状态；切换 SubVoice 时恢复该 SubVoice 在当前 Workspace 会话中的值。
- 每个 Segment 的下部 Logical Parameter Lane 另有独立 Snap Enabled 与 Operation Subdivision；Display Grid 仍读取该 Segment 上部钢琴卷帘设置。
- 每个 SubVoice 的下部 Event Lane 另有独立 Snap Enabled 与 Operation Subdivision；Display Grid 仍读取该 SubVoice 上部钢琴卷帘设置。
- 下部 Lane 的 Snap 控件位于 “Add Lane”/“Add Event...”按钮左侧。

这些设置全部是 UI 会话状态：不修改 Project，不进入 `.midora`、Application Preferences 或 Undo / Redo；关闭或替换 Project 后清除。

这项决定取代当前 SRS 17.2.3、18.2.4、18.4.2 和 20.1.4 中 Segment 与 SubVoice 共用钢琴卷帘 Grid / Snap / 默认值的规则。除作用域变化外，Subdivision 算法、Bar 计算、Snap Disabled 为 1 tick 等规则不变。

### 2.3 事件点交互与视图边界

- Draw 模式下，事件/参数点命中后使用垂直调整指针；Alt 强制画线时不显示单点命中高亮。
- 未按 Alt 且未命中已有点时，显示按当前下部 Lane Snap 量化后的待创建点预览。
- 拖动已有点时，预览值和最终值增量必须使用当前可见值域的同一 `Y <-> normalized value` 变换；不得用固定 Lane 高度近似，以免垂直缩放后鼠标与点位移不同步。
- ToolMode 发生变化时立即重新计算当前悬停指针，不等待下一次 MouseMove。
- 钢琴卷帘垂直滚动范围按完整可见键数计算；允许最后一个 MIDI 键完整显示，超过 128 键的剩余高度绘制为空白，不生成越界键。
- Event Instrument 底部预览键盘固定覆盖 MIDI Note 0～127，从左到右为 C-1～G9，只在 C 键标注 C-1～C9。

### 2.4 Mapping Chain 菜单

- Mapping Chains 标题区不再提供 Copy / Paste 按钮。
- 每个 Mapping Chain 项通过右键菜单提供 Copy、Paste、Delete。
- Note Mapping Chain 的 Delete 必须禁用；其他 Chain 的 Delete 继续使用现有 Mapping Chain 删除/清空命令及确认边界。

## 3. Requirement trace

| 项目 | 约束 |
|---|---|
| 输入 | 用户选择的 SubVoice、MIDI 事件目标；当前 Workspace/SubVoice 的 Grid、Snap、工具和视口状态；鼠标位置及修饰键 |
| 正式输出 | 创建或删除后的 `SubVoiceEventMapping`；显式编辑后产生的 `TemplateEvent` 或 Logical Parameter Point |
| 边界 | tick 非负；MIDI Note 0～127；事件值按目标正式范围约束；同一入口唯一 |
| 失败条件 | SubVoice/目标不存在、重复入口、非法目标、冲突事件点或命令准备后源数据改变 |
| 诊断 | 领域命令异常由现有同步编辑错误通道显示；不以静默修复替代正式失败 |
| 持久化归属 | 空事件入口属于 Project Source Data；Grid/Snap、工具、视口和悬停预览属于当前 UI 会话 |
| 运行时归属 | 指针、创建预览、拖动预览、滚动范围及钢琴键布局只属于 Presentation |
| 明确非目标 | 不创建隐式 tick 0 事件；不把 UI 设置写入 Project；不改变 Compiler/Canonical Result 对现有事件的解释；不修改 SRS 原文 |

## 4. 验证门

- 创建空入口后 `TemplateEvent` 集合保持逐项不变，Mapping 存在，Undo 后恢复原状。
- 创建命令后的 Incremental Compile 与 Full Compile 结果一致。
- Segment 与 Event Instrument、不同 SubVoice、上部钢琴卷帘与下部 Lane 的 Snap 状态互不串扰。
- Event Lane 点的 Draw 指针、Alt 画线和缩放后的垂直拖动使用一致的命中与坐标变换。
- 非整 Lane 高度和超过 128 键高度下，垂直视口不访问 MIDI Note 范围外的值，Key 0 可完整显示。
- Mapping Chain 右键菜单可用，Note Chain 的 Delete 不可用。
