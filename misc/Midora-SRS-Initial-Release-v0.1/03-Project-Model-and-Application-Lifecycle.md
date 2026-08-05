# 第 3 章 Project 模型与应用生命周期

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 Project 的组成、身份、单实例应用边界、默认状态、元数据、保存与打开入口、修改状态、撤销/重做以及与编译和输出系统的总体关系。文件包内部事务由第 16 章进一步规定。

## 3.1 Project 的定义
Midora Project 是用户在 Midora 中进行完整工作的最高层单位。
一个 Project 不是普通文件夹，也不是单个乐器定义，而是包含项目级设置、事件乐器库、逻辑轨道、片段、全局音乐事件、Reset 默认值、播放设置、MIDI 导出设置、音频渲染设置和 SoundFont 设置的完整上下文。
编译、播放、预览、渲染和 MIDI 导出均以 Project 为上下文。
单独的 Event Instrument、Logical Track 或 Segment 不构成完整项目上下文。
---
## 3.2 Project 的必要组成
每个有效 Midora Project 必须包含以下顶层对象或集合：
| 顶层对象 / 集合 | 是否必须存在 | 是否可删除 | 说明 |
|---|---:|---:|---|
| Project Settings | 是 | 否 | 项目级设置入口 |
| Project Metadata | 是 | 否 | 项目元数据 |
| Conductor Track | 是 | 否 | 固定全局音乐事件轨道 |
| Event Instrument Library | 是 | 否 | 当前项目内事件乐器集合 |
| Logical Tracks | 是 | 否 | 当前项目内逻辑轨道集合，可为空 |
| Global Reset Defaults | 是 | 否 | 项目级 Reset 默认值 |
| Global Event Scope Defaults | 是 | 否 | 项目级事件作用域默认规则 |
| Export Settings | 是 | 否 | MIDI 导出默认设置 |
| Playback Settings | 是 | 否 | 播放相关默认设置 |
| Audio Render Settings | 是 | 否 | 音频文件渲染默认模式、范围、Track 选择、采样率与格式设置 |
| SoundFont Settings | 是 | 否 | 初版项目级单一 SF2 设置，可为空状态 |
说明：
- 顶层对象必须存在，不代表其中必须已有用户内容。
- Event Instrument Library 可以为空。
- Logical Tracks 集合可以为空。
- Audio Render Settings 必须存在，初版默认使用 Whole Mix、Project Default Range、All Valid Logical Tracks，以及普通 RIFF/WAVE / 48 kHz / Stereo / IEEE 32-bit Float；采样率可由用户在合法范围内修改。
- SoundFont Settings 必须存在，但可以处于“未选择 SF2”状态。
- Conductor Track 必须存在，且创建新项目时至少包含默认 Tempo 与默认拍号。
---
## 3.3 单项目 / 单实例规则
初版 Midora 只允许同时打开一个 Project。
初版不支持：
```text
多项目同时打开
单窗口多项目标签页
多窗口分别打开不同项目
跨项目实时引用
跨项目资源锁定
跨项目播放上下文
多个可独立启动并打开 Project 的 Midora 应用实例同时运行
```
整个系统只允许一个用户可启动、可显示 UI、可打开 Project 的 Midora 应用实例。
当用户第二次启动 Midora 时，不应创建新的独立进程实例，而应将启动请求转发给已有进程。具体转发机制、文件参数处理和操作系统级互斥实现属于实现设计细则。

