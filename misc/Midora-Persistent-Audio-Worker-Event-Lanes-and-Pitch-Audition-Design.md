# Midora 持久音频 Worker、事件 Lane 与音高试听设计记录

日期：2026-08-15
状态：已由产品所有者明确批准并实施

## 1. Requirement trace

### 输入

- 已验证的 Project SoundFont 绝对路径、Project 中保存的 64 个小写十六进制字符 SHA-256；
- canonical `MidiRenderPlan`、实时输出设备与缓存策略；
- SubVoice 的 `EventMappings`、非 Note `TemplateEvent` 及其稳定 ID；
- Segment/SubVoice 钢琴卷帘的 Pitch Ruler、Note 放置与 Note 移动手势。

### 正式输出与运行时输出

- 正式播放、正式预览仍只消费 canonical render plan；持久 Worker 不改变编译或可听语义；
- reusable PCM 缓存键直接使用已验证的 Project SoundFont SHA-256；
- SubVoice Event Lane 的入口由 `SubVoice.EventMappings` 持久化，事件点只是该入口下的内容；
- 钢琴卷帘音高试听是独立的瞬时运行时输出，不写 Project、不进入 Undo/Redo、不进入 canonical 或 PCM 缓存。

### 边界与失败条件

- 同一 Worker 同时最多执行一个正式播放/预览任务；正式任务与简单音高试听互斥；
- SoundFont 路径或已验证 SHA-256 任一变化，都立即销毁旧 Worker 并以新 SoundFont 身份创建新 Worker；
- Worker、请求交换、WASAPI 或 BASS 操作失败后，主进程丢弃该 Worker；下一次操作重新创建，不能复用未知状态；
- 普通删除事件点不删除 Event Lane 入口；删除入口只能通过左侧 Lane 菜单显式执行，非空入口需确认；
- 音高试听按键释放、鼠标捕获丢失、手势取消或后端销毁时执行 NoteOff 与 All Sound Off。

### 诊断、持久化与运行时归属

- Worker 错误按请求 generation 返回完整错误文本；进程退出仍保留 stderr 诊断；
- `.midora` 只持久化 Project 源数据和嵌入 SF2，不保存 Worker、请求 generation、试听状态或 PCM 缓存；
- Event Mapping Chain 与事件点均保留各自稳定 ID；移动不换 ID，复制生成新 ID；
- 持久 Worker、已加载 SoundFont handle、已预载 preset 集合、试听 stream 和 WASAPI 设备全部属于 Project 播放会话运行时。

### 明确非目标

- 不把 Event Instrument 编辑器底部虚拟键盘改为简单音高试听；它仍走正式 Event Instrument held Preview；
- 不允许简单音高试听解释 Program、Bank、CC、Mapping、Lifecycle、Mute/Solo 或当前 Event Instrument；
- 不修改 canonical、MIDI 导出或离线音频渲染语义；
- 不把 SF2、PCM 或普通对象图通过共享内存 IPC 传输。

## 2. 需求依据与冲突记录

- SRS 8.52 要求事件、Mapping Chain 和 Mapping Step 使用稳定 ID；本实现据此把 Lane 入口与事件点视为不同对象。
- SRS 12.22.7 要求 SoundFont 修改使 sample-domain 缓存失效；缓存键纳入 Project 已验证 SHA-256，SoundFont 变更同时更换 Worker generation。
- SRS 16.25.3 明确 Zip 压缩方式属于实现细节，并不要求每个 entry 必须压缩。产品所有者本次明确选择嵌入 SF2 使用 Store/NoCompression；其他 entry 继续使用 Optimal。
- 产品所有者本次明确要求 Segment/SubVoice 钢琴卷帘使用专用裸音高试听，并要求创建、移动 Note 时试听。这与 SRS 13.22.7、13.24.5、18.2.3、18.2.4 中“必须走 canonical held Preview”及“放置手势不得发声”的现行文字冲突。本记录保存该明确产品决定；没有静默修改 SRS。Event Instrument 编辑器底部虚拟键盘不在覆盖范围内。

## 3. 持久 Worker 拓扑

```text
Project verified SoundFont identity (path + SHA-256)
                    |
                    v
one persistent win-x64 Native AOT Worker
  - one persistent BASS SoundFont handle
  - memoized referenced-preset preload
  - zero or one formal playback renderer at a time
  - one dedicated pitch-audition MIDI stream
                    |
      shared control ring + generation-scoped files
                    |
                    v
Desktop playback backend
```

- 进程启动时加载固定基线 BASS/BASSMIDI/BASSWASAPI 和 SoundFont；每次正式任务只创建任务级 renderer/stream/ring，不重新创建进程或 `BASS_MIDI_FontInit`。
- 控制 ABI v5 增加 Probe、Start Playback、Pitch NoteOn、Pitch NoteOff、Shutdown 命令；大参数仍使用 generation 隔离的原子文件交换，控制 ring 不承载对象图或 PCM。
- Worker 在写入播放接受标记前发布本 generation 的 `Preparing`，防止主进程误读前一 generation 的终态。
- SF2 变更很少，因此不在同进程内热换 Font：直接结束旧进程并启动新进程，使 Font handle、preset memoization 和试听 stream 同时失效。

## 4. SoundFont 与缓存身份

- `ProjectCompilationSession` 同时维护 effective path 与 effective verified SHA-256；相同路径但 SHA 改变也触发身份变更。
- `AudioSegmentCacheStaging`、`AudioUnitCacheStaging`、`PlaybackSpanCacheStaging` 和文件渲染请求直接接收该 SHA；它们不再为建键读取完整 SF2。
- BASS 原生 DLL 身份仍按三个小型固定基线文件计算，因为它们也是 PCM 可听结果的一部分；这不读取 SF2。
- 外部资源验证和离线渲染的冻结快照仍可按其安全边界校验/复制文件；该行为不是播放缓存键的重复哈希。

## 5. Event Lane 编辑模型

- UI 从 `SubVoice.EventMappings` 创建 Lane，因此最后一个事件点删除后 Lane 仍存在；不再从事件点集合反推入口。
- 同一 SubVoice、同一 MIDI value target 共用入口的 Mapping Chain；批量移动/复制只改事件点，保持入口引用。
- Select 模式先发起框选；框选结果可走现有 Delete、Cut/Copy/Paste，并新增同 Lane 的批量 Move、Ctrl+Drag Copy 与 Ctrl+D。
- 显式删除 Lane 会删除对应 Mapping 入口及其事件点；Bank MSB/LSB 可删除单一分量并保留另一分量，Pitch Bend Range 的成对入口按一个语义目标共同删除。整个操作为一个原子 Undo。

## 6. 简单音高试听

- 由于当前正式 WASAPI 输出只消费 decode source，专用 BASSMIDI stream 使用 `BASS_STREAM_DECODE`，再由独立 WASAPI source 拉取；这不是渲染缓存 stream，也不改变用户要求的交互语义。
- stream 固定为单 MIDI Channel，启用 `NOFX | NOTEOFF1`、8-point sinc、CPU=0、500 sample voices，并显式建立 melodic 初始状态。
- Pitch Ruler 按下、Note 放置开始：All Sound Off → NoteOn；松开/取消：NoteOff → All Sound Off。
- Note Move 以当前选择集中最早 tick（稳定 ID 作为平局顺序）的 Note 为试听锚点；只有锚点 pitch 改变时才触发。拖动中每次变化先 All Sound Off，再发送新 NoteOn。
- 所有 Program/Bank/CC/Mapping/Lifecycle 都被忽略，使用 SoundFont 默认 preset；用途仅为辨认 MIDI key 音高。
