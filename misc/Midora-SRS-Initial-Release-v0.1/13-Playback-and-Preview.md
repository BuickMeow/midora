# 第 13 章 播放与预览

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义主时间线播放、Event Instrument/SubVoice/Segment 预览、播放状态机、运行锁定、中途状态恢复、BASSMIDI Stream、输出设备、设备采样率、Master Volume、Limiter、可调 buffer 和实时性能边界。

## 13.1 播放系统核心原则
### 13.1.1 只消费 canonical compiled result
播放系统必须消费 `canonical compiled result`。
规则：
```text
播放系统不得直接读取 Logical Track / Segment / Event Instrument 并自行解释音乐语义。
播放系统不得重新计算 Logical Parameter Mapping。
播放系统不得重新决定生命周期、Reset、Channel Group 或 Port / Channel Unit 分配语义。
播放系统不得为了性能绕过编译系统。
```
播放系统可以在 compiled result 之上构建：
```text
播放调度队列
预渲染 buffer
运行期状态机
Track Mute / Solo 过滤状态
BASSMIDI Stream 缓存
后端清理状态
输出链状态
```
但这些结构都是消费者缓存或运行期状态，不得成为另一套语义来源。
### 13.1.2 播放前必须有可播放 compiled result
用户点击播放时，如果当前播放范围 / Track 范围对应的播放编译失败：
```text
禁止进入 Playing。
显示编译失败诊断。
不产生 partial 播放结果。
```
如果用户开启“Warning 导致编译失败”，则播放编译同样遵守该策略。
### 13.1.3 播放使用专用 CompileContext
主播放、Segment 预览、Event Instrument 预览都应使用对应的 CompileContext。
初版至少存在：
```text
Playback CompileContext
Segment Preview CompileContext
Event Instrument Preview CompileContext
SubVoice Preview CompileContext
```
这些上下文只改变：
```text
范围
临时输入
预览对象
Track / Segment 选择
输出用途
```
不得改变：
```text
事件展开语义
Logical Parameter 状态继承语义
Mapping 计算语义
生命周期语义
Reset 语义
Port / Channel 分配语义
同 tick 语义排序
```
---
## 13.2 初版播放任务类型
初版只允许一个活动播放任务。
播放任务包括：
```text
主时间线播放
Segment 预览
Event Instrument 预览
SubVoice 预览
Event Instrument 虚拟键盘预览
```
同一时刻：
```text
只能存在一个活动播放任务。
主播放、Segment 预览、Event Instrument 预览、SubVoice 预览互斥。
```
如果已有活动播放任务，用户触发另一个播放 / 预览任务：
```text
禁止新任务。
提示需要先 Stop。
```
保存 / 打开 / 关闭 / 导出属于项目级操作，可以自动 Stop 当前任务后继续。新的播放或预览任务不自动抢占旧播放任务。
---
## 13.3 播放状态机
### 13.3.1 顶层状态
初版播放系统只定义以下顶层状态：
```text
Stopped
Preparing
Playing
Stopping
Error
```
初版不包含：
```text
Paused
Resuming
Scrubbing
Recording
```
初版不支持 Pause。
### 13.3.2 Preparing
`Preparing` 表示：
```text
正在执行播放前编译 / 缓存检查
正在检查 SF2 和播放设备
正在创建或复用 BASSMIDI Stream
正在加载当前 SF2
正在执行 Channel 10 melodic 初始化
正在恢复播放起点状态
正在准备初始预渲染 buffer
```
Preparing 期间：
```text
禁止再次点击 Play 触发并发播放。
允许点击 Stop 取消准备。
禁止编辑 Project。
禁止修改 SoundFont Settings。
禁止修改播放设备。
禁止 Undo / Redo。
```
### 13.3.3 Playing
`Playing` 表示播放任务处于活动状态。
Playing 可包含内部子状态：
```text
Playing.Normal
Playing.Buffering
```
`Playing.Buffering` 用于表示 underrun 后等待预渲染恢复。
### 13.3.4 Stopping
`Stopping` 表示系统正在执行 Stop 清理。
清理包括：
```text
停止调度后续事件
关闭活动 Note
必要时发送 All Notes Off / All Sound Off / Reset All Controllers 等兜底事件
清理活动实例状态
清理或重置 Channel-Wide 状态
清空播放状态机
```
### 13.3.5 Error
发生不可恢复播放错误时，系统应：
```text
立即停止当前播放任务
执行 Stop 类清理
进入 Error 状态
保留播放位置
显示错误信息
不修改 Project 内容
```
用户修复问题后，可以从 Error 状态重新点击 Play，系统重新进入 Preparing。
---
## 13.4 播放期间操作锁定
### 13.4.1 禁止编辑 Project
Playing / Preparing / Buffering 期间禁止所有会改变 Project 内容或播放语义的操作，包括：
```text
编辑 Conductor Track
编辑 Logical Track / Segment / Logical Note
编辑 Logical Parameter Lane
编辑 Event Instrument / SubVoice / Mapping / Lifecycle
创建 / 删除 / 重命名 / 复制 Event Instrument
创建 / 删除 / 排序 Logical Track
修改 Project Settings 中影响编译或播放的设置
修改 SoundFont Settings
修改播放设备
Undo / Redo
```
### 13.4.2 允许纯 UI 操作
Playing 期间允许不改变 Project 内容、不改变播放语义的纯 UI 操作，例如：
```text
滚动时间线
缩放视图
选择对象
打开信息面板
查看诊断
切换非持久化显示过滤
移动播放视图焦点
```
### 13.4.3 Mute / Solo 是允许的监听状态操作
Mute / Solo 都属于临时播放监听状态，不属于 Project 内容。
因此播放期间允许切换 Mute / Solo。
规则：
```text
Mute / Solo 不保存进 Project。
Mute / Solo 不进入 Undo / Redo。
Mute / Solo 不标记 Project 已修改。
Mute / Solo 切换不属于编辑 Project。
```
---
## 13.5 保存、打开、关闭与导出期间的自动 Stop
### 13.5.1 保存 Project
播放期间用户可以触发保存：
```text
保存按钮
Ctrl + S
```
执行顺序必须是：
```text
1. 自动 Stop 当前播放任务；
2. 执行 Stop 清理；
3. 播放光标位置按 Stop Cursor Behavior 设置处理；
4. 开始保存 Project；
5. 保存完成后保持 Stopped；
6. 不自动恢复播放。
```
保存 Project 本身不改变 compiled result 语义，也不应单独使编译缓存失效。
### 13.5.2 打开 / 关闭 Project
播放期间打开其他 Project 或关闭当前 Project 时：
```text
自动 Stop 当前播放任务；
完成 Stop 清理后，继续执行打开 / 关闭流程。
```
自动 Stop 不跳过未保存修改提示。
仍应按项目系统规则处理：
```text
保存
不保存
取消打开 / 关闭操作
```
### 13.5.3 MIDI 导出 / 音频文件渲染
播放期间触发 MIDI 导出或音频文件渲染时：
```text
1. 自动 Stop 当前播放任务；
2. 执行 Stop 清理；
3. 播放光标位置按 Stop Cursor Behavior 设置处理；
4. 进入导出 / 渲染流程；
5. 导出 / 渲染完成后保持 Stopped；
6. 不自动恢复播放。
```
导出 / 渲染流程必须使用对应专用 CompileContext 重新检查或复用等价缓存。
不得直接复用运行中的播放 buffer 作为导出 / 渲染结果。
### 13.5.4 Preparing / Buffering 期间触发保存
如果处于：
```text
Preparing
Playing.Buffering
```
用户触发保存 / 打开 / 关闭 / 导出，同样按自动 Stop 流程处理：
```text
取消准备 / 等待
执行清理
再继续项目级操作
```
### 13.5.5 自动 Stop 清理失败
如果自动 Stop 过程中后端返回错误，例如 Stream 清理失败：
```text
尽最大努力清理。
保存允许继续。
打开 / 关闭 / 导出需提示风险并由用户确认后继续。
```
---
## 13.6 播放范围与播放光标
### 13.6.1 默认播放起点
Stopped 状态点击主播放 Play 时：
```text
startTick = 当前播放光标 tick
```
用户可以在 Stopped 状态下把播放光标放到任意合法 tick，再从该位置播放。
播放起点允许为任意 tick，不要求对齐小节、拍线或 Segment 边界。
播放起点必须满足：
```text
startTick >= 0
```
否则禁止播放。
### 13.6.2 默认播放终点
用户从主时间线点击播放时，默认播放范围为：
```text
startTick = 当前播放光标 tick
endTick = Project End Marker tick；若无 End Marker，则由有效内容自然结束
```
如果 Project End Marker 早于后续内容：
```text
主播放默认到 End Marker 为止。
后续内容保留，但默认不播放。
```
用户显式指定播放范围超过 Project End Marker 时：
```text
允许播放超过 End Marker 的内容。
Project End Marker 只是默认范围依据，不是播放硬限制。
```
### 13.6.3 零长度播放范围
如果播放范围为：
```text
[startTick, startTick)
```
允许进入 Preparing，但随后立即完成 Stop 清理流程，不产生声音输出。
如果：
```text
endTick < startTick
```
属于非法播放范围，禁止播放。
### 13.6.4 播放光标显示
播放光标位置以当前播放 tick 为主。
播放系统不要求完全由音频设备 sample clock 反推播放光标。
Buffering 等待期间：
```text
播放光标停在当前 tick。
直到恢复播放后继续移动。
```
### 13.6.5 Stop Cursor Behavior
Stop 后播放光标位置由用户设置决定：
```text
Stop Cursor Behavior:
1. 回到本次播放开始 tick（默认）
2. 停在当前停止 tick
```
播放途中用户跳转到新 tick 时：
```text
“本次播放开始 tick”保持不变。
```
因此，如果用户从 tick 100 开始播放，播放中跳到 tick 500，再按 Stop：
```text
若设置为“回到本次播放开始 tick”：回到 tick 100
若设置为“停在当前停止 tick”：停在 Stop 发生时的当前 tick
```
当播放自然到达 endTick 时：
```text
进入 Stop 清理流程。
完成清理后，播放光标位置仍受 Stop Cursor Behavior 控制。
```
---
## 13.7 播放跳转与循环
### 13.7.1 播放中跳转
播放中用户跳转到新 tick 时，视为：
```text
停止当前播放状态
清理旧状态
从新 tick 冷启动
恢复非 Note 状态
不补发范围前 Note On
继续播放
```
播放中跳转不会改变 Stop Cursor Behavior 中的“本次播放开始 tick”。
### 13.7.2 循环播放
初版支持时间范围循环播放。
循环范围必须满足：
```text
loopStart >= 0
loopEnd > loopStart
```
到达 loopEnd 时：
```text
停止调度当前循环尾部后续事件
发送必要 Note Off / Reset / All Notes Off / All Sound Off
清理活动实例状态
从 loopStart 冷启动恢复非 Note 状态
继续播放
```
循环播放时，播放光标到达 loopEnd 后跳回 loopStart，并继续显示循环内当前位置。
---
## 13.8 从中途播放的状态恢复
### 13.8.1 不补发范围前 Note On
如果某个 Note On 在播放起点之前发生，播放起点之后仍在持续：
```text
播放系统不补发该 Note On。
```
这是中途冷启动的明确限制。
### 13.8.2 必须恢复非 Note 状态
从中途播放时，必须在播放起点恢复当前有效的非 Note 状态，包括：
```text
Program
Bank Select
Pitch Bend
Pitch Bend Range
CC
RPN
NRPN
Logical Parameter Mapping 后影响到的非 Note 状态
Tempo / Time Signature / Key Signature 的播放上下文状态
```
恢复状态不得依赖复用 Stream 的当前状态。
每次进入 Playing 前，本次使用的 Stream 必须处于清洁状态，然后基于 compiled result 发送范围起点状态恢复事件。
### 13.8.3 恢复事件排序
如果播放起点 tick 上既有恢复事件，又有用户原始事件：
```text
恢复事件先发送。
同 tick 用户原始事件随后发送。
用户事件优先覆盖恢复状态。
```
范围起点状态恢复事件应作为播放调度队列中的事件处理，并标记来源：
```text
范围起点状态恢复
```
不应散落为不可追踪的后端初始化副作用。
### 13.8.4 tick 0 播放
从 tick 0 播放时，仍需要发送必要系统初始化与 tick 0 用户事件，包括：
```text
Tempo / Time Signature 默认状态
Channel 10 melodic 初始化
tick 0 Program / Bank / CC / Pitch Bend / RPN / NRPN / Note 等事件
```
---
## 13.9 Conductor Track 播放语义
Conductor Track 永远参与播放上下文。
Mute / Solo 永远不影响 Conductor Track。
### 13.9.1 Tempo
实时播放速度完全由 Conductor Track 的 Tempo Map 决定。
初版不提供额外 playback speed multiplier。
播放系统必须支持播放过程中遇到 Tempo 事件后即时改变 tick-to-seconds 换算。
每个 canonical 事件的 sample frame 以及播放范围的 sample 长度必须按第 4.1.4 节计算：使用本次实际设备采样率，对相对播放起点的完整 Tempo Map 区间做 decimal 积分，乘采样率后仅执行一次 `AwayFromZero`。不得为实时播放采用与预览或文件渲染不同的取整规则。
### 13.9.2 Time Signature
Time Signature 只影响：
```text
小节 / 拍显示
播放光标的小节坐标
Buffering 恢复目标的小节长度
循环范围显示辅助
```
Time Signature 不直接改变声音事件 tick 位置。
### 13.9.3 Key Signature
Key Signature 只作为状态型全局音乐元数据和显示 / 导出信息。
Key Signature 不直接影响播放音高。
### 13.9.4 Marker
普通 Marker 不影响播放声音。
Marker 只影响：
```text
定位
显示
UI 辅助
```
---
## 13.10 Track Mute / Solo 临时监听状态
### 13.10.1 归属
初版支持 Track Mute / Solo，但二者均为临时播放监听状态。
规则：
```text
Mute 不属于 Project 内容。
Solo 不属于 Project 内容。
Mute / Solo 不保存进 Project。
Mute / Solo 不进入 Undo / Redo。
Mute / Solo 切换不标记 Project 已修改。
关闭 Project 或打开新 Project 后，Mute / Solo 状态全部丢弃。
新建或打开 Project 后，所有 Track 默认 Mute = false，Solo = false。
```
### 13.10.2 播放过滤规则
主时间线播放默认包含所有有效绑定 Event Instrument 的 Logical Track。
初版主播放不提供独立于 Mute / Solo 的显式 Track 选择。
如果至少存在一个 Solo Track：
```text
播放只输出 Solo 集合中未被 Mute 的 Track。
```
如果不存在 Solo Track：
```text
播放输出所有未被 Mute 的有效 Track。
```
Mute 与 Solo 同时存在时：
```text
Solo 先缩小候选集合。
Mute 再从候选集合中排除。
```
### 13.10.3 Mute / Solo 不改变 compiled result
Mute / Solo 只作为播放消费者层实时过滤。
规则：
```text
Mute / Solo 不改变 canonical compiled result。
Mute / Solo 不影响 MIDI 导出。
Mute / Solo 不影响音频文件渲染。
成品输出由导出 / 渲染设置中的 Track 选择决定。
```
### 13.10.4 Mute / Solo 过滤不只过滤 Note
被过滤 Track 的以下事件不得发送：
```text
Note
Program
Bank Select
Pitch Bend
Pitch Bend Range
CC
RPN
NRPN
Logical Parameter Mapping 后影响到的非 Note 状态
范围起点状态恢复事件
```
这用于避免被过滤 Track 的状态污染其分配到的 Channel Unit。
### 13.10.5 播放中打开 Mute
播放中将某 Track Mute 打开时：
```text
立即生效。
立即截断并清理该 Track 当前活动声音。
不等待 Release / Tail 自然结束。
停止发送该 Track 后续事件。
```
### 13.10.6 播放中开启 Solo
播放中开启 Solo 后：
```text
所有不在 Solo 集合且当前正在发声的 Track 立即清理。
只清理新被过滤掉的 Track。
不影响仍应发声的 Track。
```
### 13.10.7 解除 Mute / Solo
播放中解除 Mute 或取消 Solo 过滤后，重新进入候选集合的 Track：
```text
从当前 tick 冷启动恢复必要非 Note 状态。
不补发之前因过滤错过的 Note On。
恢复事件在当前 tick 后续普通事件之前发送。
```
### 13.10.8 Mute / Solo 清理范围
Mute / Solo 导致 Track 被过滤时：
```text
清理只作用于该 Track 关联的 Channel Unit / Stream 范围。
不得全局 All Sound Off 影响仍应发声的 Track。
```
允许使用强兜底清理事件，例如：
```text
All Notes Off
All Sound Off
Reset All Controllers
```
但必须限制在该 Track 当前关联的 Channel Unit / Stream 范围内。
### 13.10.9 来源追踪要求
为了支持 Mute / Solo，播放事件至少需要能追踪到：
```text
Logical Track ID
Segment ID
Event Instrument Instance ID 或临时实例来源
SubVoice ID
Port / Channel Unit
```
---
## 13.11 SoundFont 与播放
### 13.11.1 播放需要有效 SF2
点击播放时，如果 Project：
```text
未选择 SF2
SF2 缺失
SF2 不可读
SF2 格式不支持
SF2 加载失败
```
则：
```text
不进入 Playing。
显示不可播放原因。
不修改 Project 内容。
```
Event Instrument 预览、SubVoice 预览、Segment 预览与主播放一样需要有效 SF2。
### 13.11.2 播放期间禁止修改 SoundFont
播放期间禁止修改 SoundFont Settings。
包括：
```text
选择 SF2
取消选择 SF2
替换 SF2
改变 SF2 保存 / 引用模式
```
---
## 13.12 BASSMIDI Stream 生命周期
### 13.12.1 Stream 创建
播放开始时，只为本次播放 compiled result 实际使用到的 Port 创建 BASSMIDI Stream。
不允许：
```text
始终创建 16 个 Stream
维持历史最大 Port 集合
用户手动创建 Port Stream
```
### 13.12.2 Channel 10 melodic 初始化
每个实际使用 Port 对应的 BASSMIDI Stream 创建或重建时，必须立即执行 Channel 10 melodic 初始化。
即使复用 Stream，下次播放开始前也必须确保 Channel 10 melodic 状态有效。
### 13.12.3 Stream 复用
Stop 后允许保留已创建的 BASSMIDI Stream 作为缓存，以加快下一次播放。
但复用前必须重新确认：
```text
SF2 是当前 Project 当前选择的 SF2
Port 使用集合匹配本次播放需求
Channel 10 melodic 初始化有效
Stream 处于清洁状态
无残留 Note
无残留 CC / Pitch Bend / Program / RPN / NRPN 等状态
```
Stream 复用只能是性能优化。
不得继承上次播放残留状态。
### 13.12.4 Port 使用集合变化
如果下一次播放所需 Port 集合与上一次不同：
```text
Stream 管理按实际使用集合创建 / 释放 / 复用。
只保留或创建实际需要的 Stream。
```
### 13.12.5 Stop 清理范围
Stop 清理应覆盖本次播放任务实际使用过的所有 Channel Unit，而不仅是当前仍有活动 Note 的 Channel Unit。
Stop 是立即停止，安全优先，不追求音乐性尾音。
规则：
```text
Stop 不等待 Release / Tail 自然结束。
必须尽快关闭所有活动声音。
必要时可使用 All Notes Off / All Sound Off / Reset All Controllers。
```

