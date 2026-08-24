# 第 15 章 音频文件渲染

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义整曲和按 Logical Track 离线渲染、普通 RIFF/WAVE 与用户可选采样率、采样映射、BASSMIDI 离线后端、文件专用 OutputDevice 抽象、主输出链、文件事务、取消、结果状态与任务参数。

## 15.1 核心语义与专用 CompileContext
### 15.1.1 只消费 canonical compiled result
音频文件渲染系统必须消费 `canonical compiled result`。
不得：
```text
直接读取 Logical Track / Pure MIDI Track / Segment / Event Instrument 并自行展开
重新计算 Logical Parameter Mapping
重新决定生命周期、Release、Tail 或 Reset
重新决定 Port / Channel / Channel Unit 分配
自行改变同 tick 语义排序
使用运行中的播放 buffer 作为正式渲染结果
```
渲染系统可以在 compiled result 之上建立：
```text
离线调度队列
Canonical Segment / Unit 音频投影与 Unit Stream pool
预分配音频混合 buffer
输出链状态
文件写入事务
进度和运行期诊断
```
这些结构不得成为另一套音乐语义来源。
### 15.1.2 Audio Render CompileContext
初版必须使用专用：
```text
Audio Render CompileContext
```
它至少包含：
```text
渲染模式
渲染 tick 范围
参与渲染的 Logical Track / Pure MIDI Track 稳定 ID 集合或全部有效 Track 策略
整曲 / 单 Track 输出上下文
Project End Marker 默认范围策略
Warning 导致编译失败策略
诊断收集策略
```
渲染模式、范围和 Track 选择属于 CompileContext 输入。
以下内容不改变 canonical MIDI 语义：
```text
RIFF/WAVE 容器
.wav 扩展名
本次文件采样率
输出路径
覆盖决定
命名偏好
结果窗口显示方式
```
### 15.1.3 输入冻结
用户确认开始后，本次任务的以下输入必须固定：
```text
当前内存 Project 内容
CompileContext
Track 选择
范围
SF2 资源
Playback Master Volume
用于渲染的 Limiter 语义与参数
本次文件采样率
本次 Offline Maximum Sample Voices per Unit Stream
文件专用 OutputDevice 配置
输出目标列表
覆盖授权
```
系统级只要求语义固定，不强制通过完整 Project 深拷贝实现。
未保存 Project 允许渲染。渲染不要求先保存，也不自动保存 Project。
磁盘上的 `.midora` 在任务中被外部修改，不影响当前内存 Project 的本次渲染，系统不得在任务中重新读取并合并该文件。
---
## 15.2 初版渲染模式
初版支持两种互斥模式：
```text
Whole Mix / 整曲混音渲染
Per Logical Track / 按 Logical Track 分轨渲染
```
一次渲染任务只能选择其中一种，不允许在同一次任务中同时输出整曲和分轨。
初版不支持按 Port 分轨。
### 15.2.1 整曲混音
整曲模式把用户显式选择的全部有效 Logical Track 与 Pure MIDI Track 混合为一个 WAV。
默认选择：
```text
全部有效 Logical Track 与 Pure MIDI Track
```
如果没有选择任何有效 Track：
```text
不允许进入正式 Preparing / Rendering。
提示至少选择一条有效 Track。
```
### 15.2.2 按 Logical Track 分轨
分轨模式中：
```text
每条被选择且有效的 Logical Track 生成一个独立 WAV。
各 Track 按 global Arrangement Track Order 过滤得到的 Logical Track 顺序依次处理。
每条 Track 使用独立 Audio Render CompileContext。
每条 Track 独立编译、分配 Channel Unit、创建干净后端状态并执行完整输出链。
不要求保留整曲编译中的 Port / Channel 编号。
```
所有分轨使用同一渲染范围和同一最终采样长度。

Per Logical Track 模式仍只生成 Logical Track 文件；Pure MIDI Track 不产生独立 WAV，但其选择状态保持以便切回 Whole Mix。Pure MIDI 分轨音频不是本次 Pure MIDI/SMF 需求的一部分；Whole Mix、实时播放和正式 MIDI 导出必须完整包含被选择 Pure MIDI Track。
### 15.2.3 Track 选择与 Usage / Root 状态
Track Mute / Solo 不影响音频文件渲染。
显式 Track 选择是唯一决定因素：
```text
选择中的 Mute Track 正常渲染。
未选择的 Solo Track 不渲染。
Mute / Solo 不与 Track 选择取交集。
```
无内容且无 Usage 的 Logical Track 不是有效渲染目标；有内容 Track 必须有唯一有效 Usage/Definition。引用结构错误或任何相关 Damaged Placeholder 使渲染准备失败；不得静默忽略为无输出 Track。
已绑定但没有发声音频的 Track仍是有效目标：
```text
整曲模式中贡献静音。
分轨模式中生成与范围等长的静音 WAV。
```
结构合法但内容为空的 Event Instrument 不视为错误。
---
## 15.3 输出规格与可选采样率
初版音频文件渲染规格为：
```text
Container: RIFF/WAVE
File extension: .wav
Sample rate: 用户选择的 8,000–192,000 Hz 整数
Channels: Stereo
Channel order: Left, Right
Sample format: IEEE 32-bit floating point
Sample layout: stereo interleaved frames
Byte order: RIFF/WAVE 标准 little-endian
```

新 Project 和旧格式迁移的默认文件采样率为：
```text
48,000 Hz
```

用户可以为本次任务选择范围内的任意整数采样率。UI 应提供以下常用值作为快捷选择：
```text
44,100
48,000
88,200
96,000
176,400
192,000 Hz
```

同时必须允许手工输入其他 `8,000–192,000 Hz` 整数值。Container、Channels、Sample Format、Layout 与 Byte Order 不提供修改控件。

初版不计划支持其他音频格式，保持单一 RIFF/WAVE float32 stereo 路径。

### 15.3.1 RIFF 大小边界

普通 RIFF/WAVE 使用 32 位 chunk / 容器长度字段。Preparing 阶段必须根据冻结的：
```text
采样率
渲染范围对应的最终 frame 数
2 channels
32-bit float
实际需要的 RIFF/WAVE header 与 chunk
```
精确检查每个计划文件能否由合法 RIFF/WAVE 表示。

