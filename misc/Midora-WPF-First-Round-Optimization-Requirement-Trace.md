# Midora WPF 第一轮优化需求追踪

## 输入

- 产品需求：`misc/user/第一轮大型 Midora Desktop WPF 端优化需求.md`
- SRS 相关章节：第 6、8、9、11、13、17、18、20、22 章。
- 现有实现：`Midora.Desktop`、`Midora.Desktop.Presentation` 与对应测试。

## 正式输出与状态归属

- Project 正式输出仍只通过 Domain → Validation → Compilation → Canonical Result → Consumer；本轮不改变可听语义或 `.midora` 格式。
- Arrangement 与 piano-roll 的 Grid、Snap、默认创建长度/力度属于 Project Session UI State，不持久化，不进入 Project Undo。
- Note velocity、参数点、Segment/Note 创建、移动和 Resize 属于 Project 编辑；一次完整手势形成一次 Undo。
- 瞬时状态消息属于运行时 UI 状态；不进入 Project、诊断持久化或 Undo。

## 边界与失败条件

- Grid 分数相对于全音符，tick 步长为 `max(1, Ceiling(TPQ * 4 * numerator / denominator))`；Snap 关闭等价于 1 tick 操作步长。
- 需求原文中的 `3/12（附点16分）` 数学上等于 `1/4`，与名称及相邻预置冲突。本轮按音乐时值正确值 `3/32` 实现。
- 新建 Segment 可在目标空隙内静默缩短，但现有 Segment 的移动/Resize overlap 继续整体拒绝。
- 越界拖动使用整个选择共享 delta 的 clamp；不会改变对象间相对关系。
- SubVoice Pitch Ruler 是 Selected SubVoice 试听，不得用外层 Logical Note 触发语义。
- Integer 参数的指针输入先取最近整数再验证；Enum 仍只允许离散值。
- 放置 Note 只显示视觉预览，不启动 Held Preview。
- Held Preview 的独立低延迟音频路径属于后续性能设计；本轮不实施，也不绕过 canonical 预览管线。

## 诊断与 UX

- 只有阻止安全继续、要求决策或持续影响全局工作流的问题使用 Notice/Blocking Dialog。
- 普通成功、信息、可恢复锁定和小错误进入状态栏最右侧；错误使用红色，其他使用次要文本色。
- SoundFont 配置状态位于状态栏左侧第二单元；运行时 RID/.NET 版本不显示。

## 明确非目标

- 不改变 Compiler、Canonical Result、MIDI Export 或 Audio Render 的音乐语义。
- 不实现 Held Preview 的替代裸 MIDI 或非 Decode Stream 架构。
- 不增加 UI 状态的 `.midora` 持久化。

## 实施结果

- Arrangement 使用独立的 Editor Settings；所有 Segment 与 SubVoice 共用另一份 Editor Settings。Display Grid、Operation Step、Snap、默认长度和默认力度均为会话态，默认值分别为 `1/4`、`1/16`、启用、`1/4` 和 `100`。
- `Bar` 不再折算成固定 tick 数；显示与吸附均依据 Project Time Signature Map 的实际小节边界，包括拍号切换产生的截短小节。绝对位置和 delta 分别使用对应的 `Ceiling` 规则。
- Segment/Note 的创建、移动、Resize、框选、Time Range、Edit/Playback Cursor 统一接入操作网格；多选移动使用共享 delta，越界时整体 clamp。Arrangement 空隙内创建会静默缩短到可用正长度。
- Draw 模式增加虚线创建预览；点击已有 Note 会复制其长度作为后续默认值。放置 Note 不再触发音频试听。
- Segment/SubVoice 增加 Velocity editor；左键自由绘制、右键直线绘制，一次完整手势对应一次原子 Undo。参数/Event Curve 只显示一个活动 Lane，并使用实际取值轴、网格与独立纵向缩放。
- SubVoice 左侧 Pitch Ruler 使用直接 SubVoice pitch preview，不再经过外层 Logical Note 转调；底部 Event Instrument 键盘不再因开始试听后控件禁用而提前丢失释放事件。
- Project SoundFont 选择后立即刷新运行时验证状态；Project Settings 的无 SoundFont 提示改为条件显示，布尔值和枚举值改为 ComboBox。
- 完成精确点/区间命中区、Ctrl+滚轮钢琴键纵向缩放、选择颜色层级、鼠标光标、底部面板 splitter/高度保持、菜单勾选、项目树/时间线上下文菜单、标题修改星号、工具栏顺序与图标、导出按钮与状态栏等 UI 调整。
- 普通可恢复消息进入状态栏最右侧；SoundFont 状态移至左侧，移除 RID/.NET 状态文本。阻塞 Notice 仅保留损坏对象保存和 Mapping 草稿决策。
- Space 的播放/停止分支位于窗口 PreviewKeyDown；文本编辑、展开菜单、Popup 和 Modal/任务锁定时不抢占输入。播放启动后保留快捷键意图，用户转入文本控件或 Tab 导航时解除。

## 验证证据

- Debug 针对性测试：`Midora.Desktop.Tests` 25/25、`Midora.Desktop.Presentation.Tests` 18/18、`Midora.Application.Tests` 283/283、`Midora.Compiler.Tests` 254/254，共 580/580 通过。
- Release 全量测试：`midora-desktop.slnx` 43/43；`midora-core.slnx` 844/844；合计 887/887 通过。构建输出无警告、无错误。
- computer-use 使用 `D:\MIDI\Midora Projects\Test\TestProject2.midora` 实际复核：项目重开后显示 `SoundFont Configured`；Project Settings 显示内嵌 `Roland XP-80 Layer0.sf2` 且不显示无 SoundFont 提示；布尔/枚举字段为 ComboBox；SubVoice 的 Grid/Snap 行完整显示；Velocity 区与钢琴区横向对齐；View 菜单勾选可见；Event Instrument Library 右键菜单范围正确；SubVoice 左侧音高试听未出现错误弹窗；首次 Space 可进入 Playing，Stop 按钮可停止。
- 当前 computer-use 运行时在音频 Worker 启动后不再把后续键盘事件送达 Midora 主窗口，因此不能把“同一轮实际窗口连续按两次 Space”记为通过；此限制已与应用内首次 Space 处理、播放状态及 Stop 按钮结果区分记录。

## 保留项

- 产品所有者明确延期的 Held Preview 独立低延迟音频路径仍未实施；本轮只修复正确性、输入释放和错误路径，不宣称已完成低延迟架构。
