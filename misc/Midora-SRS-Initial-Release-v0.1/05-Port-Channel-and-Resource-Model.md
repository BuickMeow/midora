# 第 5 章 Port、Channel 与资源模型

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 MIDI 1.0 资源域、Channel-Wide 状态污染、Channel Unit/Channel Group、动态 Port 使用、资源共享、峰值占用、失败与诊断边界。

## 5.1 Port 需求
### 5.1.1 Port 的定义
Port 是 Midora 中用于区分独立 MIDI 资源域的系统级单位。
在资源模型中：
```text
Port 1 / Channel 1
Port 2 / Channel 1
```
是两个完全不同的 Channel Unit。
相同 Channel 编号在不同 Port 中不共享 Channel-Wide 状态。
### 5.1.2 Port 数量边界
Midora 初版最多支持：
```text
16 Ports
```
Port 的 UI 显示编号为：
```text
Port 1–16
```
系统内部可按实现需要使用 0-based 编码，但不得向用户暴露 0-based Port 编号。
### 5.1.3 Port 按需动态使用规则
初版不采用“16 个 Port 固定常驻”的运行模型。
系统级规则为：
```text
Project 概念上最多支持 16 个 Port
用户不负责创建或删除 Port
Port 由编译器 / 资源系统按实际需要使用；非空 Pure MIDI Fixed Root 可以显式引用其中的 Port.Channel，但不创建新的 Port
UI 与编译结果只应呈现实际使用或当前操作上下文需要呈现的 Port
空闲 Port 不应导致常驻 BASSMIDI 实例
当实际需要超过 16 个 Port 时，编译失败
```
该规则的目的：
```text
避免用户管理底层 Port 创建数量
避免 16 个 BASSMIDI 实例和 16 份音色库常驻内存
保持最多 16 Ports 的 MIDI 1.0 资源上限
让编译器承担 Logical Usage/Auto Root 的 Port 使用判断，同时尊重非空 Fixed Root 固定路由
```
### 5.1.4 动态 Port 分配带来的实现设计问题
Port 按需动态使用会带来后续编译与播放设计问题，包括但不限于：
```text
随机位置开始预渲染 / 播放时，如何快速判断需要多少 Port
如何避免每次随机播放都全曲重新编译导致卡顿
局部编辑后只重编后部内容时，如何保持 Port 分配稳定
当前部需要的 Port 数多于后部时，后部如何继承或兼容此前的最大资源布局
资源缓存、增量编译、播放上下文解析如何共同维护 Port 数稳定性
```
这些问题必须在 第 12 章《编译系统与 Canonical Compiled Result》 编译系统、第 13 章《播放与预览》 播放系统以及实现设计设计中仔细设计。
本章只确认以下系统级原则：
```text
Port 数按需动态使用，非空 Fixed Root 引用的 Port 视为已使用
Port 数判断属于编译 / 播放上下文解析的重要职责
初版不得为了简化实现而强制 16 个 BASSMIDI 实例常驻
初版不得把 Port 创建 / 删除责任交给用户
```
### 5.1.5 Port 与播放后端的关系
播放和音频渲染仍消费 canonical 中最终确定的 Midora Port / Channel 路由，但原生合成粒度不是 Port。每个实际使用的抽象 Channel Unit 使用一个干净的 1-channel BASSMIDI Stream 语义；实际 native Stream 由有界 pool 按生命周期复用。
系统级含义是：
```text
canonical Port / Channel = 确定性资源路由与 MIDI 导出地址
abstract Channel Unit = 独立的单声道 synth 与 raw PCM 缓存语义
```
因此，任何 Unit 之间都不得共享可污染的 MIDI Channel-Wide 状态；物理 Port / Channel 号不得进入抽象 Unit PCM 缓存身份。
未被当前编译结果或当前播放 / 渲染上下文使用的 Unit，不应强制创建对应 BASSMIDI Stream。
具体 BASSMIDI Stream 创建、销毁、SF2 加载、混音、buffer 拉取等由 第 13 章《播放与预览》 和实现设计阶段细化。
### 5.1.6 Port 与 MIDI 导出的关系
MIDI 导出系统必须能够表达 Midora 的 Port 概念。
已确定导出模式包括：
```text
整曲 MIDI
按 Logical Track 导出
按 Port 导出
```
其中按 Port 导出时，一个实际使用的 Port 对应一个 MIDI 文件。
具体 Port meta event、Device / Port Name、文件命名和事件排序由第 14 章《MIDI 导出》规定。
---
## 5.2 Channel 需求
### 5.2.1 Channel 的定义
Channel 是 MIDI 1.0 在单个 Port 内的 16 个通道之一。
每个 Port 固定包含：
```text
16 Channels
```
UI 显示编号为：
```text
Channel 1–16
```
MIDI 数据内部 Channel 编码仍按标准使用：
```text
0–15
```
UI 不暴露 0-based Channel 编号。
### 5.2.2 Channel 的 Port 局部性
Channel 编号只在 Port 内有意义。
例如：
```text
Port 1 / Channel 5
Port 2 / Channel 5
```
不是同一个资源。
任何 Channel-Wide 状态都只污染同一个 Port 内同一个 Channel，不跨 Port 污染。
### 5.2.3 Channel 10 的 Melodic / Percussion 规则
Midora 不再对全部数据强制单一 Channel 10 模式，而是按来源明确区分：

