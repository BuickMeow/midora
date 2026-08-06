# Midora Close / Exit 非 UI 生命周期 Requirement Trace

状态：已实现并进入回归验证；Q-NUI-017 待确认。  
日期：2026-08-06

上位规范：SRS 第 3.6.4、3.9.4、19.2.2、19.2.5、19.10 节，以及 INV-004、INV-018、INV-024。

## 1. 输入与正式输出

- 输入：当前打开的 `ProjectCompilationSession`、`ApplicationTaskCoordinator`、New/Open/Close/Exit 命令、播放 cleanup 结果、Function Draft 决定、未保存 Project 决定、普通 Save 结果及调用方提供的实际 Project switch 动作。
- 正式输出：既有结构化 Project Switch Guard 结果，以及与当前 Project 实际开闭状态一致的工程总耗时运行/Closing pause 状态。
- Close Project 成功后的无 Project 空状态、Exit 的进程 shutdown 和 WPF 窗口行为属于后续 UI composition；非 UI 层只提供不允许绕过的任务锁、Guard 顺序和关闭时钟边界。

## 2. 顺序与边界

```text
Stop / playback cleanup
→ Resolve Function Drafts
→ Acquire Project Edit Lock
→ Resolve and optionally Save unsaved Project
→ BeginProjectClosing
→ Perform actual Project switch / close / exit
```

- Stop、Draft、确认和普通 Save 期间当前 Project 仍然打开，工程总耗时继续累计。
- `BeginProjectClosing` 紧邻实际切换动作；成功后保持暂停，直到旧 `ProjectCompilationSession` 被释放。
- Draft/未保存阶段 Cancel、Save unavailable 或 Save 失败都未进入 Closing pause。
- 实际切换失败或取消会执行 `CancelProjectClosing`；恢复后的时间从当前单调时钟重新起算，不补计切换尝试期间的暂停窗口。
- Save Copy 不是 Guard 的 Save 分支，不能替代关闭前普通 Save。

## 3. 失败与原子性

- 播放 cleanup 风险继续使用与原命令绑定的一次性 continuation；未经确认不进入 Guard 主体。
- 嵌套普通 Save 一旦开始不可取消；其失败使 Project switch 失败，旧 Project 保持打开且未进入 Closing pause。
- 只有实际切换动作完整返回才保持 Closing pause；异常返回会恢复旧 Project 计时并由应用任务结果保留原始错误。
- Project Edit Lock、全局任务槽和取消源仍由 `ApplicationTaskCoordinator` 的 `finally` 路径释放。

## 4. 持久化与运行时归属

- 工程总耗时的已累计整毫秒值属于 Project Metadata；Closing pause reason、任务状态、Guard 决定、cleanup error 和 switch continuation 只属于运行时。
- 自动累计不单独标记 Modified、不进入 Undo/Redo、不更新 metadata 修改时间，也不影响 canonical fingerprint。
- 不持久化关闭不保存、丢弃 Draft、应用退出请求、任务阶段或 UI 空状态。

## 5. 明确非目标

- WPF 确认对话框、主窗口关闭事件、无 Project 空页面和导航。
- autosave、crash recovery、自动恢复、多 Project 或后台关闭队列。
- 在未通过 Guard 前释放旧 Project，或在失败后自动恢复播放。

## 6. 自动化验证门

- Guard 中的不可取消 Save 期间工程时长继续累计。
- 实际切换动作一进入即暂停；动作成功后继续保持暂停。
- 未保存确认 Cancel 不暂停现有打开会话。
- 实际切换抛错后恢复累计，并且不补计失败动作占用的暂停窗口。
- 既有 Stop、Draft、Save、编辑锁、cleanup continuation、Cancel 与 Save unavailable 顺序测试继续通过。