### 13.12.6 Reverb / Chorus 禁用

所有正式播放和预览 BASSMIDI Stream 必须启用：
```text
BASS_MIDI_NOFX
```

初版不支持 BASSMIDI Reverb / Chorus。Project、canonical compiled result 和正式调度数据不得包含 CC91 / CC93。后端如果收到 CC91 / CC93，必须视为编译结果或 IPC 数据一致性 Error，不得以“NOFX 会忽略”为由继续静默播放。

`BASS_MIDI_NOFX` 是正式后端配置，不是用户可关闭的音色选项。

### 13.12.7 Stream 采样率

实时播放和预览的每个 BASSMIDI Stream 必须直接按当前所选输出设备初始化后报告的实际输出采样率生成音频。

```text
所有实际 Port 使用同一实时采样率
不先固定生成 48 kHz 再做最终重采样
设备或其实际采样率变化时，旧 Stream 和所有 sample-domain 缓存失效
```

SoundFont 内部样本插值或合成器内部采样处理不属于这里禁止的“固定输出采样率后再重采样”。
---
## 13.13 Reset Playback Engine
初版需要提供 `Reset Playback Engine` 命令。
用途：
```text
播放后端异常恢复
悬挂音处理
Stream 状态残留清理
Error 后端恢复
```
规则：
```text
Reset Playback Engine 不改变 Project 内容。
Reset Playback Engine 不进入 Undo / Redo。
Reset Playback Engine 不影响 compiled result 缓存。
Reset Playback Engine 在 Stopped 状态也允许执行。
Reset Playback Engine 不需要用户确认，直接执行。
成功后只做轻量状态提示，不弹窗。
```
如果当前处于：
```text
Playing
Preparing
Playing.Buffering
Error
```
用户执行 Reset Playback Engine 时：
```text
先终止当前播放任务。
执行完整后端清理。
销毁或重建所有 BASSMIDI Stream / 后端连接缓存。
进入 Stopped。
```
Reset Playback Engine 应强制清空后端播放缓存，而不是仅发送 All Notes Off。
---
## 13.14 播放输出设备
### 13.14.1 设置归属
播放输出设备选择属于软件全局 / 用户环境设置，不保存进 Project。
理由：
```text
Project 文件跨机器移动时，设备名 / 驱动 ID 容易失效。
输出设备是本机环境状态，不是音乐内容。
```
### 13.14.2 Project Playback Settings 与全局设置边界
Project Playback Settings 包含：
```text
Playback Master Volume
Playback Limiter 开关
Stop Cursor Behavior
```
软件全局 / 用户环境设置包含：
```text
输出设备选择
音频后端偏好
Render-Ahead Buffer
Device Buffer Request
```
初版不提供 WASAPI Shared / Exclusive 模式选择；正式 BASSWASAPI 后端固定使用第 13.14.7 节策略。