允许主应用实例启动一个内部音频后端子进程。该子进程：
```text
不构成第二个 Midora 应用实例
不能独立打开或持有 Project 源数据
不提供用户界面
只能接收主应用提供的正式编译结果、音频配置和控制命令
```
初版正式音频后端固定使用内部音频子进程；主应用不得保留另一套进程内正式消费路径，也不向用户提供拓扑切换设置。子进程按第 13.30 节独立 Native AOT 发布，具体 CPU RID 由发布架构决定。
用户在已有 Project 打开的情况下打开另一个项目时，必须先处理当前项目：
```text
保存
不保存
取消打开操作
```
---
## 3.4 新项目默认状态
创建新 Project 时，系统必须生成结构完整的项目。
新项目默认包含：
```text
Project Settings
Project Metadata
Conductor Track
空 Event Instrument Library
空 Logical Tracks 集合
Global Reset Defaults
Global Event Scope Defaults
Export Settings
Playback Settings
Audio Render Settings
SoundFont Settings 空状态
```
### 3.4.1 Conductor Track 默认内容
新项目必须有一个 Conductor Track。
Conductor Track 默认包含：
```text
Tempo = 120 BPM
Time Signature = 4/4
```
Conductor Track 应允许用户自由插入 Tempo、Time Signature 等全局音乐事件，以实现变速和变拍。
Conductor Track 具体事件编辑规则由 第 4 章《时间、Conductor Track 与全局音乐事件》 细化。
### 3.4.2 Event Instrument Library 默认状态
新项目创建一个空 Event Instrument Library。
初版不创建默认 Event Instrument。
### 3.4.3 Logical Tracks 默认状态
新项目默认无 Logical Track。
用户需要主动创建 Logical Track。
初版不创建默认 Track，也不创建默认 Segment。
### 3.4.4 SoundFont 默认状态
创建项目时可以不选择 SF2。
无 SF2 时：
```text
不创建任何 BASSMIDI 实例
不能播放
不能预览
不能音频渲染
状态栏用文字提示当前无 SF2
```
无 SF2 不影响 MIDI 导出。
MIDI 导出输出的是 MIDI 事件数据，不依赖 SF2 加载状态。无 SF2 时允许 MIDI 导出，且不需要因为无 SF2 额外弹出导出前警告。
---
## 3.5 空项目规则
空项目不作为特殊项目类型处理。
空项目只是普通 Project 的一种内容状态。
初版中，空项目允许：
```text
保存
编译
```
空项目编译应产生结构上有效的编译结果。
具体编译结果是否仅包含 Conductor Track、是否插入全局初始化事件、是否生成空事件流，由 第 12 章《编译系统与 Canonical Compiled Result》 和输出章节细化。
空项目在无 SF2 状态下仍不能播放、预览或音频渲染。
---
## 3.6 Project Metadata 与系统级文件信息
### 3.6.1 用户 Project Metadata
初版用户 Project Metadata 应包含以下内容：
| 元数据 | 初版要求 | 编辑性 |
|---|---|---|
| 项目名称 | 必须支持 | 用户可编辑 |
| 项目版本 | 必须支持 | 用户可编辑，自由文本 |
| 项目作者或团队 | 必须支持 | 用户可编辑 |
| Remix 原曲 / 原曲作者或团队 | 必须支持 | 用户可编辑，可为空 |
| 版权信息 | 必须支持 | 用户可编辑 |
| 备注 | 必须支持 | 用户可编辑 |
| 创建时间 | 必须支持 | 只读，一次性，在创建项目瞬间写入 |
| 修改时间 | 必须支持 | 只读，普通保存或保存副本写出时按对应事务规则更新输出文件 |
| 工程总耗时 | 必须支持 | 只读，初版按项目打开累计时间自动累计 |
### 3.6.2 manifest 系统级文件信息
Midora 文件仍必须记录：
```text
createdWithSoftwareVersion
lastSavedWithSoftwareVersion
```
但这两个字段属于 `.midora` `manifest.json` 中的系统级文件信息，不属于用户可编辑 Project Metadata，也不保存于 `metadata.json`。
UI 可以在 Project Settings 的只读 `File Information` 区显示这些值。
### 3.6.3 名称区分
以下概念必须严格区分：
```text
Project Version
    用户可编辑自由文本，用于作品或工程版本说明。
File Format Version
    系统控制的持久化兼容版本，只读。
Created With / Last Saved With Midora
    manifest 系统信息，只读。
```
说明：
- 项目格式版本与用户可见的“项目版本”不是同一概念。
- 项目格式版本用于软件兼容性与迁移，应由系统管理。
- 用户可见的项目版本用于作品或工程版本记录。
- 工程总耗时的累计规则、暂停规则、是否统计空闲时间，由实现设计确定细化。
---
## 3.7 项目内对象名称与 ID 规则
### 3.7.1 内部稳定 ID
项目内所有需要被引用的对象都必须拥有内部稳定 ID。
内部引用必须基于稳定 ID，不得依赖用户可见名称。
具体 ID 格式、生成方式、迁移策略属于实现设计实现细则。
### 3.7.2 Event Instrument 名称
Event Instrument 名称在当前 Project 内必须唯一。
该限制的目的不是内部引用，而是迫使用户形成清晰命名习惯，降低项目维护成本。
内部仍然必须使用稳定 ID 维护对象身份。
### 3.7.3 Logical Track 名称
Logical Track 名称允许重复。
创建 Logical Track 时，如果用户未指定名称，默认使用当前指定的 Event Instrument 名称。
允许出现多个同名 Logical Track。
例如：
```text
Beautiful Pad
Beautiful Pad
```
这两个 Logical Track 可以引用同一个 Event Instrument，但它们的运行状态互不影响。
### 3.7.4 Segment 名称
Segment 不持有名称。
Segment 是 Logical Track 上一段可以放置音符和事件的有效时间范围。
在 Logical Track / Segment 层，初版“事件”主要指 Logical Parameter Lane / Point / Curve，而不是直接裸 MIDI CC / Pitch Bend / RPN / NRPN。
Segment 内的 Logical Parameter 数据属于 Project 内容。保存、复制、移动、分割、连接 Segment 时，Logical Parameter Lane / Point / Curve 应随 Segment 保留。
Segment 不是 FL Studio 中 Pattern 的等价概念。
Segment 直接存在于 Logical Track 的时间线上。
Segment 可以被用户进行以下操作：
```text
跨轨道复制
移动
向前扩张
向后扩张
缩短
分割成两段
两个相邻 Segment 连接成一段
```
Segment 的关键语义是：
```text
每个 Segment 末尾都会将该 Segment 使用过的事件全部 Reset
```
Segment 的详细编辑和裁剪规则由 `第 11 章《Logical Track、Segment 与编曲语义》` 细化。
### 3.7.5 Envelope Preset 名称
Envelope Preset 名称可选，且允许重复。
Envelope Preset 的作用范围在 Event Instrument 内部。
用户通常不必为每个 Envelope Preset 命名。
### 3.7.6 Mapping Function 名称
Mapping Function 名称必填。
Mapping Function 名称在单个 Event Instrument 内不可重复。
Mapping Function 的具体内容、映射目标、编辑方式和编译行为由 第 9 章《曲线、Logical Parameter 与映射》 继续细化。
### 3.7.7 Export Preset
初版不做 Export Preset。
初版只需要保存当前项目的 Export Settings。
如果未来导出系统需要支持多个导出配置方案，再在 第 14 章《MIDI 导出》 或实现设计阶段补充 Export Preset。
---
## 3.8 Logical Track 的事件乐器绑定状态
Logical Track 可以处于以下事件乐器绑定状态之一：
```text
已指定 Event Instrument
未指定 Event Instrument
```
因此，Logical Track 应支持以下操作：
```text
指定 Event Instrument
取消指定 Event Instrument
替换 Event Instrument
```
### 3.8.1 删除被引用 Event Instrument 时的行为
当某个 Event Instrument 被删除，且存在 Logical Track 正在引用它时：
```text
引用该 Event Instrument 的 Logical Track 自动变为未指定 Event Instrument 状态
```
该 Logical Track 应保留最近一次绑定的 Event Instrument 名称，仅用于 UI 提示，不参与编译。
具体显示位置和显示方式推迟到 UI 章节细化。
项目仍然可以保存。
项目仍然可以编译。
该 Logical Track 上已有的音符、Segment 和事件数据仍然保留在项目文件中。
### 3.8.2 未指定 Event Instrument 的编译行为
如果 Logical Track 未指定 Event Instrument，则编译时忽略该 Logical Track 的音符和事件。
播放、预览和渲染时亦如此。
该 Logical Track 在项目数据中仍然存在。
如果未指定 Event Instrument 的 Logical Track 包含音符或事件，应只在编译诊断中列为信息，不算警告，不导致编译失败。
---
## 3.9 项目保存、打开与关闭
### 3.9.1 保存
Project 必须支持普通保存。
普通保存应覆盖当前 `.midora` Project 的完整已提交状态，包括：
```text
Project Settings
Project Metadata
Conductor Track
Event Instrument Library
Event Instrument definitions
Logical Tracks
Segments
Global Reset Defaults
Global Event Scope Defaults
Playback Settings
Export Settings
Audio Render Settings
SoundFont Settings
已 Apply 的 C# Mapping Function 源码
```
未 Apply 的 Function Draft 不属于 Project 已提交状态，不进入普通保存。
具体安全写出事务、覆盖、备份、临时文件、自校验和路径规则由 第 16 章《.midora 文件格式与持久化》 细化。
### 3.9.2 保存副本 / Save Copy
初版不提供传统 Save As。
Save Copy 把当前内存 Project 快照写出为新的 `.midora` 文件，但不改变：
```text
当前 Project 的工作路径
当前 Project 的 Modified 状态
当前 Project 的 Undo / Redo History
当前内存中的 metadata 修改时间
当前内存中的 lastSavedWithSoftwareVersion
```
Save Copy 成功不等于当前 Project 已保存。
如果当前 Project 在 Save Copy 前处于 Modified，Save Copy 后仍然处于 Modified。
Save Copy 不是创建新 Project。副本文件：
```text
保留原 Project 创建时间
保留 createdWithSoftwareVersion
使用当前内存 Project 的工程累计时间快照
把 Save Copy 完成时间写为副本 metadata 修改时间
把当前 Midora 版本写为副本 manifest 的 lastSavedWithSoftwareVersion
```
Save Copy 不更新当前打开 Project 的对应内存字段。
具体目标覆盖、安全替换、资源路径和事务规则由 第 16 章《.midora 文件格式与持久化》 细化。
### 3.9.3 打开
打开 `.midora` Project 时，系统应进行系统级检查：
```text
文件是否可读取
文件是否可识别为 Midora Project
文件格式版本是否可被当前软件版本处理
必要顶层对象是否存在
关键对象引用关系是否可解析
外部资源引用是否可用
Project 是否存在兼容性、迁移或损坏问题
```
初版不提供扫描式 Project Repair Mode，也不猜测或重建未知音乐语义。
### 3.9.4 关闭
关闭 Project 时，如果存在未保存修改，应提示用户处理：
```text
保存
不保存
取消关闭
```
Save Copy 不改变当前 Project 的保存状态，因此不能替代关闭流程中的普通保存。
### 3.9.5 损坏 Project 的分级处理
当 `manifest.json`、`project.json`、schema、文件 kind 或 Project 索引发生无法恢复的结构性不一致，无法建立可信 Project Object Graph 时：
```text
候选 Project 打开失败
```
初版不提供：
```text
扫描式修复
按名称猜测引用
只读修复模式
自动重建未知音乐语义
部分恢复导出
```
当 第 16 章《.midora 文件格式与持久化》 明确允许隔离的单个 Event Instrument 或 Logical Track 对象损坏时，Project 可以打开，并为该对象创建 `Damaged Placeholder`。
Damaged Placeholder 应保留：
```text
稳定 ID
名称快照
对象类型
Project 排序位置
来源文件路径
损坏或读取失败信息
```
Damaged Placeholder：
```text
不参与编译
不能进入普通编辑器
可以由用户明确删除
不等于已修复对象
```
只要 Project 中仍存在任何 Damaged Placeholder：
```text
Save Project 禁止
Save Copy 禁止
```
用户必须先删除全部 Damaged Placeholder，或放弃当前 Project 会话。
该行为属于有界降级打开，不等于 Project Repair Mode。
### 3.9.6 缺失与损坏文件的分级规则
初版至少遵循以下边界：
#### 3.9.6.1 metadata.json 缺失
```text
Project 可打开
Metadata 使用空默认值
产生 Error Diagnostic
Project 标记为 Modified
```
#### 3.9.6.2 metadata.json 存在但损坏、hash 不匹配或无法解析
```text
Project 打开失败
```
#### 3.9.6.3 普通 Settings 缺失或损坏
除 Audio Render Settings 的严格例外外：
```text
Project 可打开
对应 Settings 使用当前软件默认值
产生 Error Diagnostic
Project 标记为 Modified
普通保存时写出完整 Settings
```
#### 3.9.6.4 当前文件格式强制要求的 Audio Render Settings 缺失或损坏
```text
不允许普通 fallback
Project 打开失败
```
只有存在明确旧版本迁移路径时，才允许为旧格式补充默认 Audio Render Settings。
#### 3.9.6.5 Conductor Track 缺失或损坏
```text
Project 可打开
Conductor Track 回退为 tick 0 Tempo 120 BPM 与 Time Signature 4/4
产生 Error Diagnostic
Project 标记为 Modified
```
更精确的 manifest、hash、schema、版本迁移和对象文件规则由 第 16 章《.midora 文件格式与持久化》 定义。
### 3.9.7 未知与孤立包文件
Zip 包中存在但未被 `project.json` 纳入当前 Project 语义的未知或孤立文件：
```text
不形成 Project 对象
不创建 Damaged Placeholder
可以被忽略并产生 Information Diagnostic
本身不阻止 Save Project 或 Save Copy
```
再次保存时，系统从当前内存 Project 重新生成完整包，因此这些未知或孤立文件不会被保留。
初版不提供“保留未知包文件”选项。
---
## 3.10 项目修改状态
Project 应维护未保存修改状态。
以下行为应使项目进入已修改状态：
```text
修改 Project Settings
修改 Project Metadata 中的用户可编辑项
修改 Conductor Track
新建、删除、重命名、编辑 Event Instrument
修改 Event Instrument Library
新建、删除、重命名、编辑 Logical Track
指定、取消指定、替换 Logical Track 的 Event Instrument
新建、删除、移动、缩放、分割、连接、编辑 Segment
修改 Global Reset Defaults
修改 Global Event Scope Defaults
修改 SoundFont Settings
修改 Playback Settings
修改 Export Settings
修改 Audio Render Settings 中持久化的默认值
修改任何影响编译、播放、预览、渲染或导出结果的项目内容
```
以下行为不应使项目进入已修改状态：
```text
改变当前播放位置
打开或关闭临时面板
临时缩放时间轴视图
临时选择对象
临时试听
临时显示错误面板筛选条件
执行 Save Copy
```
初版不保存 UI 视图状态，因此 UI 视图状态变化不应标记项目已修改。
---
## 3.11 UI 视图状态
初版不将 UI 视图状态保存进 Project。
不保存的 UI 状态包括但不限于：
```text
当前打开的编辑器
时间轴缩放
选中对象
面板宽度
当前播放位置
错误面板筛选条件
滚动位置
临时展开 / 折叠状态
```
这些状态不得影响编译、播放、预览、渲染或导出结果。
---
## 3.12 项目级撤销 / 重做
初版应提供完整的、全项目统一的撤销 / 重做框架。
要求：
```text
撤销 / 重做不应只局限于某个局部编辑器
每新增一个功能，都必须评估是否涉及撤销 / 重做
如果该功能属于可撤销编辑行为，应在实现该功能时同步纳入撤销 / 重做系统
```
本章只规定系统级要求，不定义具体命令栈结构、事务模型、合并规则或内存策略。
撤销 / 重做的具体 UI 行为由第 17～20 章规定。
撤销 / 重做的数据结构与实现规则由实现设计工程细则细化。
---
## 3.13 自动保存与崩溃恢复
初版不做自动保存。
初版不做崩溃恢复系统。
初版应优先保证：
```text
项目数据结构科学性
项目文件结构科学性
项目文件大小控制
运行时内存性能
序列化稳定性
反序列化稳定性
```
其中：
```text
项目文件结构优先考虑文件大小
项目数据结构优先考虑运行时内存性能
```
自动保存、临时备份、崩溃恢复、恢复提示和扫描式修复模式均不属于初版；未来版本如需支持，必须另行定义。
---
## 3.14 项目模板与跨项目导入
初版不提供项目模板。
初版不支持：
```text
内置项目模板
用户自定义项目模板
从已有项目保存为模板
模板库
```
初版不做跨项目导入 / 导出 Event Instrument。
初版不支持：
```text
从另一个 .midora 项目复制 Event Instrument
单独导出 Event Instrument 文件
单独导入 Event Instrument 文件
跨项目复制粘贴 Event Instrument
程序级全局 Event Instrument Library
```
未来可以扩展项目模板、事件乐器导入导出或项目间复制功能，但初版不得依赖这些功能成立。
---
## 3.15 项目与编译的关系
Project 是编译器的完整输入上下文。
编译器不应只依赖某个孤立对象完成完整编译。
初版中，空项目允许编译。
未指定 Event Instrument 的 Logical Track 在编译中被忽略。
编译至少需要读取：
```text
Project Settings
Project Metadata 中可能影响导出的信息
Conductor Track
Event Instrument Library
Logical Tracks
Segments
Global Reset Defaults
Global Event Scope Defaults
Event Instrument definitions
Mapping functions
Lifecycle policies
Overlap policies
Reset policies
Export Settings、Playback Settings 或 Audio Render Settings
SoundFont Settings 状态
```
具体编译输入输出由第 12 章《编译系统与 Canonical Compiled Result》规定。
---
## 3.16 项目与播放、预览、音频渲染的关系
播放、预览和音频渲染均以当前 Project 为上下文。
无 SF2 状态下：
```text
不创建 BASSMIDI 实例
不能播放
不能预览
不能音频渲染
状态栏显示无 SF2 提示
```
播放和预览至少依赖：
```text
SoundFont Settings
Playback Settings
Conductor Track
canonical compiled result
Port / Channel / Channel Unit 规则
Channel 10 melodic 初始化规则
C# 映射运行结果
```
音频文件渲染还必须依赖固定存在的：
```text
Audio Render Settings
```
其 Project 级默认设置至少包括：
```text
Whole Mix / Per Logical Track 默认模式
Project Default Range / Manual Range 默认范围策略
All Valid Logical Tracks / Explicit Logical Track IDs 默认选择策略
普通 RIFF/WAVE / Stereo / IEEE 32-bit Float 固定格式字段
默认文件采样率，合法范围 8,000–192,000 Hz
有限命名偏好
```
单纯执行、成功、失败或取消音频渲染不修改 Project。
只有用户明确保存 Audio Render Settings 默认值时，Project 才进入已修改状态并进入 Undo / Redo。
音频渲染允许使用当前尚未保存到磁盘的内存 Project，不要求先保存，也不自动保存。
具体实时播放和预览行为由第 13 章《播放与预览》规定。
具体音频文件渲染行为由第 15 章《音频文件渲染》规定。
---
## 3.17 项目与 MIDI 导出的关系
MIDI 导出系统以当前 Project 为导出上下文。
导出至少依赖：
```text
Conductor Track
Logical Tracks
Event Instrument Library
Export Settings
Reset 规则
Port / Channel 分配结果
Channel 10 melodic 初始化规则
SoundFont Settings 中用于 Readme 的信息
```
无 SF2 状态下允许 MIDI 导出，且不需要因为无 SF2 额外显示导出前警告。
原因是 MIDI 导出与 SF2 加载状态没有直接依赖关系。
如果导出 Readme 需要记录推荐 SoundFont，而项目未设置 SF2，则该信息可以为空或标记为未指定。
具体导出模式和 Readme 内容由第 14 章《MIDI 导出》规定。
---
## 3.18 规则、限制与失败条件
### 3.18.1 强制规则
1. 初版只允许同时打开一个 Project。
2. 初版只允许一个可独立启动、显示 UI 和打开 Project 的 Midora 应用实例；第二次启动应转发到已有实例。允许一个不提供 UI、不能独立打开 Project 的内部音频后端子进程。
3. 每个 Project 必须有且只有一个 Conductor Track。
4. Conductor Track 不可删除。
5. 新 Project 的 Conductor Track 默认 Tempo 为 120 BPM，默认拍号为 4/4。
6. 每个 Project 必须有且只有一个 Event Instrument Library。
7. 新 Project 默认 Event Instrument Library 为空。
8. 新 Project 默认无 Logical Track。
9. 初版一个 Project 只使用一个 SF2。
10. 创建项目时可以不选择 SF2。
11. 无 SF2 状态下不创建 BASSMIDI 实例。
12. 无 SF2 状态下不能播放、预览或音频渲染。
13. 无 SF2 状态下允许 MIDI 导出，不需要额外导出前警告。
14. 初版不提供项目模板。
15. 初版不做跨项目导入 / 导出 Event Instrument。
16. 初版不保存 UI 视图状态。
17. 初版不做自动保存。
18. 初版不做崩溃恢复。
19. 初版不提供扫描式 Project Repair Mode；无法建立可信 Project Object Graph 的结构性损坏必须打开失败，但 第 16 章《.midora 文件格式与持久化》 明确允许隔离的单个 Event Instrument / Logical Track 损坏可形成 Damaged Placeholder。
20. Event Instrument 名称在当前 Project 内必须唯一。
21. Logical Track 名称允许重复。
22. Segment 不持有名称。
23. Mapping Function 名称必填，且在单个 Event Instrument 内不可重复。
24. 初版不做 Export Preset。
25. 每个 Project 必须有且只有一个 Audio Render Settings，且不可删除。
26. 新 Project 默认音频渲染模式为 Whole Mix、默认范围为 Project Default Range、默认 Track 选择为 All Valid Logical Tracks。
27. 初版音频文件输出普通 RIFF/WAVE / Stereo / Interleaved IEEE 32-bit Float；采样率为用户选择的 8,000–192,000 Hz 整数，新 Project 默认 48,000 Hz。
28. Track Mute / Solo 不影响音频文件渲染成品。
29. 音频渲染产物、缓存和任务状态不属于 Project 源数据。
30. 内部引用必须基于稳定 ID，不得依赖名称。
31. 删除被引用 Event Instrument 时，相关 Logical Track 自动变为未指定 Event Instrument，并保留最近一次绑定的 Event Instrument 名称作为 UI 提示信息。
32. 未指定 Event Instrument 的 Logical Track 在编译、播放、预览、渲染中被忽略
未指定 Event Instrument 的 Logical Track 中已有 Logical Parameter Lane 数据保留，但显示为断裂 / 不适用，不参与编译，但项目数据保留。
33. 未指定 Event Instrument 且包含音符或事件的 Logical Track，只在编译诊断中列为信息，不算警告。
34. 初版应提供完整的全项目统一撤销 / 重做框架。
35. 初版不提供传统 Save As，只提供普通保存与 Save Copy。
36. Save Copy 不改变当前 Project 路径、Modified 状态或 Undo / Redo History。
37. 只要 Project 中存在 Damaged Placeholder，普通保存和 Save Copy 均必须禁止。
38. 未知或孤立包文件不形成 Project 对象，不创建 Damaged Placeholder，也不阻止保存；再次保存时不予保留。
### 3.18.2 警告情况
以下情况应产生项目级或跨系统警告，具体严重程度由统一诊断规则确定：
1. 当前 SF2 缺失或无法访问，且用户尝试播放、预览或音频渲染；
2. 项目格式版本较旧，需要迁移；
3. 项目中存在未来版本功能标记；
4. 项目中存在不会影响打开但会影响编译、播放、预览、渲染或导出的缺失资源；
`Channel Unit` 峰值达到 248 不属于 Warning；它按第 12.15.3 的专门规则产生 `Info`，不得因“Warning 视为 Error”而阻止编译。
### 3.18.3 失败条件
以下情况应导致相应操作失败：
| 场景 | 失败结果 |
|---|---|
| 文件无法识别为 Midora Project | 打开失败 |
| manifest、project.json、schema、文件 kind 或 Project 索引存在无法恢复的结构性不一致 | 打开失败 |
| metadata.json 存在但损坏、hash 不匹配或无法解析 | 打开失败 |
| 当前文件格式强制要求的 Audio Render Settings 缺失或损坏，且不存在明确旧版本迁移路径 | 打开失败 |
| 存在 Damaged Placeholder 时尝试普通保存或 Save Copy | 阻止保存 |
| 保存目标不可写 | 保存失败 |
| 无 SF2 时尝试播放 | 播放失败或阻止播放 |
| 无 SF2 时尝试预览 | 预览失败或阻止预览 |
| 无 SF2 时尝试音频渲染 | 渲染失败或阻止渲染 |
| 项目内部引用断裂且影响编译 | 编译失败 |
| C# 映射源码缺失或无法编译 | 编译失败 |
| 项目配置违反本规格强制边界 | 应阻止保存、阻止编译或标记为错误，具体场景由实现设计确定 |
---