如果任一正式目标将超过 RIFF/WAVE 可表示上限：
```text
整个渲染任务在创建临时输出前以 Error 阻止。
不自动拆分为多个 WAV。
不回退 RF64、WAVE64 或其他容器。
不自动降低采样率。
不自动缩短范围。
```

这属于格式硬限制，不是磁盘空间预检查。

### 15.3.2 不做格式后处理
初版不提供：
```text
Dither
峰值归一化
响度归一化
自动增益
Fade In / Fade Out
DC Offset 后处理
渲染完成后的二次重采样
自动首尾静音裁剪
```
输出应由文件专用音频后端直接以本次选择的采样率生成，不得先固定生成另一采样率再做最终转换。
---
## 15.4 渲染范围
### 15.4.1 范围表示
音频渲染范围统一使用：
```text
[startTick, endTick)
```
规则：
```text
startTick >= 0
endTick > startTick
startTick 包含
endTick 不包含普通事件
```
零长度范围不允许开始渲染。
### 15.4.2 默认范围
默认范围遵循 Project 默认范围：
```text
存在 Project End Marker：endTick = Project End Marker tick。
不存在 Project End Marker：由有效音乐内容自然结束位置决定。
```
自然结束位置应包含：
```text
有效实例输出
必要 Note Off
允许范围内的 Release / Tail
Segment lane 激活所需 Reset，以及 Segment End / 渲染范围硬边界的资源安全 Reset
```
如果默认自然范围为零，例如空 Project 或 End Marker 位于 tick 0：
```text
不开始渲染。
提示没有可渲染的时间范围。
```
### 15.4.3 手动范围
初版支持用户指定手动 tick 范围。
手动范围可以：
```text
超出自然音乐结束位置
完全位于音乐内容之后
包含长时间静音
```
只要范围合法，超出音乐内容的部分正常输出静音。
手动范围不因 Project End Marker 修改而自动变化。
### 15.4.4 硬结束边界
手动 `endTick` 和默认 Project End Marker 都是音频渲染硬边界。
在边界处：
```text
普通位于 endTick 的事件不进入输出。
范围外的新事件不进入输出。
Release、Tail 和合成器内部残余发声不得越过边界写入 WAV。
编译器可以在边界生成必要 Note Off / Reset 语义，以安全结束。
边界清理不得额外延长 WAV。
```
初版不追加额外尾部静音，也不渲染到“检测为完全静音”为止。
### 15.4.5 文件时间零点
WAV 首个采样对应所选范围的 `startTick`。
非零 `startTick` 前不补从 Project tick 0 到起点的静音。
文件内部不保存 Project 绝对 tick 偏移。
---
## 15.5 Tempo Map 与采样位置
### 15.5.1 tick 到 sample
渲染使用 Conductor Track 完整 Tempo Map：
```text
absolute tick 与本次范围 startTick
→ 按第 4.1.4 节得到两者之间的 decimal durationSeconds
→ durationSeconds × 本次选择采样率
→ 唯一一次 Round / Away From Zero
→ 相对 WAV 起点的 sample frame
```
所有范围内 Tempo 变化必须参与换算。
范围起点之前最近有效的 Tempo 状态必须用于从 `startTick` 开始的第一个区间，但不得把 `startTick` 前的时长写成文件前导静音。
Time Signature、Key Signature 和 Marker 不直接改变音频采样时间或声音输出。
Tempo 非法时应在编译阶段失败，不进入音频生成。极端但合法的 Tempo 只要能被 Midora 时间系统有效表示，就按规则渲染，不额外限制或警告。
### 15.5.2 确定性要求
映射必须：
```text
确定
单调
同一 Project / Tempo Map / 范围 / 文件采样率下重复计算一致
```
不得逐 Tempo 段取整，不得先取整绝对 sample frame 再相减，也不得使用 `Floor`、To Even 或依赖平台浮点环境的隐式取整替代第 4.1.4 节规则。
同 tick 事件映射到同一采样位置，其先后仍由 canonical compiled result 的同 tick 语义排序决定。
不同 tick 允许映射到同一个采样。此时仍按：
```text
tick 顺序
同 tick 事件顺序
```
处理，不人为错开采样、不丢弃事件、不限制 Tempo。
### 15.5.3 输出采样长度
输出采样数必须完整覆盖 `[startTick, endTick)` 对应的时间范围。
同一分轨任务中，所有成功 WAV 使用相同采样长度，包括静音区间。
如果 tick、Tempo、秒数、采样数、进度计数或 RIFF/WAVE 数据长度超过实现可安全表示范围：
```text
准备或写入阶段明确失败。
不自动截断。
不自动降低采样率。
不自动拆分文件。
不自动缩短范围。
```
---
## 15.6 范围起点状态与 Pre-roll
### 15.6.1 非音符状态恢复
当 `startTick > 0` 时，使用 第 12 章《编译系统与 Canonical Compiled Result》 范围起点恢复规则，恢复必要状态，包括：
```text
Tempo / 全局上下文
Program
Bank Select
CC
Pitch Bend
Pitch Bend Range
RPN
NRPN
Logical Parameter Mapping 后的非 Note 状态
```
### 15.6.2 不补发范围前 Note On
如果 Note On 已在范围开始前发生：
```text
不得在 startTick 重新触发或补发。
该持续音不会出现在本次 WAV 起点。
```
### 15.6.3 不提供 Pre-roll
初版不提供用户可配置 Pre-roll，也不从 tick 0 静默模拟到起点。
因此范围前事件产生的：
```text
合成器内部历史状态
范围前 Release / Tail 残余发声
持续 Note 发声
```
不会被重建到输出起点。Reverb / Chorus 本身在初版已禁用。
实现可以在文件起点前进行不包含音乐历史的内部 DSP / 后端预热，但预热内容不得写入 WAV，也不得引入范围前音乐或合成器历史发声。
---
## 15.7 离线渲染后端
### 15.7.1 离线任务
初版音频文件渲染是离线渲染：
```text
不要求实时 1x 速度。
可以快于实时。
也可以因后端或文件系统性能慢于实时。
正确性、确定性和文件合法性是硬约束；满足这些硬约束的实现中优先使用更快的渲染路径。
```
渲染不依赖可用的实时播放设备。
以下内容不影响 WAV：
```text
当前播放设备
设备驱动模式
设备 buffer / latency
Windows 主音量
Windows 应用音量混音器
声卡增强 / 空间音效 / 驱动 EQ
播放设备故障
```
### 15.7.2 BASSMIDI 与 SoundFont
初版使用与播放一致的 BASSMIDI / SF2/SFZ 发声语义。
文件渲染由第 13.30 节规定的同一个 `win-x64` Native AOT 音频子进程执行，不允许用其他 CPU 架构或 JIT Worker 生成正式文件。
本次 compiled result 必须先完成全局 Port / Channel 分配，再从 Execution Projection 确定性派生 Logical Segment/Unit 与 Pure MIDI Root 音频投影。每个 Unit/Root 使用独立、干净的 1-channel BASSMIDI Stream 语义；同 Root 子 Track 必须先合并，不能逐 Track 合成后求和。实际 native Stream 可由有界 pool 复用。
所有 Unit 使用任务开始时冻结的同一个程序级 Enabled SF2/SFZ 有序配置；BASSMIDI 按列表顺序建立完整 Font handle 与目标映射数组。
每个 Stream 必须完成：
```text
干净初始化
按冻结顺序直接打开每个原绝对路径（仅 SF2 使用 `BASS_MIDI_FONT_MMAP`），并一次性设置完整 SoundFont handle/mapping 列表
按 canonical Unit descriptor 建立 Melodic/Percussion mode；Logical Channel 10 强制 Melodic
BASS_MIDI_NOFX 启用
BASS_MIDI_NOTEOFF1 启用
BASS_ATTRIB_MIDI_SRC = 1
BASS_ATTRIB_MIDI_CPU = 0
每个 Unit Stream 使用本次冻结的同一 Offline Maximum Sample Voices per Unit Stream 值
在 Preparing 预加载计划引用的 SF2 presets 或其 fallback
按本次文件采样率直接创建 / 配置
compiled result 所需初始状态应用
```