### 13.14.3 设备枚举与选择

设置界面必须列出当前系统中全部：
```text
输出方向
处于 enabled / ready 状态
可由当前 BASSWASAPI 后端枚举
```
的音频输出端点。

不得把录音输入、loopback input、disabled、unplugged 或 not-present 端点作为可选择输出设备列出。系统默认输出设备必须有明确标记。

每个条目至少显示：
```text
设备友好名称
System Default 标记（如适用）
稳定设备 ID 对应的当前选择状态
```

设备列表在打开设置页时刷新，并响应后端设备变化通知。设备 ID 只保存为本机 Application Preference，不进入 Project。

### 13.14.4 设备不可用
如果当前选择的输出设备不可用，点击播放时：
```text
不进入 Playing。
显示设备不可用原因。
允许用户切换设备。
不修改 Project 内容。
```
如果用户没有手动选择输出设备：
```text
使用系统默认音频输出设备。
```
如果系统当前没有任何 enabled output device：
```text
设备列表显示空状态。
不进入 Playing。
允许 Project 打开、编辑、编译、保存、MIDI 导出和音频文件渲染。
```

### 13.14.5 修改设备或设备采样率
Stopped 状态下修改播放设备时：
```text
所有播放后端缓存失效。
所有按旧设备采样率建立的调度、PCM 和 Stream 缓存失效。
下一次播放重新创建后端连接。
```
播放期间禁止修改播放设备。

