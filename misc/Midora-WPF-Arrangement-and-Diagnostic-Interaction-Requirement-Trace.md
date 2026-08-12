# Midora WPF Arrangement and Diagnostic Interaction Requirement Trace

状态：Implemented for desktop verification
日期：2026-08-12

## 输入

- 当前 Project 的 TPQ、Logical Track 手工顺序、Segment 区间与 Event Instrument binding。
- Arrangement 当前 Tool、Grid / Snap、鼠标 placement / drag / context-menu 手势。
- Compiler 最后一次尝试的 Diagnostic 集合与稳定 `SourceReference`。
- Segment / SubVoice Note 批量移动请求的共同 tick / pitch delta。

## 正式输出

- Segment 创建、Logical Track create / rename / reorder / bind / delete、Logical / Template Note move-or-delete 均通过 `IProjectEditCommand` 修改 Project，并形成一个可 Undo 的提交；具体 Track Header 的 create 插入目标 Track 后方，空白 Header 和其他入口的 create 追加到末尾。
- Compiler Diagnostic 文本为英文；Project Panel 计数投影自最后一次编译尝试。
- Track Header 的绑定乐器副标题、hover / pressed、拖动插入线和非法 pitch 的安全 lane 仅为 Presentation 输出。

## 边界与失败条件

- 新 Segment 与同 Track 既有 Segment 重叠时，按目标空隙裁剪；无正长度空间则不提交。
- rebind 已绑定 Track 前要求用户确认；取消不修改 Project。
- 普通 Note 移动删除 pitch 越出 `0..127` 的 Note；复制拖动仍共同 clamp。其他非法时间、长度或 velocity 在修改前拒绝。
- 诊断来源已删除时仅报告不可导航；非法 pitch 只影响安全显示位置，不修复或静默改写源数据。

## 诊断与持久化归属

- Compiler Diagnostic 与 Error / Warning 计数不保存、不进入 Undo。
- Track Header transient state、drag payload、菜单 target 和 viewport 不保存。
- Track order、binding、Segment 与 Note 编辑属于 Project Content，按既有 `.midora` 源数据格式保存；本次未改变持久化格式。

## 运行时归属与非目标

- Track Mute / Solo 继续属于 runtime monitoring state，本次 Track Header 重排和 binding 不改变其语义。
- 本次不改变 Compiler canonical 语义、播放/导出/渲染消费者、复制拖动的原子性或 Event Instrument Library 手工顺序。