Event Instrument/SubVoice 路径出现 CC91 / CC93 仍表示 compiled result 不一致并导致任务级 Error。Pure MIDI Track 的合法 CC91 / CC93 必须被计划接受；由于 `BASS_MIDI_NOFX`，文件音频投影确定性忽略其 Reverb/Chorus 效果，不产生诊断。opaque imported SysEx/Meta 不送入 synth。
### 15.7.3 不共享活动播放状态
音频渲染不得继承：
```text
最近播放 Stream 状态
活动 Note
Program / CC / Pitch Bend 历史
播放引擎合成器历史发声
播放 buffer
运行期 Mute / Solo
播放设备状态
```
渲染使用独立、干净的后端上下文。
### 15.7.4 分轨隔离
每条 Logical Track 必须从独立干净状态开始。
不得继承上一条 Track 的：
```text
Program
Bank
CC
Pitch Bend
RPN / NRPN
活动 Note
合成器内部历史发声
Limiter 状态
其他合成器或输出链状态
```
一条 Track 的失败后端状态不得被下一条 Track 复用。
初版没有 DAW 式轨道发送或共享总线。每条分轨只包含该 Track 通过自身 Event Instrument、canonical Unit 投影和公共 Master 输出链产生的结果，不继承其他 Track 的非音符控制或效果状态。

### 15.7.5 文件专用 OutputDevice 抽象

实时物理设备和文件目标应通过统一的音频输出目标 / `OutputDevice` 抽象向共享音频引擎提供：
```text
目标采样率
声道和样本格式
可写 frame buffer
写入 / 完成 / 失败 / 取消状态
```

这里的 `OutputDevice` 是实现抽象，不表示必须存在物理设备。文件专用 OutputDevice：
```text
以本次选择的文件采样率工作
输出到临时 RIFF/WAVE writer
不初始化 WASAPI
不读取当前播放设备采样率
不依赖 Windows 音量或设备 DSP
```

共享音频引擎必须从当前输出目标取得采样率；不得在领域层或全局常量中写死 48 kHz。

### 15.7.6 进程拓扑