如果操作系统或驱动在运行期改变当前设备的实际采样率，或当前选择映射到另一个物理端点：
```text
停止当前播放或预览。
完成后端清理。
丢弃所有 sample-domain 缓存和 BASSMIDI Stream。
显示非 Project 的设备变化提示。
下一次播放按新实际采样率重新 Preparing。
```

### 13.14.6 可调 buffer

Application Preferences 提供以下整数毫秒设置：

| 设置 | 合法范围 | 默认值 | 生效范围 |
|---|---:|---:|---|
| Render-Ahead Buffer | 20–2000 ms | 100 ms | 已渲染实时 PCM 队列目标容量 |
| Device Buffer Request | 5–200 ms | 50 ms | 向 WASAPI 请求的设备 buffer 时长 |

规则：
```text
只能在 Stopped 状态提交修改
非法值不提交、不静默 Clamp
修改后使实时 PCM、调度和设备连接相关缓存失效
Device Buffer Request 只是请求值；设备可以按自身能力调整实际 buffer
请求值被设备调整本身不算错误
只有后端或设备初始化失败才阻止播放
```

UI 必须只读显示后端初始化后的：
```text
设备实际采样率
设备实际 buffer 时长 / frame 数
实际 callback period
最近 callback frame 数或范围（如后端可取得）
```

设备决定的 callback period、callback frame 数和内部固定工作 block 不作为用户可调设置。
### 13.14.7 WASAPI 初版输出策略
正式 BASSWASAPI 输出固定使用：
```text
WASAPI Shared Mode
event-driven callback
stereo
interleaved IEEE float32
所选端点初始化后报告的实际混音采样率
```

