# Midora WPF 时间线编辑、播放与渲染诊断 Requirement Trace

## 范围

本增量修复以下既有初版能力中的实现缺陷：

- Arrangement / Segment Editor 的选择、删除、双击与视图状态；
- Timeline Context 中 `Space` 的 Play / Stop 状态切换；
- Segment local tick 与 Project absolute tick 的播放光标映射；
- Segment 硬边界的 Note Off、All Sound Off 与 Reset；
- Audio Render 对合法短读的处理，以及失败结果中的输出级原因和源定位。

## 需求依据

- 《Midora SRS》§11.6、§11.7.4、§11.10.2～11.10.4、§11.15.4、§11.22：Segment 是 Project 时间线容器；相邻 Segment 不自动连接，边界保留 Reset；删除进入一次 Project Undo。
- 《Midora SRS》§12.3、§12.10、§12.11：范围统一为 `[startTick, endTick)`；Segment End 是硬边界；Note Off、All Sound Off / Reset 必须在同 tick 上先于新实例状态和 Note On。
- 《Midora SRS》§13.3、§13.6、§13.12.3～13.12.7：初版没有 Pause；Stopped 从当前播放光标播放；Stop 和 Stream 复用前必须消除残留声音与状态。
- 《Midora SRS》§15.13、§15.16.5、§15.17：渲染失败必须关联具体输出、原因和诊断，不能只显示顶层 `Failed`。
- 《Midora SRS》§17.2.3、§18.1～18.2、§20.1～20.4、§20.12.6：Zoom / Scroll 属于 Project Session UI State；Segment Editor 使用 local tick；不可命中的概览对象不得进入选择；Timeline Context 中 Space 在活动播放状态执行 Stop。

## 输入与正式输出

| 输入 | 正式输出 / 状态 |
|---|---|
| Timeline 命中、框选、双击和键盘事件 | 合法且与当前 Workspace 选择域一致的 UI 命令 |
| Segment local cursor tick | 映射后的 Project absolute playback tick |
| Project、CompileContext、Segment 边界 | Canonical Compiled Result 中精确的边界清理事件 |
| Audio Render Task Result | 顶层状态、逐输出状态、编译诊断和运行期诊断的稳定摘要 |
| 音频源合法的 `Continue + partial frames` | 写入实际返回帧并继续拉取，直到精确达到冻结总帧数 |

## 边界与失败条件

- Arrangement 的 Note Preview 是不可命中的渲染概览，不得进入 Segment 选择或传给 Segment 编辑命令。
- Track Header 不属于时间线内容 Canvas；双击 Header 不得创建 Segment。
- 普通选择刷新不得修改 `StartTick`、`TickSpan`、垂直位置或 lane height。
- Segment local tick 到 Project tick 的映射固定为 `ProjectStartTick + (localTick - ContentOffsetTick)`，并检查非负与溢出。
- 相邻 Segment 即使复用同一 canonical Channel Unit，也必须在边界完成旧 Fragment 的声音和状态清理，再初始化新 Fragment。
- Render 的输出级 Error 不能只保存在 `AudioRenderOutputResult.Diagnostics` 而被结果窗口遗漏。
- 音频源为服从 MIDI 事件/Fragment 边界而返回非零短块时不属于提前结束；只有无进展、Fault 或总帧数前 EndOfStream 才失败。

## 诊断与状态归属

- 选择、Zoom、Edit Cursor、Playback Cursor 显示均为 Project Session UI State，不写入 `.midora`，不进入 Project Undo。
- Segment 删除和合法时间线编辑属于 Project Content，一次手势形成一次原子 Undo。
- Mute / Solo 和播放状态仍仅属于运行时。
- Audio Render 失败诊断属于当前任务 / Runtime History，不写入 Project。

## 明确非目标

- 不增加 Pause、Scrub、跨应用恢复 Zoom、Track 独立编辑 Workspace 或新的双击命令。
- 不改变 Event Instrument、Mapping、生命周期、Channel Unit 分配或输出文件格式语义。
- 不通过 UI 或音频消费者重新解释 Project；边界音乐语义仍由 canonical 编译结果提供。