文件渲染使用第 13.30 节固定的内部音频子进程拓扑：
```text
子进程接收冻结的 canonical compiled result、文件采样率、输出链设置、SF2 资源信息和已授权临时目标。
子进程不得读取或解释 Project 源对象。
文件专用 OutputDevice、BASS/BASSMIDI、Limiter 和流式样本写入必须位于音频子进程内；文件事务授权、最终发布结果和用户任务状态仍由主应用协调。
控制 IPC 的轮询或批次大小不得改变采样位置、事件顺序、最终 frame 数或 RIFF/WAVE 内容语义。
```
---
## 15.8 音频输出链
### 15.8.1 统一链路
每个输出文件采用：
```text
canonical compiled result
→ Canonical Segment / Unit 音频投影
→ Unit raw PCM / 确定性 stereo mix
→ Playback Master Volume
→ 强制 Limiter
→ 文件专用 OutputDevice
→ RIFF/WAVE writer
```
各 Unit 不因 Unit 数量自动平均、降低或归一化增益。
### 15.8.2 Playback Master Volume
音频渲染使用 Project 当前正式生效的 `Playback Master Volume`。
不使用：
```text
未提交的 UI 临时滑块值
Preview 专用音量
Windows 音量
音频渲染独立输出增益
```
初版不提供 Master Volume bypass。
Master Volume 为静音值时，允许成功生成静音 WAV。
### 15.8.3 Limiter
音频文件渲染必须应用第 13.17.6 节定义的版本 2 Limiter，并使用与实时播放相同的 detector、look-ahead、attack、hold 与 release 状态语义。
初版：
```text
不提供音频渲染专用 Limiter 开关。
不提供 Limiter bypass。
Limiter 无法初始化时，渲染准备失败。
```
即使实时播放未来允许关闭监听 Limiter，初版音频文件渲染链仍不提供绕过该最终保护处理的能力。
如果未来播放 Master / Limiter 链发生版本化变化，音频文件渲染必须同步采用相同系统语义，并通过设置或格式版本处理兼容，不得长期维护另一套隐式输出链。
### 15.8.4 处理顺序
必须先按稳定 Unit key 顺序混合所有实际使用 Unit，再统一应用：
```text
Playback Master Volume
最终 Limiter
```
不得每个 Unit 或 Segment 先独立 Limiter 后再混合。
整曲模式中，所有选中 Track 混合后共同进入 Master Limiter，因此一个 Track 可能影响整曲的最终限制处理。
分轨模式中，每条 Track 独立进入完整输出链，因此不存在其他 Track 触发 Limiter 对当前分轨的影响。
### 15.8.5 分轨不可重建整曲
由于每条分轨独立经过非线性 Limiter：
```text
分轨文件相加不保证等于整曲混音。
```
渲染界面或结果说明必须明确提示该边界。
### 15.8.6 输出链延迟
离线合成器或 DSP 内部延迟不得转化为 WAV 开头的额外静音。
初版版本 2 Limiter 使用 5 ms look-ahead。该内部分析前瞻必须由渲染器预取并补偿：
```text
离线渲染必须保持与播放一致的时间语义。
不得让整首音乐整体后移。
到达硬边界后不得因内部 buffer 额外延长文件。
```
---
## 15.9 样本合法性与精度
### 15.9.1 内部精度
本章不固定内部必须使用 float32 或 float64，只要求：
```text
最终正确写出 IEEE 32-bit float。
避免不必要的精度损失。
chunk 大小不改变可感知输出语义。
```
### 15.9.2 有限样本
版本 2 Limiter 之前允许存在超出 `[-1, 1]` 的有限 float 中间样本；强制 Limiter 之后写入 RIFF/WAVE 的最终样本必须处于 `[-1, 1]`。
初版不：
```text
检测 clipping
提示 limiter activity
在 Limiter 之后追加硬裁剪
自动归一化
增加第二层 Limiter
```
### 15.9.3 非有限样本
如果后端、混音或 DSP 产生：
```text
NaN
+Infinity
-Infinity
```
当前输出失败，非有限值不得进入正式 WAV。
Denormal 的抑制属于实现层，但不得造成渲染失败或可感知语义变化。
### 15.9.4 静音输出
只要范围有效且至少有一个有效 Track，全程静音仍可成功输出。
如果实现了静音检测：
```text
“未检测到发声音频”属于 Info。
不改变 Completed 状态。
完整逐采样静音扫描不是初版必要条件。
```
---
## 15.10 文件命名
### 15.10.1 整曲默认名称
整曲固定候选文件名为 `<ProjectStem>.wav`。`ProjectStem` 的来源优先级为：
```text
Project 名称
当前 .midora 文件名 stem
Midora Render
```
候选为空、仅空白或按第 14.17.4 节合法化后为空时使用后续 fallback。Windows 保留字符等可合法化内容不是跳过候选的理由；非法 UTF-16 使规划失败。未保存 Project 且 Project 名称不可用时，最终 fallback 为 `Midora Render.wav`。
### 15.10.2 分轨默认名称
初版分轨固定候选模板为 `<NN> - <LogicalTrackDisplayName>.wav`，例如：
```text
01 - Piano.wav
02 - Piano.wav
```
序号规则：
```text
使用整个 Project global Arrangement Track Order 中的当前显示序号。
不只对本次选中的 Track 重新编号。
未选中的 Track 仍占用 UI 显示序号。
允许输出序号跳号。
根据整个 Project Track 数动态增加补零宽度，至少两位。
Track 排序变化后，下次渲染使用新序号。
```
默认不额外加入 Event Instrument 名称。
### 15.10.3 空名称与重复名称
Track 名称为空、仅空白或合法化后为空时，使用：
```text
Logical Track <显示序号>
```
重复名称或合法化后冲突时，系统追加稳定、可读的序号或冲突后缀，确保所有输出目标唯一。
### 15.10.4 Windows 文件名合法化
音频渲染与 MIDI 导出必须调用同一套确定性安全文件名合法化与冲突检测规则。合法化只改变本次输出计划中的文件名，不得修改 Project 名称、Logical Track 名称或其他源数据。