初始化请求必须使用 `BASS_WASAPI_EVENT`，不得设置 `BASS_WASAPI_EXCLUSIVE`、`BASS_WASAPI_AUTOFORMAT`、`BASS_WASAPI_BUFFER` 或 `BASS_WASAPI_ASYNC`。采样率请求使用端点实际混音采样率语义，声道数固定请求 `2`；Device Buffer Request 传入设备 buffer 请求，period 请求为 `0`，由设备决定实际 callback period 和 callback frame 数。

初始化后必须立即读取并验证实际信息：
```text
仍为 Shared Mode
仍为 event-driven
sampleRate > 0 且可安全表示
channelCount = 2
sampleFormat = float32
```
实际采样率、实际 buffer frame 数、观察到的 callback frame 数 / period 是 Derived / Runtime Data，只读报告，不写入 Project。实际采样率与 Preparing 使用的 sample-domain 计划不一致时，本次 Preparing 失败并清理；下一次播放必须重新枚举设备、按新采样率重建计划，不得重采样旧计划。

初版不得静默回退到 Exclusive Mode、轮询 / push 模式、整数 sample format、mono / 多声道或另一采样率。设备不支持正式策略时，显示初始化失败并允许用户选择其他输出设备；不得影响 Project 打开、编辑、编译、保存、MIDI 导出或音频文件渲染。
---
## 13.15 实时播放输出链
### 13.15.1 输出格式
初版实时播放只定义 stereo 输出。

实时播放采样率不是 Project 固定值，也不写死为 48 kHz。它等于所选输出设备初始化后正式报告的实际采样率。所有 Port Stream、stereo mix、Master Volume、Limiter 和实时 PCM buffer 使用该采样率。

初版不支持：
```text
mono / stereo 可选
多声道实时输出
按 Port 输出到不同物理通道
按 Logical Track 输出到不同物理通道
```
### 13.15.2 Port 混音
多个 BASSMIDI Stream / Port 的音频输出先混合到同一个 stereo bus。
实时播放输出链为：
```text
Compiled Result
→ 播放调度 / 预渲染
→ 每个实际使用 Port 的 BASSMIDI Stream
→ 多 Stream stereo mix
→ Playback Master Volume
→ Limiter
→ 输出设备
```
### 13.15.3 内部精度
播放内部混音应使用足够避免明显累积削波的浮点音频表示。
第 13 章《播放与预览》 不固定具体使用：
```text
float32
float64
其他内部浮点格式
```
无论内部精度如何，WASAPI callback 边界按 BASSWASAPI 要求使用 interleaved float32 frame。
---
## 13.16 Playback Master Volume
初版需要定义项目播放总音量 / 监听音量。
规则：
```text
Playback Master Volume 属于 Playback Settings。
Playback Master Volume 保存进 Project。
Playback Master Volume 不改变 MIDI 编译结果。
Playback Master Volume 不写入 MIDI 导出。
Playback Master Volume 影响实时播放、预览和音频文件渲染的最终音频输出。
音频文件渲染不拥有独立导出音量 / 输出增益，也不提供 Master Volume bypass。
```
默认值：
```text
Playback Master Volume = -0.1 dB
```
播放总音量位于 Limiter 之前。
---
## 13.17 Limiter
### 13.17.1 存在原因
Midora 允许最多 256 个 Channel Unit 同时输出。
由于软件定位和事件乐器工作流，最终播放或音频文件渲染时可能出现总线音量过大、削波或爆音风险。
因此初版提供一个最简易内置 Limiter。
### 13.17.2 默认状态
```text
播放实时输出链路默认启用 Limiter。
音频文件渲染输出链路默认启用同一个或语义等价的 Limiter。
```
实时播放的 Limiter 开关仍由 Playback Settings 管理。
音频文件渲染使用同一算法 / 语义和正式生效的 Limiter 参数，但初版渲染链强制包含最终 Limiter：
```text
不提供音频渲染专用 Limiter 开关。
不提供音频渲染 Limiter bypass。
Limiter 无法初始化时，音频渲染准备失败。
```
### 13.17.3 语义边界
Limiter：
```text
不改变 MIDI 编译结果。
不影响 MIDI 导出。
不转换为 MIDI CC7 / CC11。
只属于最终音频输出保护处理。
```
Limiter 只是最简易保护，不保证专业混音质量，也不替代用户后期分轨混音。
### 13.17.4 参数暴露
初版 Limiter 只提供开关。
初版不提供：
```text
threshold
release
lookahead
ceiling
ratio
knee
完整母带处理参数
```
这些项目不向用户暴露不表示其值未定义；初版正式值与算法固定于第 13.17.6 节。
### 13.17.5 处理位置
Limiter 位于：
```text
所有 Port 的 stereo mix 之后
Playback Master Volume 之后
输出设备 / 音频文件写入之前
```
Event Instrument 预览、SubVoice 预览、Segment 预览也默认经过播放 Limiter。
所有实时发声都走同一播放输出链。
### 13.17.6 初版固定算法
初版 Limiter 算法版本为 `1`，固定为 stereo-linked、zero-look-ahead、sample-peak limiter。

逐 sample frame 处理时，输入 `left` / `right` 已经过所有 Port 求和与 Playback Master Volume。令前一 frame 后保存的线性增益为 `gain`，新任务或显式重置后的初值为 `1.0`：
```text
peak = max(abs(left), abs(right))
targetGain = peak > 1.0 ? 1.0 / peak : 1.0

if targetGain < gain:
    gain = targetGain
else:
    releaseCoefficient = exp(-1 / (sampleRate × 0.050))
    gain = 1 - ((1 - gain) × releaseCoefficient)

outputLeft  = left  × gain
outputRight = right × gain
```
规则：
```text
左右声道共享同一个 peak、targetGain 和 gain，不得分别限制。
attack 为当前 sample frame 立即生效，不做 look-ahead，也不引入算法延迟。
sample-peak ceiling 固定为线性 1.0；初版不检测 true peak 或 inter-sample peak。
release 是 50 ms 单极指数时间常数，按实际 sampleRate 计算系数。
audio block 边界不得重置 gain；不同 callback / 工作 block 大小必须产生相同连续处理语义。
新播放、预览或渲染任务以及 Stop / Reset 后必须把 gain 重置为 1.0；硬结束后不输出 release tail。
任一输入或输出样本为 NaN / Infinity 时，当前音频任务按一致性错误失败，不得静默钳位或继续。
```

