# Midora held Preview 因果 Gate 需求追踪

日期：2026-08-07
状态：非 UI 纵向切片已实施并通过自动回归；交互时延仍需后续 WPF 接线时做人工验收
关联决定：Q-NUI-011、Q-NUI-022、Q-NUI-031、ADR-AUDIO-005
需求依据：SRS §9.17.10、§12.2.4、§13.22.7、§13.24.5、§13.30、INV-018、INV-039

## 输入与正式输出

- 输入入口包括 Event Instrument / SubVoice 虚拟键盘、Segment Editor Pitch Ruler，以及放置单个 Logical Note 时的草稿预览。所有入口都解析正式 Project 绑定并进入 Preview Compiler，不发送裸 MIDI。
- Gate Start 时，Mapping 固定读取 `MappingContext.GateLength = long.MaxValue`；该值只表示 Gate 尚未结束，不作为 Project Note 长度、持久化字段或导出值。
- Gate End 时，交互层冻结用户实际 Gate Length；Release/Tail 的因果变化从音频 producer 尚未渲染的第一个 sample frame 起生效。已消费和已经进入 Render-Ahead ring 的 PCM 不回写。
- `HeldPreviewGateEndReport` 报告冻结 Gate Length、Gate End 输入时已消费 frame、producer frontier，以及仍排队的 frame / 毫秒数，供后续 UI 展示实际交互延迟。

## 边界与因果续接

- Render-Ahead producer 可以在安全前沿暂停；WASAPI callback 仍可消费已进入 ring 的连续 PCM。计划替换以该 producer frontier 为唯一 splice frame。
- splice 会根据 causal prefix 重建各 Port/Channel/pitch 的活动 Note 计数，并在 frontier 的后续事件之前插入逐实例 NoteOff；不得提前影响 ring 内 PCM，也不得把同音高重叠压成一次释放。
- Gate 未结束时使用有界窗口编译，当前内部策略是 8 秒窗口、剩余不多于 4 秒时续接。续接先暂停 producer，再从同一 preview origin 编译下一窗口、替换未渲染后缀并恢复；窗口值是 Q-NUI-031 的小决定，不进入 Project 或公共文件契约。
- Segment Note 草稿使用 Segment 的 ProjectStart、ContentOffset、参数 Lane、Track 绑定和草稿音高/力度；预览不得把草稿写入 Project。成功 Gate End 后可先释放编辑锁，再提交冻结的 Note；Release/Tail 仍使用已冻结计划继续播放。

## 失败条件与清理

- 无效 Track/Segment/Instrument 绑定、不可消费的 canonical 结果、SoundFont/设备不可用、计划损坏、generation 不一致、暂停/替换/恢复超时、callback/ring/renderer fault 或 Worker 异常退出均为结构化预览失败。
- Pointer capture 丢失、窗口失焦、取消、Stop、Project 对象失效或预览错误必须进入 Stop/Reset 清理，释放任务 lease、编辑锁、计划和活动音符。
- 放置 Note 的预览启动或 Gate End 失败不得阻断合法 Project Note 提交；提交本身失败时必须停止并清理预览，且保留编辑失败为主错误。
- 活动输出设备丢失沿用 Q-NUI-028：Worker 受控断开并要求用户显式重新指定设备，不自动静默切换。

## IPC、所有权与持久化

- 共享控制 ABI v3 在 v2 seqlock 基础上增加 `HeldPreviewPaused` 状态、pause/apply-plan/resume 命令和 generation 确认；当前 ABI v4 原样保持这些字段与命令。offset 68 的状态序列及其并发规则不变；offset 88 保存当前 held plan generation。
- 新 generation 的计划是有 SHA-256 校验的当前 MDAP v4 文件，位于会话私有临时目录；实时 PCM 不跨进程。Worker 只消费 sample-domain 计划，不读取 Project 或重建 Mapping/Lifecycle。
- `.midora` 不保存 preview plan、generation、producer frontier、ring 内容、Gate End 报告或任何预览草稿。它们全部属于运行时状态。

## 自动验证

- Compiler：Gate Open 哨兵、Gate End 冻结长度与 producer tick 分离、Segment 草稿时间/参数上下文、无 Project 副作用。
- Playback/Application：Event Instrument、Pitch Ruler、Note placement 共用入口；因果窗口续接；准确 frontier NoteOff；延迟报告；任务互斥；预览失败与提交隔离。
- Audio/BASS：producer pause/drain/resume、计划 splice、同音高活动计数、renderer future-plan 替换、ABI v3 引入且由当前 v4 保持的 generation/status、托管与 Native AOT Worker pause/apply/resume 往返。
- 2026-08-07 专项完整回归：Compiler 224/224、Playback 60/60、Application 238/238、BASS 148/148，0 failure、0 skip。

## 明确非目标

- 不回写已经消费或已经缓冲的 PCM，不承诺因果结果与事后固定长度重新编译逐 sample 相同。
- 不为 held Preview 静默降低用户 Render-Ahead，不新增低延迟专用音频路径，也不允许 UI 绕开 canonical pipeline。
- 不在本切片实现 WPF pointer capture、键盘视觉状态、时延展示或 Note 提交手势；这些属于后续 UI composition。