系统必须处理：
```text
Windows 非法字符
保留设备名，如 CON / PRN / AUX / NUL
尾部空格和句点
控制字符和不适合作为文件名的不可见字符
文件名部分过长
大小写不敏感冲突
Unicode 规范化别名
```
允许 Unicode 文件名，不强制转为 ASCII 或拼音。
相同候选文件名、扩展名预算和冲突上下文在 MIDI 与音频工作流中必须产生相同文件名部分。精确算法固定为第 14.17.4–14.17.5 节的 NFC、不安全字符集合、设备保留名、`255` UTF-16 code unit、text-element 安全截断与稳定 ` (n)` 冲突规则；音频工作流不得定义变体。
### 15.10.5 扩展名
整曲路径缺少扩展名时自动追加：
```text
.wav
```
`.WAV` 等大小写形式视为合法。
用户输入 `.mp3`、`.flac` 等其他最终扩展名时，必须提示并修正或要求确认使用 `.wav`；不得把 RIFF/WAVE 内容写到误导性扩展名中。
只要最终扩展名是 `.wav`，`song.mp3.wav` 之类名称可保留。
分轨文件统一由系统追加 `.wav`。
---
## 15.11 输出路径与目标列表
### 15.11.1 目标选择
整曲模式：
```text
用户选择完整 .wav 文件路径。
```
分轨模式：
```text
用户选择输出目录。
系统生成每条 Track 的完整目标路径。
```
输出路径不保存进 Project。整曲与分轨最近目录可以分别作为用户级或会话级状态记忆。
### 15.11.2 目标列表冻结
分轨开始前，系统必须根据：
```text
Track 选择
Project 当前 Track 排序
命名规则
合法化规则
输出目录
```
生成并冻结完整目标列表，并在开始前向用户预览合法化后的全部最终路径和冲突状态。
开始后不得因 Project、排序或命名设置变化而改变。
### 15.11.3 目标冲突
任务开始前必须：
```text
完成文件名合法化
按 Windows 大小写与路径别名语义检测冲突
确保两个输出不指向同一最终路径
列出已有目标文件冲突
让用户统一决定覆盖、修改目标或取消
```
覆盖决定不写入 Project，每次冲突重新确认。
不得：
```text
让后一个分轨覆盖前一个分轨
未授权时覆盖任务中途新出现的文件
自动覆盖当前 .midora
自动覆盖程序级 SoundFont 列表中的任何原文件
```
### 15.11.4 禁止目标
禁止 WAV 目标覆盖：
```text
当前打开的 .midora 文件
程序级 SoundFont 列表中的任何原文件
任何正式目标对应的临时文件路径
```
`.midora` 是单文件 Zip package，不支持把 WAV “写入包内目录”。
### 15.11.5 目录
允许创建缺失的最终输出目录。
创建目录失败时，在渲染开始前报告权限或路径诊断。
允许网络盘和可移动磁盘，按普通文件系统处理；断开、权限变化或写入失败正常报告，不自动切换目录。
完整路径超过目标文件系统能力时，该输出失败并报告路径错误。整曲目标如果实际指向目录而不是文件路径，则为非法；分轨目标则必须是目录。
符号链接、重解析点和路径规范化的安全处理属于实现层，但不得写入用户未授权目标。
---
## 15.12 文件事务
### 15.12.1 临时写入
每个输出必须：
```text
先写独立临时文件
完整生成并最终化 RIFF/WAVE
正常关闭写入器
再移动或替换到最终目标路径
```
临时文件优先位于目标文件所在目录，以提高同文件系统替换的安全性。
临时文件名必须：
```text
避免与正式目标冲突
可识别为 Midora 未完成输出
不得与任何正式目标相同
```
具体命名格式由实现层决定。
### 15.12.2 创建时机
整曲：
```text
编译、Enabled SoundFont 配置和后端关键前置检查成功后才创建临时输出。
```
分轨：
```text
每条 Track 的编译和该输出前置检查成功后才创建对应临时文件。
```
不得在任务开始时先创建所有空正式文件。
### 15.12.3 成功判定
只有满足全部条件才算输出成功：
```text
音频样本完整生成
RIFF/WAVE 结构最终化
最终 RIFF、fmt 和 data 等 chunk 的大小、格式和 frame 对齐正确记录
写入器正常关闭
临时文件成功发布到最终目标路径
```
“音频已生成”或“临时文件已写完”不等于成功。
是否重新完整读取并解码校验推迟到实现层；初版至少必须验证写入过程和最终结构完成。
是否执行显式 flush-to-disk 属于实现层；系统级要求是在写入器正常完成并关闭后才报告成功。
### 15.12.4 覆盖安全
覆盖已有文件时，应尽量保留原目标，直到新文件成功完成。
如果最终替换失败：
```text
当前输出失败。
尽量保留原目标文件。
报告临时文件路径或清理结果。
```
不自动删除原文件后重试，不自动换名。
已授权覆盖的目标在任务期间被外部替换，仍可按已确认决定尝试最终替换，但必须正常处理占用、权限和替换失败。
原本不存在的目标在任务期间被外部创建，且用户未授权覆盖时，不得静默覆盖，当前输出失败。
### 15.12.5 不预检查磁盘空间
初版不进行可用磁盘空间预检查。
只在实际写入失败时报告错误。
界面可以根据：
```text
本次选择的文件采样率
stereo
32-bit float
渲染时长
文件数量
```
显示预计文件大小，但估算不构成成功保证。
### 15.12.6 临时文件清理
失败或取消时应尽力清理当前输出临时文件。
清理失败：
```text
显示残留路径。
产生文件系统 Warning 或取消清理诊断。
不把临时文件当作成功 WAV。
```
分轨中某个临时文件清理失败，不阻止其他独立输出继续。
正式文件成功发布后仍残留额外临时文件：
```text
正式输出仍可判定成功。
产生文件系统 Warning。
```
初版不要求全盘扫描历史临时文件，只能在明确可识别且安全的 Midora 临时位置或当前操作相关路径中处理。
---
## 15.13 多文件独立成功 / 失败
按 Logical Track 分轨不采用整体原子性。
规则：
```text
每个文件独立成功或失败。
某条 Track 编译失败：记录失败，继续下一条。
某条 Track 渲染或写入失败：清理当前后端和临时文件，继续下一条。
Mapping Function 运行时异常：当前输出失败；分轨模式只影响实际调用到该异常映射的 Track，其他 Track 可继续。
已经成功发布的文件不因其他文件失败而删除。
```
公共前置条件失败时，整个任务失败，例如：
```text
SF2 缺失或无法加载
内嵌 SF2 损坏
Limiter 强制链无法初始化
无法建立公共渲染环境
```
输出目录在任务中途被删除或权限变化时，当前及后续文件分别按实际操作结果失败，任务继续尝试其他文件。
### 15.13.1 任务最终状态
初版状态至少包括：
```text
Preparing
Rendering
Cancelling
Completed
Completed With Errors
Failed
Cancelled
```
定义：
| 状态 | 条件 |
|---|---|
| Completed | 整曲唯一输出成功；或分轨所有正式输出成功。 |
| Completed With Errors | 多输出任务中至少一个正式输出成功、至少一个正式输出失败。 |
| Failed | 整曲唯一输出失败；分轨全部正式输出失败；或任务级公共前置失败。 |
| Cancelled | 用户确认取消，不论此前是否已有完整成功文件。 |
Track 缺少唯一合法 parent 属于 Project 结构 Error，并在任务级公共前置阶段阻止渲染；不得忽略后继续。
Warning 本身不触发 `Completed With Errors`。
---
## 15.14 渲染任务与模态锁定
### 15.14.1 单任务
初版同一时间只允许一个音频渲染任务。
音频渲染不能与以下操作并发：
```text
MIDI 导出
Project 保存
打开或关闭 Project
手动编译任务
其他模态导出任务
另一个音频渲染任务
```
### 15.14.2 自动 Stop
如果开始渲染时存在播放或预览任务：
```text
1. 自动 Stop；
2. 完成播放清理；
3. 播放光标按 Stop Cursor Behavior 处理；
4. 进入音频渲染；
5. 渲染结束后保持 Stopped；
6. 不自动恢复播放。
```
渲染完成不改变当前播放光标位置，也不改变运行期 Mute / Solo 状态。
### 15.14.3 模态渲染窗口
从进入 `Preparing` 开始，到任务进入最终状态，Midora 锁定在渲染窗口。
用户不能执行任何除“取消渲染”之外的功能，包括：
```text
操作主窗口
编辑 Project
保存 Project
打开 / 关闭 Project
退出 Midora
修改 Mute / Solo
修改播放或渲染设置
启动其他导出 / 编译任务
```
渲染窗口禁用系统关闭按钮，只允许使用专用“取消渲染”操作。
渲染窗口是否允许最小化及其具体窗口行为不在本层决定；无论如何，主窗口和其他功能必须不可操作。
### 15.14.4 Project 任务锁
系统级语义上，渲染任务从 `Preparing` 到最终状态期间持有 Project 只读任务锁，并阻止任何 Project 写操作。
具体锁实现由实现设计阶段决定。
### 15.14.5 系统环境
窗口最小化或应用失去焦点不应改变渲染结果。
初版不为以下情形规定特殊处理：
```text
Windows 注销
Windows 关机
系统睡眠
```
不要求阻止关机或睡眠，也不保证这些情形下完成临时文件清理。
本节不规定渲染任务在系统睡眠后的恢复能力；工程总耗时仍按第 3.6.4 节在睡眠 / 休眠期间暂停，不能把暂停时间计入 Project。
---
## 15.15 取消
### 15.15.1 取消确认
点击取消后弹出确认。
用户确认前，渲染继续进行。
确认后进入：
```text
Cancelling
```
此时：
```text
禁止重复取消。
停止尚未完成的编译、资源加载、渲染、混音或写入。
不再开始后续 Track。
等待后端和文件事务安全停止。
清理未完成临时文件。
```
### 15.15.2 安全优先
文件一致性和安全清理优先于瞬时取消响应。
如果取消发生在不可安全中断的原子替换阶段：
```text
让该文件系统事务完成或失败后，再完成取消。
```
不得强制中止并制造不确定文件状态。
### 15.15.3 当前文件完成边界
如果取消发生在：
```text
音频已生成但尚未成功发布：该文件不算成功，清理临时文件。
已经成功发布到目标路径：该文件保留，取消只影响后续或尚未成功的输出。
```
### 15.15.4 清理错误
取消清理中发生新错误：
```text
记录为取消清理诊断。
顶层状态仍为 Cancelled。
必要时显示残留临时文件或资源未完全清理。
```
窗口必须等待安全停止和必要清理后才能关闭。
如果清理失败，退出渲染窗口前显示残留路径。
### 15.15.5 取消结果提示
分轨任务取消后，初版只显示：
```text
渲染已取消
```
不额外逐项汇总成功、失败、取消或未开始文件。
已完整成功发布的文件仍保留。
初版不支持断点续渲或自动恢复中断任务。
---
## 15.16 进度与结果窗口
### 15.16.1 进度
初版显示：
```text
总体进度
当前阶段
分轨模式中的当前 Track / 文件
已耗时
可选的动态剩余时间估算
```
进度主要按已处理音频采样或时间范围计算。
分轨模式可按：
```text
各计划文件已处理采样数之和
÷
全部计划文件总采样数
```
计算总体进度。失败或跳过文件按其已完成处理状态计入。
预计剩余时间不保证精确，也不保存进 Project。
极端范围下进度计数必须避免整数溢出。
### 15.16.2 总耗时
结果中的总耗时指：
```text
从进入 Preparing 到进入最终状态的实际墙钟时间
```
不等于 WAV 播放时长。
Project 打开累计工程总耗时在渲染准备、执行和取消清理期间继续累计。
窗口最小化或应用失去焦点不暂停该累计。
### 15.16.3 Completed
整曲成功时显示：
```text
最终文件路径
格式
范围
总耗时
打开文件位置入口
```
分轨全部成功时显示：
```text
成功文件数量
输出目录
范围
总耗时
打开文件位置入口
```
不要求逐条展开所有成功文件。
### 15.16.4 Completed With Errors
显示：
```text
成功数量
失败数量
输出目录
总耗时
可展开的失败文件和诊断
```
必须能够识别哪些文件成功、哪些文件失败。
“打开文件位置”打开输出目录，不自动选中某个不确定文件。
### 15.16.5 Failed
整曲失败显示当前输出及原因。
分轨全部失败时列出每个失败输出及对应原因。
进入最终 `Failed` 状态后，取消按钮不再可用，用户查看结果并关闭窗口。
初版不要求在结果窗口直接修改参数或重试失败项。用户关闭后重新发起任务。
### 15.16.6 结果窗口关闭
任务完成后不自动关闭窗口、不自动播放 WAV、不自动打开资源管理器。
用户主动关闭后返回主窗口。
本次诊断可以继续保留在运行期诊断面板中，直到后续操作替换或用户清除，但不保存进 Project。
初版不提供持久化任务历史。
---
## 15.17 诊断
### 15.17.1 级别
音频渲染诊断至少区分：
```text
Error
Warning
Info
```
规则：
```text
Error：导致相关正式输出失败。
Warning：默认不阻止输出；只有编译系统适用的 Warning 才受“Warning 导致编译失败”策略影响。
Info：不阻止输出。
```
文件系统、资源和兼容性 Warning 不受“Warning 导致编译失败”开关影响。
### 15.17.2 来源
诊断可来自：
```text
编译系统
Event Instrument / Mapping Function
SF2 与资源验证
BASSMIDI 后端
Port 混音
Limiter / 输出链初始化
非有限音频样本
RIFF/WAVE 写入与最终化
目录、权限、文件占用和替换事务
临时文件清理
取消清理
```
### 15.17.3 定位
诊断应尽量关联：
```text
Logical Track
Segment
Event Instrument
SubVoice
Mapping Function 或源对象
目标最终文件路径
必要时的临时文件路径
```
整曲模式中的源对象错误不得只显示“整曲渲染失败”。
### 15.17.4 排序与合并
默认先按严重级别，再按输出文件 / Logical Track 顺序和源对象位置稳定排序。
相同诊断可以合并并显示影响次数或对象，但不得隐藏不同源对象的重要定位。
### 15.17.5 特定提示
| 情况 | 级别 / 结果 |
|---|---|
| 计划文件超过 RIFF/WAVE 大小上限 | Preparing Error，整个任务不开始 |
| Channel Unit 峰值达到 248 | 编译 Info，不受 Warning-as-error 影响 |
| 全静音检测结果 | Info |
| Track 缺少、重复或引用错误类型的 parent | Preparing Error，整个任务不开始 |
| Enabled SoundFont 主路径缺失、SFZ 依赖失败或 BASSMIDI 加载/映射失败 | Preparing Error，整个任务不开始 |
| 正式输出成功但临时文件残留 | 文件系统 Warning |
| Warning 存在但所有正式输出成功 | 顶层仍为 Completed |
Info 默认折叠展示，用户可以展开查看。
初版不默认额外生成 `.log` 文件，也不把诊断写入 WAV 或 `.midora`。
是否提供“导出诊断文本”留给 第 17～20 章的 UI 与交互规格 或实现层。
---
## 15.18 SoundFont 资源规则
### 15.18.1 无 SoundFont
程序级 SoundFont 列表无任何 Enabled 项时禁止音频文件渲染。
不生成静音占位文件，也不自动使用系统默认音色库。
### 15.18.2 任务冻结与直接读取
Preparing 开始时冻结当前 Enabled 项的有序绝对路径与目标映射。任务必须：
```text
不复制 SoundFont 或 SFZ 依赖到临时目录
不计算或校验完整文件 hash
不读取 .midora 获取 SoundFont
由音频 Worker 直接打开原路径；仅 SF2 使用 BASS_MIDI_FONT_MMAP
任一主路径、SFZ 依赖缺失/不可读或加载/映射失败时整体失败
```
开始后使用本次已打开的 handle，不在任务中热重载。列表或原文件变化不修改 Project；用户必须在 Stopped / Idle 明确重新提交列表或重启任务。
### 15.18.3 加载失败
任一 Enabled SoundFont 或其 SFZ 依赖无法由 BASSMIDI 加载：
```text
任务级公共前置失败。
不能生成任何新音频输出。
```
不得自动寻找同名 SoundFont、修复 SFZ 依赖、使用系统音色库或输出静音代替。
### 15.18.4 缓存失效
有序 Enabled 配置、目标映射或任一主文件的 length / last-write-time 元数据变化：
```text
使相关音频后端和音频样本缓存失效。
不必使纯 MIDI canonical compiled result 失效。
```
该缓存身份只是避免完整文件读取的本机性能键，不是内容完整性验证；用户在保持路径、长度和时间戳不变的情况下原地替换文件属于未检测的外部修改。
---
## 15.19 缓存与确定性
### 15.19.1 缓存允许范围
可以缓存：
```text
canonical compiled result
可证明等价的 Event Instrument / 曲线 / Mapping 编译结果
离线调度中间结果
受完整缓存键约束的 Segment / Unit raw PCM tile
受完整范围、Track 集合、Master 和 Limiter key 约束的 playback/render span
```
不要求缓存完整最终 WAV 样本。
缓存不保存进 `.midora`。初版只在当前 Project 打开 session 内保留，不跨会话复用；Project 关闭时删除本 session 的已知条目。
### 15.19.2 等价原则
所有缓存必须遵守 第 12 章《编译系统与 Canonical Compiled Result》：
```text
使用缓存的结果必须等价于同一上下文下重新完整计算的结果。
```
不得为保留旧缓存而保留旧 Port / Channel 分配或忽略设置变化。音频只能在成功 canonical 之后派生不依赖物理 Port/Channel 身份的抽象 Unit 投影。
### 15.19.3 缓存键与失效
至少应考虑：
```text
Project 内容
Audio Render CompileContext
Track 选择
范围
Tempo Map
程序级 Enabled SF2/SFZ 有序配置、目标映射与主文件元数据缓存身份
Playback Master Volume
渲染 Limiter 语义和参数
固定容器 / 声道 / 样本格式
本次选择的文件采样率
Offline Maximum Sample Voices per Unit Stream
固定 BASS/BASSMIDI baseline 与 audio renderer version
```
具体规则：
```text
Master Volume / Limiter 变化：音频输出缓存失效，不必使 canonical compiled result 失效。
Tempo Map 变化：事件采样位置和长度变化，相关调度 / 样本缓存失效。
文件采样率变化：sample position、最终 frame 数、RIFF 大小检查、后端 Stream 和全部音频缓存失效；不必使纯 MIDI canonical compiled result 失效。
Time Signature / Key Signature / Marker：只有实际影响范围、CompileContext 或输出语义时才失效。
Project End Marker：默认范围模式下使范围和相关缓存失效；手动范围不因此改变。
Track 选择：属于 CompileContext，不得误用其他 Track 集合的最终缓存。
命名偏好、输出路径、覆盖决定：只影响目标列表和文件事务，不使音乐 / 音频内容缓存失效。
Track 排序：不改变单 Track 音乐语义，但改变处理顺序、默认文件序号和结果顺序。
播放设备变化：不使音频渲染缓存失效。
```