这是最简易 sample-peak 保护算法，不承诺专业母带质量；瞬时 attack 可能改变极端瞬态，但不得替换成硬削波、自动归一化或另一套未版本化算法。
---
## 13.18 clipping 与 limiter activity
如果 Limiter 关闭且实时输出可能或已经发生削波：
```text
播放系统允许显示运行期 clipping 指示。
```
如果 Limiter 开启且发生明显限制处理：
```text
播放系统允许显示运行期 limiter activity 指示。
```
这些提示：
```text
属于实时播放运行期状态提示。
不进入 Project 编译诊断。
不标记 Project 已修改。
不保存进 Project 文件。
```
初版音频文件渲染不检测或报告 clipping / limiter activity；其强制启用的版本 1 Limiter 必须按第 15 章《音频文件渲染》输出范围规则处理最终样本。
---
## 13.19 预渲染 buffer 与 Buffering
### 13.19.1 基本策略
初版播放采用前方预渲染 / 预调度 buffer。
不采用：
```text
每个音频 buffer 回调时即时编译高层语义
完整预渲染整个播放范围后才允许播放
```
播放前必须准备足够安全的初始 buffer。
实时 PCM Render-Ahead Buffer 的用户可调目标由 §13.14.6 规定。
### 13.19.2 buffer 描述单位
播放预渲染 buffer 的系统级描述以音乐时间为主：
```text
tick
小节
四分音符
```
播放系统内部可以根据 Tempo Map 将预渲染音乐长度转换为实际秒数 / sample 范围，但语义要求仍以音乐时间范围为准。
初版正式实时合成与 Render-Ahead producer 的最大工作 block 固定为 `256 frames`。最终短块、事件边界前的短块和任务结束前的短块允许小于 256 frames，不得为凑满 block 越过事件或硬结束边界。

需要区分：
```text
音乐语义预调度范围：tick / 小节 / 四分音符
Render-Ahead PCM 容量：毫秒，按设备实际采样率换算为 frame
Device Buffer Request：毫秒，最终实际值由设备决定
内部固定工作 block：256 frames，不向用户暴露
```

Render-Ahead ring 容量不是固定 block 数，而是根据用户设置按以下规则换算：
```text
capacityFrames = ceil(actualSampleRate × bufferMilliseconds / 1000)
```
换算必须使用可检查溢出的整数运算；不得向下取整到短于请求时长，也不得为了对齐 256 frames 而静默扩大或缩小用户请求。
如果合法的低采样率与最小 buffer 设置使 ring 容量小于 256 frames，则 producer 本次实际工作块等于 ring 容量；`256 frames` 是正式最大值，不得以固定块为由拒绝合法 buffer 设置。
### 13.19.3 compiled result 与播放 buffer
播放 buffer 是 compiled result 之上的消费者缓存。
规则：
```text
compiled result 变更会使相关播放 buffer 失效。
播放 buffer 不是 compiled result 本身。
播放 buffer 不得独立保持旧语义直到用户手动刷新。
```
### 13.19.4 buffer underrun
播放中如果预渲染 / 调度 buffer 发生 underrun：
```text
不直接停止播放。
状态栏显示性能 / buffer 警告。
播放光标与声音输出在当前位置等待。
后台继续预渲染。
必须等到足够的音乐时间范围预渲染完成后，才恢复播放。
```
等待期间主状态仍为 `Playing`，内部子状态为：
```text
Playing.Buffering
```
Buffering 期间：
```text
允许 Stop。
允许跳转到新 tick。
跳转视为停止当前等待并从新 tick 重新冷启动播放。
保持音频静音，不循环最后一个 audio buffer。
恢复播放时从等待发生的同一 tick 继续，不按墙钟时间跳过音乐。
不改变“本次播放开始 tick”。
```
### 13.19.5 Buffering 恢复目标
恢复播放前目标预渲染长度为：
```text
min(当前拍号下的一整个小节长度, 8 个四分音符)
```
含义：
```text
常规拍号下尽量等待至少当前一小节。
如果当前小节超过 8 个四分音符，则以 8 个四分音符封顶。
```
### 13.19.6 连续 underrun
短时间内连续发生 underrun 时：
```text
仍继续采用等待预渲染再播放。
不自动进入 Error。
```
### 13.19.7 可等待性能不足与不可恢复错误
预渲染失败应区分：
```text
可等待的性能不足：进入 Buffering，等待后继续
不可恢复错误：进入 Error，Stop 清理
```
可等待性能不足例子：
```text
CPU 暂时跟不上导致 buffer underrun
后台预渲染队列暂时落后
短时间磁盘 / 系统调度抖动
```
不可恢复播放错误例子：
```text
C# Mapping Function 运行时异常
BASSMIDI Stream 创建失败
SF2 加载失败
输出设备丢失且无法恢复
compiled result 与播放缓存一致性校验失败
```
### 13.19.8 Buffering 与工程耗时
工程总耗时细则不在 第 13 章《播放与预览》 展开。
但可确认：
```text
播放 Buffering 期间，Project 仍处于打开状态。
如果工程总耗时系统正在累计项目打开时间，则 Buffering 时间仍计入工程耗时。
```

### 13.19.9 活动音频线程零托管分配

Preparing 阶段必须完成正式播放所需的 buffer、队列、事件批次和工作区分配。进入 Playing、Preview Playing 或 Buffering 后，以下音频活动线程和热路径不得产生托管堆分配：
```text
设备 callback
实时事件调度
BASSMIDI 拉取 / 合成协调
多 Port 混音
Master Volume / Limiter
Render-Ahead buffer 搬运
跨进程命令 / 状态共享内存热路径
Buffering 补充路径
实时预览的对应音频路径
```

固定工作区与 ring 必须在 Preparing 一次分配并重复复用。音频子进程内部使用一个有界 SPSC PCM ring，ring 的 frame 容量服从第 13.19.2 节的毫秒换算，不另设固定 block 数；256-frame producer 工作区独立于 ring 容量。实时 PCM 不跨进程传输。主进程与子进程间的运行时命令和状态使用固定版本、固定布局、有界的二进制共享内存协议；命令生产和消费热路径不得分配托管对象，不得使用 JSON、文本协议或逐消息对象反序列化。所有内存必须有明确上限、所有权和释放时机。

Preparing、Stop 清理和 Finalizing 可以产生托管分配。与音频后端同进程的 UI 或其他非音频线程允许分配并触发进程级 GC；该 GC 本身不构成“音频活动线程产生托管分配”的验收失败，但 callback deadline miss、underrun 或爆音仍按运行期性能问题记录。

