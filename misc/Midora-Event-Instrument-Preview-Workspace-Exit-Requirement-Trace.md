# Event Instrument Preview Workspace Exit Requirement Trace

## 输入与触发

- 输入是顶层 Active Workspace 从 Event Instrument Workspace 变为其他 Workspace 或关闭为 `null`。
- 只有底部 Preview Keyboard 通过 Held Event Instrument/SubVoice Preview 路径建立的显式任务所有权可触发自动停止。

## 正式输出与边界

- 若键盘 Held Preview 仍处于 Gate-open 或 release-tail 活动期，停止该完整预览任务并释放其 Project edit lock。
- 主时间线播放、普通 Event Instrument/SubVoice Preview、Segment Preview、Segment Pitch Ruler/Note Preview 均不是该清理目标。
- 停止失败作为运行时错误显示，但不得改写 Project 或把普通播放误判成键盘预览。

## 持久化与非目标

- Workspace、键盘预览所有权与停止结果均为会话运行时状态，不持久化、不进入 Undo/Redo。
- 不改变 Preview 编译、canonical、生命周期或声音结果，只收紧 Workspace 离开时的清理时机。