Segment/Unit raw PCM 位于 Track 选择求和、Master 和 Limiter 之前。未修改 Segment 的完整 key 不变时可跨其他 Segment 编辑复用；从编辑位置起部分失效只允许使用编译器能证明的最早 causal dirty tick，无法证明时必须回退到 Segment 有效起点。

相同 Project revision、Audio Render CompileContext、范围和完整 key 的 exact replay 命中时，不得再次执行语义编译或 BASSMIDI 合成。跨范围复用必须包含范围冷启动上下文，不能切片包含范围前持续 Note 的连续 PCM。
### 15.19.4 结果稳定性
相同 Project、SF2、CompileContext、输出链配置和软件环境下，渲染应尽量稳定一致。
Midora 不主动注入随机性。
如果 BASSMIDI 或 SF2 自身存在无法控制的随机行为：
```text
不承诺逐采样或逐字节完全一致。
不要求重复渲染文件 hash 相同。
```
buffer / chunk 大小不得改变音乐时间、事件顺序或可感知输出语义。

涉及逐采样、逐字节或不同 block 完美一致性的音频测试，必须使用实际活动 sample voices 不超过本次 `Offline Maximum Sample Voices per Unit Stream` 的输入。达到配置上限时，允许 BASSMIDI 的固定 voice-limit 行为改变音频；不得把这种资源上限行为误判为编译器或 block-size 不确定性。
---
## 15.20 Audio Render 任务参数
### 15.20.1 归属
渲染模式、范围、Track 选择、文件采样率、Offline Maximum Sample Voices per Unit Stream、输出位置与覆盖授权只属于当前 Audio Render Dialog / Task Draft。它们不是 Project 顶层对象，不进入 `.midora`、Project Undo / Redo、canonical fingerprint 或 Application Preferences。