### 13.19.10 约 200 ms 性能基准

实时播放和预览的端到端延迟以约 200 ms 作为性能测试基准。测试路径尽量覆盖：
```text
事件在视觉 / 调度语义上应生效或交互预览触发被接受
必要编译与准备
共享内存控制 IPC
事件调度与渲染
Render-Ahead 和设备 buffer
对应样本提交到 WASAPI 输出端点
```

外部 DAC、蓝牙设备自身额外缓冲、功放和声学传播不属于可稳定自动测量的软件边界，可以在专项人工测试中另行记录。

该 200 ms 是性能回归与架构选择基准：
```text
不是用户可调 Target Latency
不决定 Preparing / Playing 成败
超过基准不自动阻止播放
应记录测试失败、性能回归或运行期性能诊断
```
---
## 13.20 播放运行期提示与资源概要
以下内容属于播放运行期状态 / 性能提示，不进入 Project 编译诊断：
```text
buffer underrun
clipping
limiter activity
输出设备临时不可用
后端初始化失败
BASSMIDI runtime error
```
播放错误、underrun、clipping、Limiter activity 均不标记 Project 已修改。
播放运行期日志不保存进 Project 文件。
点击播放前 / Preparing 阶段，可以显示本次播放 compiled result 的资源概要，例如：
```text
使用 Port 数
峰值 Channel Unit 数
播放范围
Track 选择
是否启用 Limiter
```
资源概要只是播放信息，不作为 Project 诊断。
播放前不强制弹出资源概要窗口，可在面板 / 状态栏显示。
---
## 13.21 Event Instrument 预览
### 13.21.1 复用编译与播放管线
Event Instrument 预览必须走：
```text
临时 Event Instrument Preview CompileContext
→ canonical compiled result
→ 播放系统消费
```
不得直接手写 MIDI 事件发给 BASSMIDI。
Event Instrument 预览产生的 Channel Group 只属于本次预览任务。
规则：
```text
不写回 Project。
不影响正式编译资源缓存。
不保留分配结果供正式播放复用。
```
### 13.21.2 预览参数
Event Instrument 预览面板初版允许临时设置：
```text
previewPitch
previewVelocity
previewGateLength
previewTempo
```
这些值：
```text
不写入 Project。
不进入 Undo / Redo。
不标记 Project 已修改。
只作为本次预览 CompileContext 的临时输入。
```
默认值：
```text
previewPitch = Event Instrument Root Note
previewVelocity = 100
previewGateLength = Event Instrument Template Length
previewTempo = 当前播放光标所在 tick 的有效 Tempo
```
### 13.21.3 previewTempo
Event Instrument 预览不绑定项目时间线范围，但可以借用当前播放光标处 Tempo 作为默认试听速度。
规则：
```text
打开预览面板时，previewTempo 默认取当前播放光标处有效 Tempo。
用户手动修改 previewTempo 后，后续预览使用用户设置值。
用户可通过“重置为当前光标 Tempo”之类操作恢复跟随当前播放光标 Tempo。
```
Event Instrument 预览只借用当前光标 Tempo，不借用当前光标 Time Signature。
初版 Event Instrument 预览只使用固定 previewTempo，不支持预览过程中 Tempo Map 变化。
### 13.21.4 预览与 Conductor Track
Event Instrument Library 直接预览某个 Event Instrument 时：
```text
不绑定项目时间线范围。
不使用 Project End Marker。
不使用普通 Marker。
不使用 Segment 位置。
不使用 Track 上下文。
默认 Tempo 可取当前播放光标处 Tempo。
```
更准确地说：
```text
Event Instrument 预览不参与项目时间线编译；
但可以借用当前播放光标处 Tempo 作为默认试听速度。
```
---
## 13.22 Event Instrument 虚拟键盘预览
### 13.22.1 键盘显示
Event Instrument 预览面板需要提供一排虚拟钢琴键盘。
系统级要求：
```text
键盘使用简单全高黑白键即可。
键盘高亮标记当前 Event Instrument Root Note。
键盘可以选取显示范围。
键盘可以缩放。
```
显示范围 / 缩放只影响 UI。
合法可预览 pitch 仍为：
```text
0–127
```
### 13.22.2 鼠标触发语义
在某个键上：
```text
鼠标左键按下 = Gate Start
鼠标左键松开 = Gate End
```
鼠标按下时立即开始发声。
按住期间视为 Gate 仍未结束。
松开时向预览任务发送 Gate End，之后按生命周期规则进入：
```text
Release
Tail
Reset
预览任务结束
```
### 13.22.3 previewGateLength 的作用
`previewGateLength` 作为普通 Preview 按钮 / 一次性预览的默认 Gate Length。
虚拟键盘按住预览时：
```text
Gate Length 由实际按住时长决定。
previewGateLength 不强制键盘按住预览的 Gate End。
```
### 13.22.4 普通 Preview 按钮
除了虚拟键盘按住预览，面板还应允许一个普通 Preview 按钮。
普通 Preview 按钮使用当前：
```text
previewPitch
previewVelocity
previewGateLength
previewTempo
```
执行一次固定长度预览。
### 13.22.5 单键限制
初版虚拟键盘只允许单键预览。
不支持多个键同时按下并同时预览多个 Event Instrument Instance。
当已有键盘预览活动时，按下新键前必须释放旧键，或新键替换旧键并清理旧预览。
具体 UI 行为由 第 17～20 章的 UI 与交互规格 或实现设计阶段细化。
### 13.22.6 velocity 规则
面板提供复选框：
```text
固定 velocity 预览
```
默认：
```text
固定 velocity 预览 = 开启
```
开启时：
```text
鼠标垂直位置不影响 velocity。
预览使用面板上的 previewVelocity 值。
```
关闭时：
```text
鼠标按下位置越靠近键盘顶部，velocity 越低。
鼠标按下位置越靠近键盘底部，velocity 越高。
键顶部 = velocity 1。
键底部 = velocity 127。
线性映射并四舍五入到整数。
```
velocity 在 Gate Start 固定。
鼠标按下后在同一键上上下移动，不会改变已触发 velocity。
---
## 13.23 SubVoice 预览
单独预览某条 SubVoice 时：
```text
默认触发音高使用该 SubVoice 的 Effective Root Note。
虚拟键盘高亮该 SubVoice 的 Effective Root Note。
```
SubVoice 预览仍以所属 Event Instrument 为上下文。
规则：
```text
不允许 SubVoice 脱离 Event Instrument 单独编译。
不允许直接裸发 SubVoice MIDI 事件。
仍走 Event Instrument 生命周期、Mapping、Preview CompileContext 和临时 Channel Unit 分配。
只是预览输出过滤到该 SubVoice。
```
---
## 13.24 Segment 预览
### 13.24.1 范围
Segment 预览默认范围为该 Segment 当前有效裁剪窗口。
不预览裁剪窗口外隐藏内容。
Segment 预览只播放被预览 Segment 所在 Track / Segment。
不播放同时间范围内其他 Track。
### 13.24.2 与 Mute / Solo 的关系
当用户明确预览某个 Segment 时：
```text
忽略当前 Track Mute / Solo 状态。
显式预览对象应发声。
```
但以下情况仍不能预览：
```text
Track 未绑定 Event Instrument
Track 引用断裂
Segment 预览编译失败
无有效 SF2
播放后端错误
```
### 13.24.3 项目时间绑定
时间线上的 Segment 预览应绑定项目时间上下文。
Segment 预览绑定项目时间时：
```text
包含 Conductor Track 上下文。
使用实际 Tempo Map。
包括 Segment 范围内 Tempo 变化。
恢复起点前全局状态与非 Note 状态。
```
Segment 预览不提供临时 Tempo 覆盖。
Segment 预览始终使用项目实际 Tempo Map。
### 13.24.4 预览光标
Event Instrument 预览不改变主时间线播放光标。
Segment 预览也不改变主时间线播放光标。
Segment 预览播放时，UI 可以显示独立的 Segment 预览光标，但不改变主播放光标状态。
---
## 13.25 空项目播放
空项目在有有效 SF2 的情况下允许点击播放。
行为：
```text
进入播放流程。
无音乐输出。
可按 Conductor Track 时间状态运行。
如果默认范围为零长度，则立即进入 Stop 清理流程。
```
无 SF2 时仍不能播放。
---
## 13.26 初版明确不支持的功能
初版播放系统不支持：
```text
Pause
Metronome / Click
Count-in
Scrubbing
实时 MIDI 输入录制
播放倍率 / playback speed multiplier
多播放任务混合
多声道实时输出
分轨实时监听输出
主播放显式 Track 选择
```
说明：
```text
Count-in 指正式播放开始前的预备拍。
Scrubbing 指拖动播放头时实时搓音 / 试听。
```
这些功能若未来加入，必须单独定义与 compiled result、状态恢复、输出链、Mute / Solo、预渲染 buffer 和播放任务互斥的关系。
---
## 13.27 与音频文件渲染的边界
完整音频文件渲染规则由 第 15 章《音频文件渲染》 定义。
第 13 章《播放与预览》 只确认与播放输出链共享的系统语义：
```text
音频文件渲染不得复用运行中播放 buffer、活动 BASSMIDI 状态或播放设备状态作为结果。
音频文件渲染使用 Audio Render CompileContext。
音频文件渲染使用 Project 当前正式生效的 Playback Master Volume。
音频文件渲染不拥有独立输出增益，也不提供 Master Volume bypass。
音频文件渲染使用与播放一致语义的最终 Limiter。
初版音频文件渲染不提供独立 Limiter 开关或 bypass。
多 Port 音频先混合为 stereo，再经过 Master Volume 与最终 Limiter。
Track Mute / Solo 不影响音频文件渲染成品。
离线渲染不依赖实时播放设备、Windows 音量或设备 DSP。
```
整曲、按 Logical Track 分轨、普通 RIFF/WAVE、自定义文件采样率、范围、文件事务、取消和诊断均由 第 15 章《音频文件渲染》 定义，不在本章复制。
---
## 13.28 与 MIDI 导出的边界
播放系统不定义 MIDI 文件结构。
本章只确认：
```text
播放输出链、Playback Master Volume、Limiter、输出设备、Mute / Solo 均不影响 MIDI 导出数据。
MIDI 导出必须使用导出专用 CompileContext。
MIDI 导出不得复用运行中播放 buffer。
```
---
## 13.29 与 UI 章节的边界
第 13 章《播放与预览》 只定义播放系统级交互语义。
不定义：
```text
播放按钮布局
状态栏具体样式
虚拟键盘绘制细节
资源概要面板布局
Limiter activity 指示器样式
clipping 指示器样式
播放设备设置面板布局
buffer 设置和实际值面板布局
预览面板完整 UI
```
这些内容由 第 17～20 章的 UI 与交互规格 或实现设计阶段细化。
---