```text
Logical Track / Event Instrument allocation -> Channel 10 始终显式初始化为 Melodic
Pure MIDI Root                          -> 由 Root 的 Melodic / Percussion 源字段决定
SMF import Channel 10 Root              -> 默认 Percussion
```

同一个 Channel Unit 不能同时属于 Logical allocation 与 Pure MIDI Root，因此模式不会在同一 Unit 上产生歧义。初版仍不允许 Event Instrument/SubVoice 通过 SysEx 将其获配 Channel 10 改为 drum mode；导入后 opaque SysEx 的保留不构成 Event Instrument 自由 SysEx 编辑。

### 5.2.4 Channel 10 的资源分配地位
Channel 10 继续作为 256 Unit 池中的普通地址参与 Root 预留、Auto Root 分配和 Logical 分配。它不被系统全局保留为鼓通道；只有占用该 Unit 的 Pure MIDI Root 可以把该 Root 的合成/导出模式设为 Percussion。
---
## 5.3 Channel Unit 需求
### 5.3.1 Channel Unit 的定义
Channel Unit 是 Midora 资源分配的最小唯一单位。
定义为：
```text
Channel Unit = Port + Channel
```
例如：
```text
Port 1 / Channel 1
Port 1 / Channel 2
Port 2 / Channel 1
```
均为不同 Channel Unit。
### 5.3.2 Channel Unit 数量
Midora 初版最大 Channel Unit 数量为：
```text
16 Ports × 16 Channels = 256 Channel Units
```
这是一项系统级硬边界。
如果项目在当前编译语义下需要超过可用 Channel Unit，或无法在当前时间范围内找到满足隔离要求的 Channel Unit：
```text
编译失败
```
不允许 Voice Steal。
不允许自动删除、截短或降级用户内容来规避资源不足。
### 5.3.3 Channel Unit 可用性与 Root 预留
初版不允许用户禁用 Port 或 Channel Unit。Fixed Port.Channel 只能随至少一条 Pure MIDI Track 存在，不提供空 Unit 预留。
系统级规则为：
```text
所有最多 256 个 Channel Units 都属于编译器可用资源池
用户不能禁用整个 Port
用户不能禁用单个 Channel Unit
Fixed Root 先预留其精确 Channel Unit
含参与 Segment 内容的 Auto Root 随后按最早成员的 global Arrangement Track order 取得最低未预留 Unit
Logical/Event Instrument 分配只使用剩余 Unit
```
说明：
```text
当前规则不等于 256 个 Channel Units 都会在运行时常驻。
它只表示它们都是编译器可用的潜在资源。
实际使用的 Port / Channel Unit 由 Root 源路由和编译结果共同决定。
```
### 5.3.4 Channel Unit 的生命周期占用
Event Instrument Instance 在其 Rendered Instance Length 范围内要求独立或共享的 Channel Group。对于产生 Note 的 Segment，为保证精确 NoteOff 后的 SoundFont 原生 release 不被音频 Unit fragment 窗口裁断，编译器还必须把该 Segment 已启用的 Channel Unit lane 保留到 Segment End；同一 Segment 内后续不重叠 instance 可以复用同一保留 lane，但每次非重叠复用都必须先按目标闭包建立 Reset Defaults 与 Initial State。普通 instance 结束本身不执行通用目标 Reset。
占用范围由以下后续系统共同决定：
```text
Template Length
Gate Length
Rendered Instance Length
短音策略
长音策略
Loop / Envelope
Overlap 策略
Segment 裁剪
Reset 策略
Project End Marker 范围规则
```
本章只确认：Channel Unit 是否可被复用，取决于占用时间范围、Channel-Wide 状态污染风险和 Reset 规则。
具体实例生命周期计算由 第 10 章《实例生命周期、Loop、Envelope 与重叠》、第 11 章《Logical Track、Segment 与编曲语义》、第 12 章《编译系统与 Canonical Compiled Result》 细化。
---
## 5.4 Channel Group 需求
### 5.4.1 Channel Group 的定义
Channel Group 是一个 Event Instrument Instance 在编译后占用的一组 Channel Unit。
例如，一个包含多个 SubVoice 的事件乐器被一次逻辑音符触发后，可能需要：
```text
Channel Group
├─ SubVoice 1 -> Port 1 / Channel 1
├─ SubVoice 2 -> Port 1 / Channel 2
└─ SubVoice 3 -> Port 2 / Channel 1
```
### 5.4.2 Channel Group 原子分配
Channel Group 分配必须是原子的：
```text
要么整个 Channel Group 分配成功
要么编译失败
```
不允许出现以下状态：
```text
SubVoice 1 分配成功
SubVoice 2 分配成功
SubVoice 3 分配失败但项目继续编译
```
一旦某个 Event Instrument Instance 所需 Channel Group 无法完整分配，相关编译流程必须进入失败状态。
### 5.4.3 Channel Group 跨 Port
Midora 系统级始终允许一个 Channel Group 跨 Port。
规则：
```text
编译器可自由跨 Port 分配 Channel Group
Event Instrument 不需要显式允许跨 Port
用户不需要显式允许跨 Port
初版不提供“同一实例所有 SubVoice 必须位于同一 Port”的约束
```
目的：
```text
提高 Channel Unit 利用率
降低单 Port 内资源碎片造成的失败概率
适应多 SubVoice 事件乐器的资源需求
```
跨 Port 分配不得改变 Event Instrument 的音乐语义。
如果未来功能要求同一 Event Instrument Instance 的所有 SubVoice 必须处于同一 Port，应作为额外约束显式设计，不能默认假设。
### 5.4.4 Channel Group 与 SubVoice
初版强制采用以下模型：
```text
一个 Event Instrument Instance 中，每条 SubVoice 对应一个 Channel Unit
```
因此：
```text
SubVoice 数量 = 单个 Event Instrument Instance 所需 Channel Unit 数量
```
该规则适用于初版全部 SubVoice，包括只包含 Note On / Note Off、没有任何 Channel-Wide 事件的 SubVoice。
初版不允许多个 SubVoice 共享同一个 Channel Unit。
初版不做：
```text
SubVoice 自动合并
Note-only SubVoice 共享 Channel Unit
用户显式标记 SubVoice 可共享
编译器基于事件内容自动压缩 SubVoice 数量
```
原因：
```text
保持资源模型清晰
降低 Channel-Wide 状态污染解释复杂度
避免在本规格提前引入 SubVoice 合并语义
```
### 5.4.5 单实例 SubVoice 数超过 256
如果某个单一 Event Instrument Instance 的 SubVoice 数超过 256，则该实例所需 Channel Unit 数超过系统上限。
结果：
```text
编译失败
```
是否在 Event Instrument 编辑阶段限制 SubVoice 数量，由 第 8 章《SubVoice 与 MIDI 事件编辑》 或实现设计阶段细化。
本章只确认：资源系统不会尝试通过自动合并 SubVoice 来规避该失败。
---
## 5.5 Channel-Wide 状态污染规则
### 5.5.1 Channel-Wide 的系统级含义
MIDI 1.0 中，很多事件不是作用于单个 Note，而是作用于整个 Channel。
如果两个互相重叠的实例共享同一个 Channel Unit，并且任一实例写入 Channel-Wide 状态，则可能导致状态污染。
例如：
```text
Instance A 在 Port 1 / Channel 1 写 Pitch Bend 曲线
Instance B 同时在 Port 1 / Channel 1 发 Note
```
结果可能是：
```text
Instance B 的 Note 也被 Instance A 的 Pitch Bend 影响
```
这与 Midora 的事件乐器实例隔离目标冲突。
### 5.5.2 默认 Channel-Wide 事件类型
初版默认按以下原则处理：
```text
Note On / Note Off：非 Channel-Wide
除 Note 外的 MIDI Channel Voice / Channel Mode / Channel 状态类事件：默认 Channel-Wide
```
包括但不限于：
```text
Control Change
Program Change
Pitch Bend
Pitch Bend Range
RPN
NRPN
Bank Select
Channel Pressure
Sustain Pedal
Expression
Modulation
All Sound Off
All Notes Off
Reset All Controllers
```
CC91（Reverb Send）与 CC93（Chorus Send）属于合法 Channel-Wide 事件：Pure MIDI Track 可以创建、导入、编译和导出它们。Event Instrument/SubVoice 的创建与 Mapping 面仍不开放 CC91/CC93；正式 BASSMIDI 音频路径继续使用 `NOFX`，因此不呈现其 Reverb/Chorus 效果。
其中某些事件是否允许用户在 Event Instrument 中直接编辑，由第 8～9 章细化；Pure MIDI Track 的完整 Channel Voice 面由第 23 章固定。
本章只定义：一旦某事件被视为 Channel-Wide，它就会影响 Channel Unit 共享安全性。
初版不允许用户编辑事件作用域表。
### 5.5.3 Channel-Wide 状态污染的时间条件
Channel-Wide 状态污染主要在时间重叠或状态未被 Reset 时发生。
系统级规则：
```text
重叠实例共享同一 Channel Unit 时，必须检查 Channel-Wide 状态污染风险
同一 Segment 内的非重叠实例在旧实例已结束后可以复用同一保留 Channel Unit lane；新实例起点必须先执行 lane 激活 Reset Defaults，再应用 Initial State/用户状态和 NoteOn
共享 lane 内仍存在重叠实例时，后续 Gate Start 不重复执行 lane 激活 Reset；需要独立 Channel-Wide 起点状态时必须使用 Channel Isolation
不同 Segment / Track 不得在前一 Segment End 之前取得该保留 lane；否则无法在不误杀新实例的情况下于前一 Segment End 执行 CC120，也无法保持 Track Mute/Solo 与 Segment PCM 的尾音归属
前一 Segment 已到达 Segment End 后，不要求后续实例必须是同一 Event Instrument 才能复用 Channel Unit
```
因此，本章不采用以下规则：
```text
同一 Channel Unit 只能被同类 Event Instrument 复用
非重叠情况下仍按事件类型永久隔离 Channel Unit
```
### 5.5.4 Channel Unit 可共享的基本条件
两个实例或两个运行状态要安全共享同一个 Channel Unit，至少需要满足：
```text
时间占用范围不发生会造成污染的重叠
共享期间不存在互相影响的 Channel-Wide 状态写入
必要 Reset 已在释放前完成
共享不违反 Event Instrument 的音符实例隔离规则
共享不违反重叠策略
共享不违反 Segment 裁剪和生命周期规则
```
具体判断算法由 第 12 章《编译系统与 Canonical Compiled Result》 或实现设计阶段细化。
### 5.5.5 非重叠复用
当某个 Channel Unit 在时间上已经释放，并且必要 Reset 已完成后，后续 Event Instrument Instance 可以复用该 Channel Unit。
这种复用是 Midora 资源系统的基本能力。
### 5.5.6 重叠共享
重叠共享 Channel Unit 只有在语义上安全时才允许。
关闭音符实例隔离的简单和弦型事件乐器可能允许多个 Note 共用同一 Channel Group。
但只要存在依赖每音符独立 Channel-Wide 状态的行为，就不能安全共享同一个 Channel Unit。
具体哪些场景允许重叠共享，需要由音符实例隔离、事件作用域、重叠策略和编译系统共同细化。
---
## 5.6 音符实例隔离与资源系统
### 5.6.1 开启音符实例隔离
当 Event Instrument 开启 Per-Note Instance Isolation 时：
```text
每个逻辑音符生成独立 Event Instrument Instance
每个 Event Instrument Instance 独占一组 Channel Group
```
同一 tick 出现多个音符时，每个音符都可能需要独立 Channel Group。
如果 Channel Unit 不足：
```text
编译失败
```
不支持 Voice Steal。
### 5.6.2 关闭音符实例隔离
当 Event Instrument 关闭 Per-Note Instance Isolation 时：
```text
多个逻辑音符可以共用一组 Channel Group
```
但此时应禁止依赖每音符独立 Channel-Wide 状态的功能，例如：
```text
Note → Event 映射
事件循环段
每音符独立 envelope
每音符独立 loop phase
每音符独立 Pitch Bend
每音符独立 CC
每音符独立 RPN / NRPN
```
### 5.6.3 关闭音符实例隔离时的共享范围
关闭音符实例隔离时，Channel Group 共享范围定义为：
```text
仅同一 Logical Track / Event Instrument Binding 内的重叠音符共享 Channel Group
```
不允许：
```text
同一 Event Instrument 在整个 Project 内共享 Channel Group
不同 Logical Track 即使引用同一 Event Instrument 也共享 Channel Group
同一 Event Instrument Library 定义的所有使用处共享运行状态
```
原因：
```text
多个 Logical Track 即使引用同一 Event Instrument，其运行状态也必须分离
Logical Track / Event Instrument Binding 是更符合直觉的运行状态边界
```
---
## 5.7 空项目、无 SF2、父节点损坏与资源系统
### 5.7.1 空项目
空项目允许编译。
空项目没有 Event Instrument Instance，因此通常不需要分配 Channel Group。
是否仍生成全局初始化事件、空 Port 流或仅生成 Conductor Track 相关结构，由 第 12 章《编译系统与 Canonical Compiled Result》 和 第 14 章《MIDI 导出》 细化。
### 5.7.2 无 SF2 状态
无 SF2 状态下：
```text
不创建 BASSMIDI 实例
不能播放
不能预览
不能音频渲染
```
但无 SF2 不影响 MIDI 导出。
因此，资源系统本身不应因为无 SF2 而改变 Port / Channel / Channel Unit 的编译语义。
确认规则：
```text
无 SF2 不影响 MIDI 编译
无 SF2 不影响资源分配语义
无 SF2 不影响 MIDI 导出
无 SF2 只影响播放、预览和音频渲染
```
### 5.7.3 Logical Track Usage 错误
有内容的 Logical Track 必须且只能引用一个有效 Event Instrument Usage，Usage 必须非空且引用一个有效 Definition。空壳未绑定 Track 不参与编译且不产生诊断；有内容无 Usage、空 Usage或 kind/Definition 错误是结构 Error。
---
## 5.8 资源不足、警告与失败规则
### 5.8.1 资源不足
以下情况应导致编译失败：
```text
所需 Channel Unit 超过 256 个
某一时间范围内可用 Channel Unit 不足
某个 Event Instrument Instance 所需 Channel Group 无法完整分配
同一 Channel Unit 共享会导致 Channel-Wide 状态污染且无安全替代分配
跨 Port 分配也无法满足资源需求
实际所需 Port 数超过 16
```
编译失败时，错误信息应尽量定位到：
```text
Logical Track
Segment
Event Instrument
SubVoice
Note
Tick
所需 Channel Unit 数量
可用 Channel Unit 数量
可能冲突的实例或时间范围
```
具体诊断格式由第 15 章《音频文件渲染》规定。
### 5.8.2 不支持 Voice Steal
Midora 不支持 Voice Steal。
系统不得通过以下方式自动解决资源不足：
```text
抢占旧实例
删掉旧音符
截断旧尾巴
降低音符实例隔离语义
自动合并 Channel-Wide 状态
自动忽略部分 SubVoice
自动把 Channel-Wide 事件降级为非 Channel-Wide
自动合并 SubVoice
```
### 5.8.3 资源使用量 Info 阈值
本规格曾建议资源使用警告采用 80% / 95% 阈值。
本章根据正式决定，将初版资源使用量提示规则统一为：
```text
仅当 Channel Unit 使用量大于等于 248 时产生资源使用量 Info
资源不足时编译失败
```
说明：
```text
248 / 256 = 96.875%
初版不采用 80% 普通 Warning 阈值
初版不采用 95% 严重 Warning 阈值作为独立系统级规则
248 尚未达到 256 上限，不得因“Warning 视为 Error”策略阻止编译
资源不足仍然是编译失败，不只是提示
```
第 12 章《编译系统与 Canonical Compiled Result》、第 15 章《音频文件渲染》和 UI 章节必须把 `>= 248 Channel Units` 显示为 `Info`，不应重新引入 80% / 95% 双阈值，除非修订本章或更高层需求。
### 5.8.4 资源使用量计算口径
资源使用量按“Root 全局预留/分配数 + 整曲任一 tick 的 Logical/Event Instrument 峰值同时占用数”计算。
计算口径：
```text
CombinedPeakChannelUnits = AllocatedRootUnits + PeakLogicalUnits
ResourceUsage = CombinedPeakChannelUnits / 256
```
其中：
```text
AllocatedRootUnits = 全部非空 Fixed Roots + 含可编译内容的 Auto Roots
PeakLogicalUnits = 整曲任一 tick 同时占用的最大 Logical/Event Instrument Channel Unit 数
```
不采用以下口径作为初版资源 Info 依据：
```text
整曲曾经使用过的不同 Channel Unit 总数
所有历史使用过的 Port / Channel 去重数量
单 Port 峰值使用率
```
单 Port 峰值、曾用资源数、资源占用图等可作为未来 UI 或诊断增强，但不是本规格规定的初版 Info 口径。
### 5.8.5 资源分配失败诊断
资源分配失败时，诊断应尽量列出：
```text
理论最小所需 Channel Unit 数
当前峰值冲突位置
导致峰值的相关 Logical Track / Segment / Event Instrument / Note
相关 MIDI Channel Root / Pure MIDI Track / Midi Segment
可用 Channel Unit 数量
是否因为单实例 SubVoice 数超过上限
是否因为跨 Port 后仍无法满足资源需求
```
允许在复杂情况下给出近似定位或主要冲突范围。
初版不要求所有情况下都证明数学上的最小不可满足集合。
---
## 5.9 与编译系统的关系
编译系统负责实际执行 Channel Unit 与 Channel Group 分配。
本章只规定编译系统必须满足的资源语义：
```text
遵守最多 16 Ports
遵守每 Port 16 Channels
遵守最多 256 Channel Units
按需动态使用 Port
不把 Port 创建 / 删除责任交给用户
不强制 16 个 Port 常驻
先验证并预留 Fixed MIDI Channel Roots
再按最早成员的 global Arrangement Track order 分配含参与 Segment 内容的 Auto Roots
Logical/Event Instrument 分配绕开全部 Root Unit
Logical 路径的 Channel 10 强制 melodic；Pure MIDI Root 遵守其 Melodic/Percussion Mode
把 Channel 10 作为普通可寻址 Channel Unit
不允许用户手动指定 Event Instrument / SubVoice 固定使用某个 Port 或 Channel
不允许用户禁用 Port / Channel Unit
遵守 Channel-Wide 状态污染规则
遵守 Event Instrument Instance 与 Channel Group 的关系
初版遵守一条 SubVoice 对应一个 Channel Unit
初版不做 SubVoice 共享 / 合并
遵守 Channel Group 原子分配规则
始终允许跨 Port 分配 Channel Group
资源不足时编译失败
不支持 Voice Steal
按 Root 全局预留/分配数加 Logical 峰值占用计算组合资源使用量
Channel Unit 使用量 >= 248 时产生资源使用量 Info
```
具体编译阶段、扫描顺序、排序规则、分配启发式、回溯策略、动态 Port 数判断、增量编译稳定策略或失败定位算法，由 第 12 章《编译系统与 Canonical Compiled Result》 和实现设计阶段细化。
---
## 5.10 与播放、预览、音频渲染的关系
播放、预览和音频渲染依赖编译结果中的 Port-separated MIDI event streams 和 Channel Unit 分配结果。
播放系统必须遵守：
```text
每个实际使用的抽象 Channel Unit 使用独立的 1-channel BASSMIDI Stream 语义
空闲 Unit 不应强制创建 BASSMIDI Stream
Logical/Event Instrument canonical Channel 10 Unit 必须按 melodic 初始化
Pure MIDI Root Unit 必须按 Root 的 Melodic/Percussion Mode 初始化；不得对 Percussion Root 无条件执行 melodic 初始化
同一 Port 内的 Channel-Wide 状态按编译结果生效
不同 Port 的同号 Channel 不互相污染
```
无 SF2 状态下不创建 BASSMIDI 实例，不能播放、预览或音频渲染。
随机位置播放 / 预渲染时需要解决：
```text
如何确定当前位置所需 Port 数
如何恢复 Channel Unit 状态
如何与编译缓存和局部重编译结果保持一致
```
这些问题由 第 13 章《播放与预览》 和实现设计阶段细化。
---
## 5.11 与 MIDI 导出的关系
MIDI 导出必须能够表达编译后的 Port / Channel 结果。
导出系统应遵守：
```text
UI 显示编号从 1 开始
MIDI 内部 Channel 编码使用 0–15
Logical/Event Instrument 输出中，每个实际有事件的 Channel Unit 严格对应一个 MIDI 事件 Track
Pure MIDI 输出中，每个 Pure MIDI Track 严格对应一个单 Channel MIDI 事件 Track；同 Root 的多个 Track 可以共享一个 Port.Channel
每个 MIDI 事件 Track 仍严格只包含一个 Port.Channel 的 Channel Event
Program Change 等数据值按 MIDI 标准编码
Logical Channel 10 与 Melodic Pure MIDI Root 的 Normal Part 初始化写入导出结果；Percussion Root 不写
按 Port 导出时，每个实际使用 Port 输出为独立 MIDI 文件
按 Logical Track 导出和整曲导出时，通过 Unit Track Name 与 Port Meta 保留 Port / Channel 语义
```
如果目标播放器不支持或忽略 Port 相关 Meta Event，可能无法正确复现多 Port 语义。该问题由 第 14 章《MIDI 导出》 的导出警告和 Readme 规则细化。
---
## 5.12 规则、限制与失败条件
### 5.12.1 强制规则
1. Midora 初版最多支持 16 Ports。
2. 每个 Port 固定包含 16 Channels。
3. 最大 Channel Unit 数量为 256。
4. Channel Unit = Port + Channel。
5. Port 按需动态使用，不固定 16 Port 常驻。
6. 用户不负责创建或删除 Port。
7. 用户不能手动指定 Event Instrument / SubVoice 固定使用某个 Port 或 Channel。
8. 用户不能禁用 Port 或 Channel Unit。
9. 所有最多 256 个 Channel Units 都属于编译器潜在可用资源池。
10. UI 显示 Port / Channel 编号从 1 开始。
11. MIDI 内部 Channel 编码按标准使用 0–15。
12. Logical/Event Instrument 分配到 Channel 10 时强制作为普通旋律通道；Pure MIDI Root 按自身 Melodic/Percussion Mode 使用 Channel 10。
13. Channel 10 作为普通可寻址 Channel Unit 参与 Root 预留、Auto Root 或 Logical 分配。
14. 初版 Note On / Note Off 非 Channel-Wide，其余 Channel 状态类事件默认 Channel-Wide。
15. 初版不允许用户编辑事件作用域表。
16. 初版每条 SubVoice 在一次 Event Instrument Instance 中占用一个 Channel Unit。
17. 初版不允许多个 SubVoice 共享一个 Channel Unit。
18. 初版不允许 Note-only SubVoice 自动共享 Channel Unit。
19. Channel Group 分配必须原子成功或整体失败。
20. Channel Group 始终允许跨 Port 分配。
21. 开启音符实例隔离时，每个逻辑音符生成独立 Event Instrument Instance，并独占一组 Channel Group。
22. 关闭音符实例隔离时，Channel Group 共享范围固定为同一 Event Instrument Usage 的活动连通区间；可跨该 Usage 的多条 Logical Track。
23. 非重叠实例在旧实例结束后可以复用同一 Segment-owned Channel Unit lane；新实例起点先执行目标闭包的 lane 激活 Reset/Initial State。
24. Channel-Wide 状态污染按生命周期重叠、lane 激活初始化和硬边界最终 Reset 共同判断。
25. 单个 Event Instrument Instance 所需 Channel Unit 超过 256 时编译失败。
26. 实际所需 Port 数超过 16 时编译失败。
27. Channel Unit 不足时编译失败。
28. 不支持 Voice Steal。
29. 资源使用量按整曲任一 tick 的峰值同时占用 Channel Unit 数计算。
30. Channel Unit 使用量大于等于 248 时产生资源使用量 Info；该 Info 不受“Warning 视为 Error”策略影响。
31. 无 SF2 不影响 MIDI 编译和资源分配语义。
32. 无 SF2 只影响播放、预览和音频渲染。
33. 无内容 Logical Track 可以不指定 Usage且不分配资源；含内容 Logical Track 必须引用唯一有效 Usage/Definition。Damaged Definition/Usage/Track Placeholder 不产生 Event Instrument Instance 或 Channel Unit。
34. Conductor Track 不参与 Channel Unit 分配。
35. 每个 Pure MIDI Track 必须属于一个 MIDI Channel Root；一个 Root 对应一个 Unit，一个 Unit 不得属于两个 Root。
36. 纳入 CompileContext 的非空 Fixed Root 先占用精确 Unit；Fixed 冲突为 Error；含参与 Segment 内容的 Auto Root 后按最早成员 global Track order 低号分配。
37. Root/Usage 均不得为空；所有已分配 Root Unit 在本次 CompileContext 内不得与 Logical Usage/instance 时间复用。
38. Root Units 数量与 Logical/Event Instrument 峰值占用之和不得超过 256。
### 5.12.2 Info 情况
以下情况应产生资源系统相关 Info：
```text
整曲任一 tick 的峰值同时占用 Channel Unit 数 >= 248
资源使用量接近系统上限，可能很容易因后续编辑导致编译失败
```
该诊断保持为 Info；文字、定位和 UI 呈现由第 12、15、17～20 章规定。
### 5.12.3 失败条件
以下情况应导致编译失败或相应操作失败：
| 场景 | 结果 |
|---|---|
| 某一时间范围内所需 Channel Unit 超过可用资源 | 编译失败 |
| 实际所需 Port 数超过 16 | 编译失败 |
| 单个 Event Instrument Instance 的 SubVoice 数超过 256 | 编译失败 |
| Channel Group 无法完整原子分配 | 编译失败 |
| 重叠共享会导致 Channel-Wide 状态污染，且无安全替代分配 | 编译失败 |
| 用户尝试手动固定 Event Instrument / SubVoice 到某个 Port / Channel | 阻止操作 |
| 用户尝试禁用 Port 或 Channel Unit | 阻止操作 |
| Event Instrument/SubVoice 尝试把获配 Channel 10 改为 drum mode | 阻止操作或项目规则错误 |
| Pure MIDI Root 使用合法 Percussion Mode | 允许；按第 23 章编译、播放和导出 |
| 两个 Fixed MIDI Channel Root 指向同一 Port.Channel | 编译失败 |
| Root 预留/分配数加 Logical 峰值超过 256 | 编译失败 |
| 用户尝试通过 Event Instrument 自由 SysEx 绕过其 melodic 规则 | 初版不支持该编辑入口，应阻止 |
---