### 15.20.2 固定初始值
每次打开 Audio Render Dialog 时使用：
```text
Mode = Whole Mix
Range = Project Default Range
Track Selection = All Valid Logical and Pure MIDI Tracks
Format = RIFF/WAVE / Stereo / Interleaved IEEE float32 little-endian
Sample Rate = 48,000 Hz
Offline Maximum Sample Voices per Unit Stream = 500
```
合法采样率仍为 8,000–192,000 Hz 整数；复音上限仍为 1–16,777,216。用户修改只在按 Start 后冻结给本次任务；Cancel 不产生持久状态。初版不提供 `Save as Project Defaults`。

### 15.20.3 范围与 Track 选择
Project Default Range、Manual Range、Whole Mix 与 Per Logical Track 的正式语义保持本章前文定义。Track 删除、排序或编辑不得维护任何“默认渲染选择”集合，因为该集合不再存在。本次任务开始前必须对当前 Project 重新解析并冻结选择。

### 15.20.4 本机路径
Whole Mix 与 Per Logical Track 的最近输出位置可以按用途保存为 Application Preference；它们不属于 Project，不进入 Undo / Redo，也不得参与音频内容 identity。
## 15.22 RIFF/WAVE 写出系统级要求
初版必须正确声明：
```text
RIFF/WAVE 容器
IEEE floating-point sample format
32 bits per sample
本次冻结的 8,000–192,000 Hz 整数采样率
2 channels
Left / Right order
interleaved frames
little-endian
```
RIFF/WAVE 必须按标准正确记录：
```text
RIFF size
WAVE form type
fmt chunk 的 floating-point format、sample rate、byte rate、block align、bits per sample 和 channel count
data chunk size
完整 frame 对齐
```
具体字节布局由实现设计阶段定义。
初版只写生成合法 RIFF/WAVE 浮点 WAV 所需的标准结构，不写：
```text
RF64 / ds64
WAVE64
BWF
LIST / INFO
Project Metadata
Track 名称
Marker
Tempo Map
版权元数据
Midora 私有 Chunk
完整编译结果
Project stable ID
```
文件系统创建 / 修改时间由操作系统和写入过程自然生成，不从 Project Metadata 复制。
---
## 15.23 性能与长任务
初版不设置人为最大渲染时长。
实际限制由：
```text
tick / Tempo / sample 表示范围
RIFF/WAVE 32 位大小边界
本次文件采样率
文件系统
磁盘空间
内存
BASSMIDI 后端
```
决定。
系统必须允许：
```text
分块生成
分块混音
分块写入
```
不要求也不应为了性能先把完整最终音频保存在内存中。应通过有明确上限的预分配工作 buffer、队列、索引和缓存提高吞吐量。