## 13.30 音频后端进程拓扑

初版正式音频后端固定为由主应用管理的单个内部音频子进程。主应用拥有 Project、Compiler、Canonical Compiled Result、UI 和任务协调；音频子进程独占 BASS、BASSMIDI、Limiter、Render-Ahead、BASSWASAPI、设备 callback 和文件专用 OutputDevice。主应用不得加载或持有这些正式音频后端的原生全局状态与 handle。

子进程必须遵守：
```text
它不提供 UI。
它不能独立打开或解释 .midora Project。
它只接收冻结的 canonical compiled result、已解析音频设置、必要 SF2 资源信息和控制命令。
它不得重新解释 Event Instrument、Mapping、Lifecycle、Segment 或资源分配语义。
实时 PCM 只在子进程内部的 Render-Ahead ring 与 WASAPI callback 之间流动，不跨进程传输。
运行时命令与状态使用固定版本、固定布局、有界的二进制共享内存 ABI；禁止 JSON、文本协议和逐消息对象反序列化。
Playing、Preview Playing、Buffering 与 Rendering 的命令 / 状态 IPC 热路径不得产生托管堆分配。
IPC 延迟和吞吐量计入 §13.19.10 的约 200 ms 性能基准。
子进程异常退出时，当前播放 / 预览进入 Error 并完成主进程侧资源清理；允许通过 Reset Playback Engine 重建子进程。
```

音频子进程必须针对每个正式支持的 Windows CPU RID 独立 Native AOT 发布，不允许在正式运行时依赖 JIT 编译；具体 RID 集合由产品发布架构决定。Native AOT 不替代零分配、callback deadline、underrun、故障恢复和确定性验收。

进程内后端或“子进程合成、主进程 WASAPI”的混合链只允许作为开发期对照测试，不是正式消费者，不得由产品运行时回退或切换进入。
---