在满足确定性、文件合法性、取消和失败事务要求的前提下，时间性能优先于最小空间占用。允许使用双缓冲、三缓冲或四缓冲；具体内部 block 大小和数量由基准测试确定，不作为用户设置。

Preparing 阶段必须完成 Rendering 热路径所需的托管内存分配。进入每个正式输出的 Rendering 阶段后，负责以下工作的音频活动线程不得产生托管堆分配：
```text
事件调度
BASSMIDI 拉取 / 合成协调
多 Unit 确定性混音
Master Volume / Limiter
工作 buffer 搬运
文件样本分块写入
```

固定 buffer 一次分配、后续复用。Preparing 和 Finalizing 可以产生托管分配。同进程其他非音频线程的托管分配或进程级 GC 不构成本条失败；但渲染线程分配计数、吞吐量、停顿和结果必须单独测量。

分轨内部是否串行或有限并行属于实现层，但必须：
```text
结果确定
资源受控
任务状态和进度一致
每条 Track 状态隔离
```
不要求并行所有 Track，也不提供用户线程数设置。
---
## 15.24 初版明确不支持的功能
初版不支持：
```text
MP3 / FLAC / OGG 或其他音频格式
RF64 / WAVE64 或其他大文件 WAV 容器
自动拆分超过 RIFF 大小上限的输出
8,000 Hz 以下或 192,000 Hz 以上的文件采样率
位深选择
Mono 输出
多声道输出
按 Port 分轨
同一任务同时输出整曲与分轨
音频渲染独立 Master Volume
Master Volume bypass
音频渲染专用 Limiter 开关
Limiter bypass
Dither
峰值 / 响度归一化
Fade In / Fade Out
DC Offset 后处理
自动首尾静音裁剪
用户可配置 Pre-roll
从 tick 0 静默预滚恢复合成器历史
Release / Tail / 合成器残余发声越过硬范围
自动追加静音尾部
按静音检测自动结束
播放设备 DSP 捕获
渲染期间后台编辑或主窗口操作
多个并发渲染任务
持久化任务历史
断点续渲
自动恢复中断任务
自动跳过已有文件作为续渲
把 WAV 嵌入 .midora
默认生成渲染日志文件
```
---
