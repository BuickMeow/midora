# 第 16 章 .midora 文件格式与持久化

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 `.midora` Zip package、JSON/protobuf 分工、manifest/project/object 索引、严格 schema、资源保存、损坏隔离、打开与保存事务、Save Copy、迁移和确定性写出。

## 16.1 文件格式核心原则
### 16.1.1 Zip package
`.midora` 初版本质上是 Zip package。
用户可以将 `.midora` 改名为 `.zip` 后解包检查。这属于诊断便利，不表示 Midora 正式支持用户手工编辑包内容。
手工修改后的项目必须仍通过严格格式校验，否则可能无法打开。
Midora 打开文件时不只依赖后缀名：
```text
只要 Zip 包内 magic 正确，即使文件后缀为 .zip，也允许作为 Midora Project 打开。
```
新建项目首次保存默认后缀为：
```text
.midora
```
保存副本默认后缀为：
```text
.midora
```
普通保存使用当前项目路径，不强行修改后缀。
### 16.1.2 不支持手工扩展包内容
初版不支持用户手工往 `.midora` 包内添加自定义内容。
规则：
```text
用户手工添加的文件不属于支持用法。
当前软件不认识的文件不参与 Project 语义。
未被 project.json 纳入 Project 语义索引的文件不参与 Project。
保存时从内存 Project 重建项目包，因此这些文件不会被写入新包。
```
### 16.1.3 保存时从内存重建完整包
普通保存和保存副本均遵循：
```text
从当前内存 Project 重新序列化生成完整 .midora Zip package。
不回读原 .midora 包。
不复制原包中的旧对象文件。
不复制原包中的未知文件。
不保留原包中的多余文件。
```
保存成功后的新包只包含当前版本明确写出的 Project 语义文件与资源文件。
### 16.1.4 不保存派生结果
`.midora` 不保存以下派生结果：
```text
canonical compiled result
编译缓存
编译器 cache fingerprint
MIDI 导出产物
音频渲染产物
音频渲染任务状态 / 进度 / 历史
音频渲染最终样本和中间样本缓存
播放预渲染 buffer
BASSMIDI Stream 状态
C# Mapping Function 编译产物 / DLL / 编译缓存
诊断结果
Undo / Redo 栈
```
---
## 16.2 包内固定结构
初版 `.midora` 包内固定使用以下结构：
```text
.midora  // Zip package
├─ manifest.json
├─ project.json
├─ metadata.json
├─ conductor-track.json
├─ settings/
│  ├─ project-settings.json
│  ├─ export-settings.json
│  ├─ playback-settings.json
│  ├─ audio-render-settings.json
│  ├─ soundfont-settings.json
│  ├─ global-reset-defaults.json
│  └─ global-event-scope-defaults.json
├─ event-instruments/
│  ├─ ei_<id>.pb
│  └─ ...
├─ logical-tracks/
│  ├─ lt_<id>.pb
│  └─ ...
└─ resources/
   └─ soundfonts/
      └─ <resourceId>.sf2
```
固定文件名：
```text
manifest.json
project.json
metadata.json
conductor-track.json
settings/project-settings.json
settings/export-settings.json
settings/playback-settings.json
settings/audio-render-settings.json
settings/soundfont-settings.json
settings/global-reset-defaults.json
settings/global-event-scope-defaults.json
```
固定目录名：
```text
settings/
event-instruments/
logical-tracks/
resources/soundfonts/
```
Event Instrument 对象文件路径模板：
```text
event-instruments/ei_<id>.pb
```
Logical Track 对象文件路径模板：
```text
logical-tracks/lt_<id>.pb
```
`.pb` 扩展名固定表示 protobuf 对象文件。
初版允许未来通过新的 `fileFormatVersion` / `schemaVersion` 增加新的顶层目录或结构性文件类型。但旧版软件遇到过新的版本时应拒绝打开，而不是忽略。
---
## 16.3 JSON 与 protobuf 持久化策略
### 16.3.1 JSON 使用范围
以下内容初版使用 JSON：
```text
manifest.json
project.json
metadata.json
conductor-track.json
settings/*.json
```
理由：
```text
这些内容通常较轻。
可读性有利于诊断。
全局入口和设置文件需要更透明。
```
### 16.3.2 protobuf 使用范围
以下内容初版使用 protobuf 二进制文件：
```text
Event Instrument 定义
SubVoice 数据
Event Instrument 内事件与曲线
Logical Parameters
Logical Parameter Mapping
Mapping Function 源码 / 定义入口
Envelope Presets
生命周期 / Overlap / Reset 策略入口
Logical Track
Segment
Logical Note
Logical Parameter Lane / Point / Curve
裁剪窗口
大量事件数据
大量曲线数据
```
protobuf 是初版重数据的默认二进制序列化格式。
初版 protobuf 兼容基线固定为：
```text
protobuf Edition 2024
Google.Protobuf 3.35.1
Grpc.Tools 2.83.0
```
`.proto` 源文件和对应 descriptor SHA-256 基线必须纳入版本控制；生成的 C# 只进入 `obj`，不得作为手工维护源码提交。升级 protobuf runtime、代码生成器或 descriptor 基线前必须完成显式兼容性评审、descriptor diff、golden byte 回归和旧文件重开测试。
如果未来因兼容性、性能或迁移需要替换具体二进制编码，必须通过新的文件格式版本和迁移策略引入。
### 16.3.3 JSON schema 与代码生成
初版结构性 JSON 固定使用 JSON Schema Draft 2020-12。实现使用内部版本化 JSON DTO 和 `System.Text.Json` source generation；不得把领域对象的当前属性布局直接当作文件 schema，也不得依赖运行时反射自动扩张已发布字段面。

每个已发布 JSON schema 必须作为独立版本化文件纳入版本控制，并使用 `additionalProperties: false` 或等价严格约束。JSON 读取器必须拒绝重复属性、未知属性、注释、尾随逗号、UTF-8 BOM 和不符合当前 schema 的值。
### 16.3.4 Conductor Track 未来迁移口
初版 Conductor Track 使用：
```text
conductor-track.json
```
但如果未来 Conductor Track 数据量显著增大，允许通过新的文件格式版本迁移为二进制格式。
初版不得写死为“Conductor Track 永远 JSON”。
---
## 16.4 manifest.json
### 16.4.1 定位
`manifest.json` 是 `.midora` 包入口文件。
它负责：
```text
识别这是 Midora Project package
声明文件格式版本
声明最低可读版本
声明 manifest 自身 schemaVersion
记录创建 / 最新保存软件版本
记录包内文件索引
记录结构性文件 schemaVersion
记录文件 kind
记录文件 SHA-256
记录资源文件完整性信息
支持打开前版本预检
支持文件一致性校验
```
`manifest.json` 不负责：
```text
决定 Event Instrument / Logical Track 是否属于 Project
保存 Event Instrument / Logical Track 用户排序
保存 Event Instrument Library 文件夹分组
保存 Logical Track 集合结构
保存 Project 级 ID 生成状态
保存用户作品元数据
保存具体 Project 语义对象索引
```
这些属于 `project.json` 或其他对应文件。
### 16.4.2 magic
manifest 必须包含固定 magic：
```text
midora-project
```
缺失或不匹配时：
```text
打开失败。
```
magic 已足够表达 package kind，初版不额外设置独立 `packageKind`。
### 16.4.3 版本字段
manifest 顶层至少包含以下语义字段：
```text
fileFormatVersion
minimumReadableVersion
manifestSchemaVersion
createdWithSoftwareVersion
lastSavedWithSoftwareVersion
```
含义：
| 字段 | 含义 |
|---|---|
| `fileFormatVersion` | 当前 `.midora` 包采用的文件格式版本。 |
| `minimumReadableVersion` | 读取该文件所需的最低格式读取能力。 |
| `manifestSchemaVersion` | `manifest.json` 自身结构版本。 |
| `createdWithSoftwareVersion` | 创建该 Project 文件时的 Midora 软件版本。 |
| `lastSavedWithSoftwareVersion` | 最近一次保存该 Project 文件时的 Midora 软件版本。 |
`createdWithSoftwareVersion` 与 `lastSavedWithSoftwareVersion` 用于诊断、迁移提示和用户支持，不替代文件格式兼容判断。
初版中：
```text
minimumReadableVersion 通常可以等于 fileFormatVersion。
```
如果 `minimumReadableVersion` 高于当前软件支持版本：
```text
打开失败。
```
如果 `manifestSchemaVersion` 高于当前软件支持版本：
```text
打开失败。
```
### 16.4.4 文件索引
manifest 必须记录所有当前 Project 包内写出的核心文件、settings 文件、对象文件、资源文件。
每个文件索引至少具有以下系统级信息：
```text
path
file kind
schemaVersion（结构性文件）
SHA-256 hash
```
file kind 至少应能区分：
```text
core-json
settings-json
conductor-json
event-instrument-pb
logical-track-pb
embedded-resource
```
初版不需要定义 `unknown-preserved` / `future-extension` 等保留类型。
如果 manifest 中出现当前软件不认识的 file kind：
```text
该文件不参与 Project。
打开时可忽略并产生 Info。
保存时不会写入新包。
```
### 16.4.5 manifest 不记录的内容
manifest 不记录：
```text
Zip entry 压缩方式
Zip entry 压缩等级
Zip entry 压缩后大小
Zip entry 未压缩大小
Zip entry 时间戳
Zip comment
```
压缩方式、大小等属于 Zip 容器元数据，不以 manifest 为唯一真相。
### 16.4.6 hash
manifest 对以下文件记录 SHA-256：
```text
核心 JSON 文件
settings JSON 文件
conductor-track.json
Event Instrument .pb 文件
Logical Track .pb 文件
内嵌资源文件，例如 SF2
```
SHA-256 计算对象为：
```text
包内文件未压缩原始内容
```
manifest 自身不通过额外 manifest hash 校验。manifest 自身依赖 Zip CRC、JSON 解析、magic、schemaVersion 和字段校验。
保存时必须重新计算所有写出文件的 hash 并写入 manifest。
---
## 16.5 project.json
### 16.5.1 定位
`project.json` 是 Project 语义入口。
它负责保存：
```text
Project 顶层结构关系
Project 级 ID 生成状态
Event Instrument Library 集合索引
Event Instrument 顺序
Event Instrument 文件夹分组
Event Instrument ID 到文件路径映射
Event Instrument 名称快照
Logical Track 集合索引
Logical Track 顺序
Logical Track ID 到文件路径映射
Logical Track 名称快照
settings 文件引用，包括 audio-render-settings.json
metadata 文件引用
conductor-track 文件引用
```
`project.json` 是决定哪些 Event Instrument / Logical Track 属于当前 Project 的唯一权威语义索引。
`project.json` 还必须明确当前 Project 顶层包含 `Audio Render Settings`，并通过固定结构约定或显式引用关联 `settings/audio-render-settings.json`。具体字段形式由最终 JSON Schema 定义。
### 16.5.2 Project ID
初版不引入独立稳定 Project ID。
因此初版不定义：
```text
Project ID
projectFileId
保存副本时 Project ID 是否保留
```
但 Project 内对象仍然必须拥有稳定 ID。
### 16.5.3 Project 级 ID 生成状态
Project 级 ID 生成状态必须保存于：
```text
project.json 的 nextStableId 字段
```
`nextStableId` 使用 16.13.2 规定的 canonical 十进制 JSON integer。
初版采用系统级原则：
```text
持久化单调递增计数器
不补缺
不复用
Project 内所有稳定 ID 全局唯一
```
打开会话内的分配高水位不因 Undo、失败创建或 Redo 分支丢弃而回退；Redo 必须恢复原 ID。仅由已撤销瞬态分配形成的空洞不单独维持 Modified，但在任何后续保存中仍写出当前高水位。关闭 otherwise-clean 会话后，未持久化、无存活对象且无存活 History 引用的瞬态 ID 可以随会话丢弃。
不采用：
```text
打开时扫描最大 ID 后继续
每次打开随机初始化 ID 生成器
删除对象后复用 ID
同类型内唯一但跨类型可重复
```
### 16.5.4 名称快照
`project.json` 保存 Event Instrument / Logical Track 的最小显示名快照。
用途：
```text
索引显示
损坏占位显示
诊断定位
```
真实名称权威来源仍是对象 `.pb` 内部字段。
如果对象正常读取，但对象内部名称与 `project.json` 名称快照不一致：
```text
以对象内部名称为准。
产生一致性警告。
保存时更新 project.json 快照。
```
---
## 16.6 metadata.json
### 16.6.1 定位
`metadata.json` 只保存用户作品元数据。
初版包含：
```text
项目名称
用户可见项目版本
项目作者或团队
Remix 原曲 / 原曲作者或团队
版权信息
备注
创建时间
修改时间
工程总耗时
```
### 16.6.2 不保存内容
`metadata.json` 不保存：
```text
fileFormatVersion
minimumReadableVersion
createdWithSoftwareVersion
lastSavedWithSoftwareVersion
对象索引
软件运行期状态
```
用户可见项目版本与 `fileFormatVersion` 完全无关。
用户可见项目版本是自由文本。
### 16.6.3 保存副本时 metadata 行为
保存副本不是创建新 Project。
因此副本文件中：
```text
创建时间保持原 Project 创建时间。
工程总耗时使用当前内存 Project 快照，不因保存副本额外累计。
修改时间写为保存副本时间。
```
但当前打开的内存 Project：
```text
不因保存副本而更新修改时间。
不因保存副本而更新 lastSavedWithSoftwareVersion。
不因保存副本而改变修改状态。
```
普通保存时：
```text
更新 metadata 修改时间。
写入当前内存 Project 已累计的工程总耗时。
更新 manifest 中 lastSavedWithSoftwareVersion。
不更新 createdWithSoftwareVersion。
```
### 16.6.4 metadata.json v1 字段
初版 `metadata.json` schema v1 固定保存：
```text
schemaVersion = 1
projectName
projectVersion
authorOrTeam
originalWork
copyright
notes
createdAtUtc
modifiedAtUtc
totalEditingTimeMilliseconds
```
上述字段全部必须存在；允许为空的用户文本仍写为空字符串，不使用 `null` 或缺字段替代。时间和累计值服从第 16.13.9 节；`modifiedAtUtc` 不得早于 `createdAtUtc`。未知字段、重复字段、非法文本、非 canonical 时间或负累计值使现有 `metadata.json` 无效，并按第 16.18.3 节导致打开失败。

保存事务获取 metadata 快照前，必须先把当前活动打开会话截至快照瞬间的单调 elapsed time 合并进 `totalEditingTimeMilliseconds`。读取累计值、保存、Save Copy、播放、Buffering、导出和渲染都不建立独立计时器，也不得重复累计同一时间区间。
---
## 16.7 settings 文件
### 16.7.1 拆分
初版 settings 拆分为：
```text
settings/project-settings.json
settings/export-settings.json
settings/playback-settings.json
settings/audio-render-settings.json
settings/soundfont-settings.json
settings/global-reset-defaults.json
settings/global-event-scope-defaults.json
```
所有 settings 文件都必须在有效包结构中存在。
除本章对 Audio Render Settings 的严格例外外，既有 settings 文件缺失或损坏时：
```text
Project 可打开。
该 settings 回退当前软件版本定义的默认值。
产生错误诊断。
Project 标记为已修改。
保存时写出完整 settings 文件。
```
`settings/audio-render-settings.json` 的当前格式缺失 / 损坏规则见 11.5：如果当前 `fileFormatVersion` 明确要求该文件存在，则不得一律静默回退；只有明确旧格式迁移路径才允许补默认值。
### 16.7.2 project-settings.json
保存项目级系统设置，例如：
```text
TPQ
项目创建后不可修改的项目级时间精度
其他非 metadata、非 playback、非 export、非 soundfont 的项目设置
```
TPQ 必须保存于 `settings/project-settings.json`。
TPQ 是 Project 语义，不是文件格式语义。
开发期 v1 的 `ticksPerQuarterNote` 只接受整数 `1..32767`；打开超出范围的值按 settings 结构损坏处理，不迁移、不自动缩放 tick。
开发期 v1 还要求 `conductor-track.json` 中每个 Time Signature 满足 `4 × ticksPerQuarterNote % denominator == 0`。这是 project settings 与 Conductor 之间的跨文件一致性约束；读取和保存均必须校验，不创建新 schemaVersion、fileFormatVersion 或迁移器。
### 16.7.3 export-settings.json
保存 Project 默认导出设置。
开发期 v1 固定保存：导出模式、范围策略、仅在 Manual Range 时存在的 start/end tick、Track 选择策略、Routing、Readme 开关与 Warning-as-error 开关。新 Project 默认 Whole Project / Project Default Range / All Valid Logical Tracks / Compact / Include Readme / 不把 Warning 当 Error。显式 Track 具体稳定 ID 不进入该 settings 文件。
一次性导出参数不保存进 Project，除非用户明确将其保存为默认 Export Settings。
不保存：
```text
最近一次导出路径
最近一次导出范围
最近一次导出 Track 选择
最近导出产物
```
### 16.7.4 playback-settings.json
保存 Project 播放设置，例如：
```text
Playback Master Volume
Stop Cursor Behavior
其他属于 Project Playback Settings 的设置
```
不保存：
```text
播放设备选择
Render-Ahead Buffer
Device Buffer Request
Realtime Maximum Sample Voices per Unit Stream
Audio Cache Root
Maximum Reusable Audio Cache Bytes
设备实际采样率 / buffer / callback period
Mute / Solo
播放光标位置
最近播放位置
播放进度
```
播放设备选择属于本机环境或运行期设置，不属于 Project 文件语义。
### 16.7.5 audio-render-settings.json
保存 Project 顶层 `Audio Render Settings`。
初版至少保存：
```text
默认渲染模式：Whole Mix / Per Logical Track
默认范围模式：Project Default Range / Manual Range
可选默认 startTick / endTick
默认 Track 选择策略：All Valid Logical Tracks / Explicit Logical Track IDs
显式 Logical Track 稳定 ID 集合
有限命名偏好
默认文件采样率：8,000–192,000 Hz 整数，默认 48,000 Hz
默认 Offline Maximum Sample Voices per Unit Stream：1–16,777,216 整数，默认 500
固定格式：RIFF/WAVE / Stereo / Interleaved IEEE 32-bit Float / Little-endian
```
不保存：
```text
最近整曲输出路径
最近分轨输出目录
本次一次性范围
本次一次性 Track 选择
本次覆盖决定
渲染进度、耗时或剩余时间
渲染任务历史
WAV 产物
音频样本缓存
```
该文件必须纳入 manifest：
```text
path
settings-json kind
schemaVersion
SHA-256
```
严格规则：
```text
固定格式字段属于当前 schema 必需且用户不可编辑的版本化字段。
默认文件采样率属于用户可编辑 Project 默认值，但必须是 8,000–192,000 Hz 整数。
默认 Offline Maximum Sample Voices per Unit Stream 属于用户可编辑 Project 默认值，但必须是 1–16,777,216 整数。
未知枚举、非法范围、无效字段组合或不支持的固定格式不得静默接受。
当前 schema 要求文件存在而文件缺失 / hash 错误 / schema 无效时，按结构性设置损坏或不兼容规则处理。
只有明确旧 fileFormatVersion 的迁移路径才允许生成默认设置。
```
旧格式迁移默认生成：
```text
Mode = Whole Mix
Range = Project Default Range
Track Selection = All Valid Logical Tracks
Format = RIFF/WAVE / Stereo / Interleaved IEEE 32-bit Float
Sample Rate = 48,000 Hz
Offline Maximum Sample Voices per Unit Stream = 500
```
保存时只写出已规范化的合法设置；已删除 Track 的无效 ID 不得原样写回。
### 16.7.6 soundfont-settings.json
初版 schema v1 固定保存：
```text
schemaVersion = 1
mode = none / external / embedded
relativePath（仅 external）
resourceId（仅 embedded）
originalFileName（已选择时）
sha256（已选择时）
fileSizeBytes（已选择时，非负 int64）
```
`mode = none` 时不得保留其余 payload 字段。External 与 Embedded 的字段组合必须严格互斥，未知字段或无效组合导致该 settings 文件无效。

外部引用只保存第 6.4.4 节允许的两种相对路径，不保存绝对路径 fallback。`resourceId` 使用 canonical 非零稳定 ID。`sha256` 是 64 个小写十六进制字符；external 表示用户最后明确接受的内容，embedded 表示包内资源的预期内容。

缺失、不可读、hash mismatch、case-insensitive fallback、BASSMIDI 加载失败和“验证中”等状态属于打开后的派生资源状态，不写入 Project。被动状态变化不得改写本文件或使 Project 进入已修改状态。
### 16.7.7 global-reset-defaults.json
保存 Project 级 Reset 默认值，例如：
```text
CC Reset 默认值
Pitch Bend Reset 默认值
RPN / NRPN Reset 默认值
Program / Bank 相关 Reset 策略默认值
其他 Project 级 Reset Defaults
```
### 16.7.8 global-event-scope-defaults.json
初版保存不可编辑的版本化空 marker，只包含严格 schema 所要求的版本字段。该文件不得被解释为存在未定义的用户可配置事件作用域；Note 与 Channel-Wide 状态的作用域由各正式事件语义固定。未来如新增配置字段，必须发布新的 schema 版本并定义迁移规则。
---
## 16.8 conductor-track.json
### 16.8.1 内容
`conductor-track.json` 保存完整 Conductor Track 内容，包括：
```text
Tempo
Time Signature
Key Signature
Marker
Project End Marker
Conductor Track 内部事件稳定 ID
Conductor Track 事件顺序
```
Project End Marker 保存为 Conductor Track 中的特殊事件。
Project End Marker 可选。
### 16.8.2 事件稳定 ID
Conductor Track 中的可编辑事件必须拥有稳定 ID。
包括：
```text
Tempo
Time Signature
Key Signature
Marker
Project End Marker
```
稳定 ID 用于：
```text
撤销 / 重做
诊断定位
复制
排序稳定
打开后引用保持
```
### 16.8.3 缺失或损坏
如果 `conductor-track.json` 缺失或损坏：
```text
Project 可打开。
Conductor Track 回退为默认 Tempo 120 / Time Signature 4/4。
产生错误诊断。
Project 标记为已修改。
保存时写出回退后的 Conductor Track。
```
该回退可能改变项目语义。
这种风险由文件损坏状态本身导致，初版不尝试从损坏 JSON 中局部恢复。
损坏回退时：
```text
原事件无法反序列化。
不保留原事件 ID。
系统生成新的默认事件对象或使用新的内存默认对象。
```
### 16.8.4 语义错误
如果 Conductor Track 成功读取，但存在语义错误，例如：
```text
多个 Project End Marker
事件 tick < 0
Tempo BPM <= 0
非法 Time Signature
Time Signature Denominator 与 Project TPQ 不满足 `4 × TPQ % Denominator == 0`
非法 Key Signature
```
Project 可打开，但应产生错误诊断。
相关编译、播放、导出应禁止，直到用户修复。
这类问题不是文件结构损坏，而是 Project 语义错误。

开发期 v1 的 TPQ/Time Signature 整除约束是例外：它同时决定 `Bar:Beat:Tick` 能否按 v1 固定整数语义读取。若单个文件各自结构有效、但二者组合不满足 `4 × TPQ % Denominator == 0`，持久化读取必须拒绝该 Conductor 内容，并按第 16.18.5 节的 Conductor 损坏/缺失有界回退规则处理；不得以分数 tick、量化或静默改拍号继续。
---
## 16.9 Event Instrument protobuf 文件
### 16.9.1 文件粒度
初版采用：
```text
每个 Event Instrument 一个 .pb 文件。
```
不采用：
```text
整个 Event Instrument Library 一个 .pb
每个 SubVoice 一个 .pb
大 SubVoice 自动拆分
```
### 16.9.2 保存内容
Event Instrument `.pb` 保存该 Event Instrument 定义本体完整内容，包括：
```text
稳定 ID
对象类型
schemaVersion
名称
颜色
备注 / 描述
Root Note
Template Length
SubVoice 集合
SubVoice 顺序
SubVoice 内事件与曲线
Logical Parameters
Logical Parameter Mapping
Mapping Function 源码 / 定义入口
Envelope Presets
Per-Note Instance Isolation
生命周期策略
Overlap 策略
Reset 策略入口
其他属于 Event Instrument 定义的内容
```
SubVoice 不拆独立文件。
Mapping Function 源码 / 定义保存于对应 Event Instrument `.pb` 内。
每个 Mapping Function 定义必须保存 `abiVersion`、函数体源码和声明的 Context 字段集合。初版新建函数固定写 `abiVersion = 2`；未知 ABI 可以作为源数据打开和保留，但实际参与编译时按 Mapping Function 编译错误处理。编译产物、参考程序集、AssemblyLoadContext 状态和缓存不得写入 `.midora`。
Logical Parameter Definition 保存于对应 Event Instrument `.pb` 内。
### 16.9.3 Library 集合结构
Event Instrument Library 的集合级信息保存于 `project.json`，包括：
```text
Event Instrument 顺序
文件夹分组
库内显示结构
对象 ID 到文件路径映射
损坏占位需要的名称快照
```
Event Instrument 颜色属于 Event Instrument 定义级元数据，保存于对应 `.pb` 内。
### 16.9.4 删除后保存
用户删除 Event Instrument 后保存：
```text
新包不再写出该 Event Instrument .pb。
不保留孤立文件。
不写删除标记。
```
---
## 16.10 Logical Track protobuf 文件
### 16.10.1 文件粒度
初版采用：
```text
每个 Logical Track 一个 .pb 文件。
```
不采用：
```text
所有 Logical Tracks 一个 .pb
每个 Segment 一个 .pb
大 Segment 自动拆分
```
### 16.10.2 保存内容
Logical Track `.pb` 保存该 Track 本体完整内容，包括：
```text
稳定 ID
对象类型
schemaVersion
Track 名称
颜色覆盖
Event Instrument 绑定 ID
最近一次绑定 Event Instrument 名称提示
Segment 集合
Segment 内 Logical Note
Logical Parameter Lane / Point / Curve
Track 内部对象 ID
Segment 裁剪窗口
其他属于 Logical Track / Segment 的内容
```
Segment 不拆独立文件。
Logical Parameter Lane 属于 Segment / Logical Track 编曲内容，保存于对应 Logical Track `.pb` 内。
### 16.10.3 Track 改绑 / 断裂参数 Lane
如果 Event Instrument 被删除、Track 取消绑定或 Track 改绑，Track 中已有 Logical Parameter Lane 可能变为断裂 / 不适用。
保存时：
```text
仍保存这些 Logical Parameter Lane 数据。
不自动删除。
不自动按名称迁移。
不移到 project.json。
```
后续由 UI / 诊断处理。
### 16.10.4 Track 集合结构
Logical Track 集合级信息保存于 `project.json`，包括：
```text
Track 顺序
对象 ID 到文件路径映射
损坏占位需要的名称快照
```
Logical Track 颜色覆盖保存于对应 `.pb` 内。
Event Instrument 绑定 ID 保存于对应 Logical Track `.pb` 内。
最近一次绑定 Event Instrument 名称提示保存于对应 Logical Track `.pb` 内。
### 16.10.5 删除后保存
用户删除 Logical Track 后保存：
```text
新包不再写出该 Logical Track .pb。
不保留孤立文件。
不写删除标记。
```
---
## 16.11 对象路径、ID 与一致性
### 16.11.1 文件名使用稳定 ID
对象文件名必须基于稳定 ID，而不是用户可见名称。
原因：
```text
Event Instrument 名称可重命名。
Logical Track 名称允许重复。
用户可见名称可能包含非法路径字符。
用户可见名称可能存在大小写问题。
内部引用不得依赖名称。
```
### 16.11.2 类型前缀
对象文件名必须包含对象类型前缀：
```text
ei_<id>.pb
lt_<id>.pb
```
其中 `<id>` 必须使用 16.13.2 规定的无符号、无前导零十进制 ASCII。
### 16.11.3 对象内部 ID 与类型
对象 `.pb` 内部必须保存：
```text
自身稳定 ID
对象类型
schemaVersion
```
打开时必须校验：
```text
文件名 ID
project.json 索引 ID
manifest 记录
对象内部 ID
对象内部类型
所在目录 / 路径
```
之间的一致性。
### 16.11.4 project.json 中路径
即使对象文件路径可由 ID 和类型确定性派生，`project.json` 仍保存对象文件路径，用于显式索引和一致性校验。
如果路径派生结果与 `project.json` 路径不一致：
```text
整个 Project 打开失败。
```
### 16.11.5 三方一致性
对于 Project 语义对象文件：
```text
manifest 路径
project.json 路径
实际 Zip entry
```
必须一致。
不一致时按本章打开失败 / 损坏占位规则处理。
---
## 16.12 schemaVersion 与严格 schema 策略
### 16.12.1 双层 schemaVersion
每个结构性文件都必须拥有 schemaVersion。
结构性文件包括：
```text
project.json
metadata.json
conductor-track.json
settings/*.json
event-instruments/*.pb
logical-tracks/*.pb
未来新增结构性文件
```
schemaVersion 必须同时保存于：
```text
manifest.json 文件索引
结构性文件自身内部
```
用途区别：
```text
manifest 中的 schemaVersion：打开前预检与兼容性判断入口。
文件内部 schemaVersion：文件自描述与一致性校验依据。
```
打开文件后，必须校验文件内部 schemaVersion 与 manifest 记录完全一致。
不一致时：
```text
整个 Project 打开失败。
```
### 16.12.2 schemaVersion 与 fileFormatVersion
`fileFormatVersion` 管理包结构。
`schemaVersion` 管理具体结构性文件内容。
二者允许独立演进。
### 16.12.3 高版本 schema
如果任何 Project 需要读取的结构性文件 schemaVersion 高于当前软件支持版本：
```text
整个 Project 打开失败。
```
理由：
```text
初版采用严格 schema。
旧版软件不支持新版结构。
不得尝试忽略未知字段读取。
```
如果 manifest 已显示 schemaVersion 过新，不需要继续校验 hash，直接拒绝打开。
### 16.12.4 低版本 schema
如果某个结构性文件 schemaVersion 低于当前软件支持版本：
```text
使用迁移流程迁移到当前内存结构。
保存时写为当前 schema。
```
### 16.12.5 未知字段
结构性 JSON / protobuf 文件中出现当前 schema 不认识的字段：
```text
打开失败。
```
不采用：
```text
忽略未知字段
尽量保留未知字段
保存时原样写回未知字段
```
JSON 必须在反序列化前或反序列化过程中检查重复属性，并通过版本化 DTO 拒绝未知属性。protobuf 必须先以当前消息 descriptor 检查原始 wire 数据，再反序列化；任一未知 field tag、错误 wire type、非法 UTF-8、越界标量或畸形 wire 数据均导致对应结构性文件读取失败。不得依赖 protobuf runtime 默认保留或跳过 `UnknownFieldSet` 的行为。
### 16.12.6 schema 演进原则
已发布字段原则：
```text
不实际删除字段。
废弃字段应标记 deprecated。
protobuf 字段号永不复用。
protobuf 已删除字段的编号与名称必须 `reserved`；新增字段必须使用新的明确字段号。
JSON 已发布字段名不复用为不同语义。
已发布 enum 值不得改变语义。
```
开发阶段：
```text
正式 schemaVersion 不应因内部未冻结调整而频繁提升。
应在该格式阶段冻结后确定版本。
```
这要求项目数据结构设计保持严谨。
---
## 16.13 持久化数据类型规则
### 16.13.1 tick
tick 值持久化使用：
```text
64-bit signed integer
```
不得使用 floating point 保存 tick。
### 16.13.2 ID
Project 内稳定 ID 的语义核心固定为 signed 64-bit integer，合法范围统一为：
```text
1..9223372036854775807（long.MaxValue）
```
`0` 与负值非法；所有现存对象稳定 ID 还必须小于 `nextStableId`。

JSON 中的对象稳定 ID 与 `project.json.nextStableId` 必须使用十进制 JSON integer token。原始 UTF-8 token 必须匹配：
```text
[1-9][0-9]*
```
读取器必须拒绝字符串、小数、指数、正负号、前导零、超出 `long.MaxValue` 的 token 以及任何静默规范化。正式 .NET 读取器必须以 64-bit integer 精确处理；使用 JavaScript 等只能精确表示到 `2^53-1` 的第三方消费者必须自行使用任意精度整数解析。

对象文件名中的 `<id>` 使用同一数值的 invariant 十进制 ASCII：无正负号、无前导零，且必须落在相同合法范围。文件名、`project.json` 索引和对象内部 ID 必须数值一致。

Project 内稳定 ID 在 protobuf 中固定直接使用标量：
```proto
int64 <field_name> = <existing_outer_field_number>;
```
每个对象或引用保留其既有外层字段号；正值按 protobuf 标准 `int64` varint 编码。不得保留嵌套 `StableId` 消息，不得使用 `sint64`、`fixed64`、`bytes`、GUID 混合字节序或其他替代布局。读取器必须拒绝缺省得到的零、负值和超出合法语义范围的值。

该布局是首版冻结前对开发期 v1 的直接修订；旧 128-bit high/low 布局未发布，不提供迁移器或兼容读取分支。
### 16.13.3 enum
持久化 enum 必须使用稳定编号或稳定字符串。
JSON 中 enum 推荐保存为稳定字符串，例如：
```text
"embedded"
"external"
```
不得保存本地化 UI 显示文本。
### 16.13.4 Double
JSON Double 类型字段不允许：
```text
NaN
Infinity
-Infinity
非标准 JSON token
```
protobuf Double 类型字段也不允许持久化 NaN / Infinity。
保存前必须诊断失败或规范化。
### 16.13.5 路径
包内路径、相对资源路径在项目文件中保存为 UTF-8 相对路径字符串，并统一使用：
```text
/
```
不得使用 Windows `\`。保存和读取时保持原大小写与原 Unicode scalar 序列，不执行 Unicode normalization，不改写分隔符，也不静默修正路径。

路径必须包含 1–4096 个 Unicode scalar，并拒绝：
```text
空 segment
`.` 或 `..` segment
绝对路径
Windows 盘符路径
反斜杠
NUL 或控制字符
```
SoundFont 外部引用进一步限制为第 6.4.4 与 16.15 节的两种直接子路径；大小写回退、hash 不匹配处置与验证时机按这些章节执行。
### 16.13.6 字符串规范化
初版不强制 Unicode normalization。
用户可见字符串保持用户输入。
唯一性比较按对应章节规则处理，例如 Event Instrument 名称大小写不敏感并去除首尾空白。
### 16.13.7 文本长度
所有上限按 Unicode scalar 数量计算，不按 UTF-16 code unit 或 UTF-8 byte 数计算。初版固定上限为：

| 字段类别 | 最大 Unicode scalars |
|---|---:|
| 短名称、标签、用户可见版本、Marker 名称、名称快照 | 256 |
| 作者、版权及其他单行 metadata | 4,096 |
| 描述、备注 | 65,536 |
| 单个 C# Mapping Function 函数体 | 1,048,576 |
| 相对路径 | 4,096 |

超过上限必须拒绝，不得截断。短文本和单行 metadata 不允许控制字符；描述可包含 Tab、LF、CR，但不允许 NUL 或其他控制字符。

### 16.13.8 颜色
初版持久颜色固定为不透明 sRGB，不保存 alpha。protobuf 使用：

```proto
message RgbColor {
  uint32 red = 1;
  uint32 green = 2;
  uint32 blue = 3;
}
```

每个分量必须位于 `[0, 255]`。颜色若出现在 JSON 中，固定使用小写 `#rrggbb`；不得接受 shorthand、alpha、命名颜色或大写 canonical 输出。

### 16.13.9 时间与工程总耗时
`metadata.json` 的 `createdAtUtc` 与 `modifiedAtUtc` 固定使用 UTC、七位小数秒和 `Z` 后缀：

```text
yyyy-MM-ddTHH:mm:ss.fffffffZ
```

不得接受本地时间、时区偏移或不同小数位数作为 v1 canonical 表示。工程总耗时字段固定命名为 `totalEditingTimeMilliseconds`，使用非负 signed 64-bit integer；本节只固定存储表示，哪些运行阶段计入累计仍由独立工程总耗时决定固定。
---
## 16.14 JSON / protobuf 输出确定性
### 16.14.1 JSON
保存 JSON 时要求尽量确定性输出：
```text
稳定字段顺序
稳定缩进
稳定换行
UTF-8 无 BOM
标准 JSON
不允许注释
```
推荐格式：
```text
2 spaces
LF
```
具体字段顺序由实现层定义，但同一内容重复保存应尽量产生稳定输出。
### 16.14.2 protobuf
保存 protobuf 时要求 deterministic serialization，并使用当前已固定 runtime/codegen profile。protobuf 的 deterministic serialization 只保证同一 runtime/profile 下的稳定输出，不等同于跨实现、跨版本的 canonical encoding。
如果使用 map：
```text
不得让 map 顺序导致同内容多次保存输出不同。
```
并行序列化只影响执行计划，不得改变最终文件内容或 Zip entry 顺序。
每个已发布 protobuf schema 必须有 descriptor 基线和代表性 golden bytes；升级库、工具或生成 profile 时必须执行兼容性评审，不得仅因新版本仍声明 deterministic 就自动采用。
---
## 16.15 SoundFont 资源持久化
### 16.15.1 内嵌 SF2 位置
内嵌 SF2 保存于：
```text
resources/soundfonts/<resourceId>.sf2
```
包内文件名使用资源 ID。
原始文件名保存于：
```text
settings/soundfont-settings.json
```
### 16.15.2 单一 SF2
初版一个 Project 最多一个当前有效 SF2。
因此包内最多允许一个当前有效内嵌 SF2 资源。
Project 当前语义只允许一个 SoundFont 设置。
### 16.15.3 外部引用 SF2
外部引用 SF2 的信息保存于：
```text
settings/soundfont-settings.json
```
包括：
```text
相对路径
last known hash
原始文件名
文件大小等辅助信息
```
初版只保存允许目录下的相对路径，不保存绝对路径 fallback。
路径逐分量优先 ordinal 精确匹配；仅当精确匹配不存在且 ordinal-ignore-case 候选唯一时才允许回退，并产生 Warning。多个大小写近似候选导致资源歧义和不可用。回退只影响本次运行时解析，不改写已保存路径；用户明确重新绑定或接受当前文件后，才以实际解析到的大小写写入新路径。

外部 `sha256` 和 `fileSizeBytes` 只在用户明确选择、替换、重新绑定或接受当前内容时更新。打开时完整流式计算 SHA-256；普通保存、保存副本、文件监控和音频任务中的被动检查不得静默更新这些字段或 Project 修改状态。
如果外部 SF2 缺失：
```text
Project 正常打开。
SoundFont Settings 标记不可访问。
播放 / 预览 / 音频渲染不可用。
MIDI 导出仍可用。
不标记 Project 已修改。
```
如果外部 SF2 hash 变化但文件可加载：
```text
警告后加载。
```
外部 SF2 是用户可替换资源，hash 变化只表示外部文件已变化。
### 16.15.4 内嵌 SF2 完整性
内嵌 SF2 是项目包内部资源。
manifest 记录其 SHA-256。
`soundfont-settings.json` 同时记录同一资源 ID、SHA-256、原始文件名和未压缩文件大小；settings hash、manifest hash 与实际未压缩资源字节必须一致。导入和 package 写出均流式计算，不得整文件读入单个托管数组。
如果内嵌 SF2 hash 不匹配或无法加载：
```text
Project 正常打开。
SoundFont Settings 标记资源损坏 / 无法加载。
播放 / 预览 / 音频渲染不可用。
MIDI 导出仍可用。
不标记 Project 已修改。
```
内嵌 SF2 hash 不匹配代表项目包一致性损坏，不等同于外部 SF2 的普通变化。
如果 Project 打开后当前内嵌 SF2 资源损坏但其他 Project 数据正常：
```text
允许普通保存和保存副本。
保存写出当前内存 Project 中的 SoundFont Settings 与可用资源状态。
```
### 16.15.5 取消或替换 SF2
用户取消选择 SF2 后保存：
```text
新包不再写出旧内嵌 SF2。
```
用户替换内嵌 SF2 后保存：
```text
新包只写出当前被引用的新内嵌 SF2。
旧内嵌 SF2 不保留为历史资源。
```
---
## 16.16 Zip 容器合法性
### 16.16.1 Zip64
`.midora` 允许使用 Zip64。
理由：
```text
内嵌 SF2
长项目
大量事件数据
```
可能超过传统 Zip 限制。
### 16.16.2 根目录
`manifest.json` 必须位于 Zip 根目录。
不允许包内嵌套一层项目文件夹，例如：
```text
MyProject/manifest.json
```
这种结构打开失败。
### 16.16.3 路径规则
Zip entry 路径统一使用 `/`。
如果 Zip entry 使用 `\`：
```text
打开失败。
```
如果 Zip entry 路径包含：
```text
../
..\
绝对路径
Windows 盘符路径
```
打开失败。
### 16.16.4 重复路径
如果 Zip 中存在两个相同路径 entry：
```text
打开失败。
```
### 16.16.5 大小写冲突
包内路径大小写敏感，但初版不允许大小写近似冲突。
例如同时存在：
```text
project.json
Project.json
```
打开失败。
### 16.16.6 空目录 entry
Zip 中允许空目录 entry，但不参与 Project 语义。
保存时不写出空目录 entry。
### 16.16.7 非普通文件
如果 Zip 中存在符号链接、设备文件等非普通文件 entry：
```text
打开失败。
```
### 16.16.8 加密 Zip
初版不支持 Zip 加密或密码保护。
如果 `.midora` 是加密 Zip：
```text
打开失败。
```
### 16.16.9 Zip comment
初版不使用 Zip comment。
magic 不放 Zip comment，只放 `manifest.json`。
### 16.16.10 文件数量与大小
初版不设置：
```text
包内文件数量硬限制
包内单文件大小硬限制
解压总大小检测
解压膨胀比例检测
```
Midora `.midora` 是自身项目文件格式，不是通用安全沙箱文件格式。
初版不把抵御恶意构造 Zip bomb / 超大包攻击作为核心需求。
用户打开不可信来源 `.midora` 文件时，风险由文件来源本身带来。
---
## 16.17 打开流程
### 16.17.1 分阶段打开
打开 `.midora` 应按阶段处理：
```text
1. Zip 容器合法性检查
2. manifest.json 读取与版本预检
3. manifest 文件索引一致性检查
4. hash 校验
5. 核心 JSON / settings / conductor 读取
6. project.json 语义索引读取
7. Project 对象文件读取
8. 对象引用与一致性检查
9. 生成内存 Project
10. 生成打开诊断
```
打开失败时：
```text
不创建内存 Project。
不改变原 .midora 文件。
```
打开成功但存在诊断时：
```text
不改变原 .midora 文件。
```
### 16.17.2 文件占用
Project 成功打开并反序列化到内存后：
```text
不应长期占用原 .midora 文件。
打开完成后释放文件句柄。
```
打开 / 反序列化过程中可以短暂占用文件，以避免读到半修改状态。
打开后如果外部程序修改原 `.midora` 文件：
```text
初版不主动监控。
下次普通保存按当前内存 Project 覆盖目标路径。
```
### 16.17.3 多余文件
如果打开时发现 Zip 中存在未被 manifest 记录的额外文件：
```text
产生 Info，说明该文件不属于当前 Project，保存后会被移除。
不标记 Project 已修改。
不影响编译 / 播放 / 导出。
```
如果 manifest 记录了对象文件，但 `project.json` 未引用：
```text
产生 Info，说明该文件不属于当前 Project，保存后会被移除。
不标记 Project 已修改。
不影响编译 / 播放 / 导出。
```
保存后这些 Info 消失，因为新包不再包含这些文件。
### 16.17.4 打开非 Zip 或缺失 manifest
如果文件后缀为 `.midora`，但不是 Zip：
```text
打开失败，提示不是有效 Midora Zip package。
```
如果 Zip 可打开但缺少 `manifest.json`：
```text
打开失败，提示不是有效 Midora 项目包。
```
初版不扫描恢复、不创建默认 manifest。
---
## 16.18 打开失败与损坏处理
### 16.18.1 manifest 损坏
如果 `manifest.json` 缺失、无法解析、magic 不匹配、manifest schemaVersion 过新或包含未知字段：
```text
整个 Project 打开失败。
```
### 16.18.2 project.json 损坏
如果 `project.json` 缺失、损坏、hash 不匹配或无法解析：
```text
整个 Project 打开失败。
```
`project.json` 是 Project 语义入口，不进行扫描重建。
### 16.18.3 metadata.json 损坏或缺失
如果 `metadata.json` 缺失：
```text
Project 可打开。
Metadata 回退默认空值。
产生错误诊断。
Project 标记为已修改。
```
如果 `metadata.json` 损坏、hash 不匹配或无法解析：
```text
整个 Project 打开失败。
```
### 16.18.4 settings 损坏或缺失
settings JSON 缺失、损坏或 hash 不匹配：
```text
Project 可打开。
对应 settings 回退当前软件默认值。
产生错误诊断。
Project 标记为已修改。
保存时写出完整 settings。
```
### 16.18.5 conductor-track 损坏或缺失
`conductor-track.json` 缺失、损坏或无法解析：
```text
Project 可打开。
Conductor Track 回退默认 Tempo 120 / 4/4。
产生错误诊断。
Project 标记为已修改。
```
### 16.18.6 project.json 引用对象但 manifest 缺少记录
如果 `project.json` 引用某对象文件，但 manifest 没有该文件记录：
```text
整个 Project 打开失败。
```
原因：
```text
Project 语义索引与包文件索引不一致。
manifest 是结构性文件预检入口。
project.json 不应绕过 manifest。
```
### 16.18.7 project.json 引用对象且 manifest 记录存在但 Zip 文件缺失
如果 `project.json` 引用对象，manifest 也记录该文件，但 Zip entry 缺失：
```text
Project 可打开。
该对象形成损坏占位。
```
### 16.18.8 对象 hash 不匹配
如果 Project 语义对象 `.pb` hash 不匹配：
```text
Project 可打开。
该对象形成损坏占位。
```
hash 不匹配说明对象文件不可信，即使仍能反序列化也不得作为正常对象使用。
### 16.18.9 对象 schema 过新
如果 Project 语义对象的 schemaVersion 高于当前软件支持：
```text
整个 Project 打开失败。
```
### 16.18.10 manifest 与对象内部 schemaVersion 不一致
如果 manifest 记录的 schemaVersion 与对象文件内部 schemaVersion 不一致：
```text
整个 Project 打开失败。
```
### 16.18.11 对象 ID 不一致
如果对象文件可反序列化，但对象内部 ID 与 `project.json` 索引不一致：
```text
Project 可打开。
该对象形成损坏占位。
```
### 16.18.12 对象类型 / 路径错乱
如果对象文件路径、manifest file kind、`project.json` 类型、对象内部类型之间出现类型错乱：
```text
整个 Project 打开失败。
```
例如：
```text
project.json 认为是 Event Instrument，
但对象内部类型是 Logical Track。
```
---
## 16.19 损坏占位对象
### 16.19.1 Event Instrument 损坏占位
如果 Event Instrument `.pb` 无法作为正常对象加载，但 `project.json` 中存在索引：
```text
形成损坏占位对象。
保留 ID。
保留名称快照，如果 project.json 可提供。
保留文件路径。
保留错误信息。
不作为正常 Event Instrument。
不参与编译。
```
损坏占位保留在原排序位置，便于用户定位和删除。
### 16.19.2 Logical Track 损坏占位
如果 Logical Track `.pb` 无法作为正常对象加载，但 `project.json` 中存在索引：
```text
形成损坏占位 Track。
保留 ID。
保留名称快照。
保留文件路径。
保留错误信息。
不参与编译。
```
### 16.19.3 引用损坏 Event Instrument
如果某个 Logical Track 引用了损坏 Event Instrument：
```text
Track 保留绑定 ID。
显示为绑定对象损坏 / 不可用。
该 Track 不参与编译。
产生错误诊断。
```
### 16.19.4 删除损坏占位
用户允许删除损坏 Event Instrument / Logical Track 占位。
删除规则：
```text
进入 Undo / Redo。
使 Project 进入已修改状态。
保存后从 project.json 索引移除。
保存后不写出原损坏 .pb 文件。
```
删除损坏 Event Instrument 占位后，引用它的 Logical Track：
```text
变为未指定 Event Instrument。
保留最近一次绑定名称提示。
```
删除损坏 Logical Track 占位后：
```text
从 Track 索引中移除。
保存后不再写出该损坏 Track 文件。
```
撤销删除损坏占位时：
```text
恢复损坏占位、ID、索引位置和错误信息。
仍不可用。
```
### 16.19.5 损坏占位与保存
只要 Project 中仍存在损坏 Event Instrument / Logical Track 占位：
```text
禁止普通保存。
禁止保存副本。
```
损坏占位没有对应 `.pb` 写出格式。
---
## 16.20 诊断系统边界
### 16.20.1 打开诊断
打开诊断不保存进 Project。
诊断是本次打开 / 当前软件分析结果。
### 16.20.2 诊断来源分类
诊断应能区分：
```text
文件格式诊断
文件损坏诊断
版本不兼容诊断
项目语义诊断
资源缺失诊断
保存事务诊断
```
### 16.20.3 文件格式诊断与编译诊断
文件格式诊断不直接作为编译诊断。
但如果文件格式问题导致内存 Project 中对象缺失、对象损坏或对象不可用，则会间接影响编译。
### 16.20.4 源定位
打开失败或对象损坏诊断应尽量定位到：
```text
包内文件路径
对象 ID
对象名称快照
对象类型
```
示例：
```text
event-instruments/ei_x.pb
logical-tracks/lt_y.pb
project.json
settings/playback-settings.json
```
### 16.20.5 保存诊断
保存前如果因以下原因禁止保存，应收集并显示保存诊断：
```text
损坏对象占位
Project 内部 ID 不唯一
Project 内存一致性错误
project.json 将引用不存在对象
对象序列化失败
保存前一致性检查失败
```
保存诊断不进入 Undo / Redo。
保存成功后：
```text
清除本次保存诊断。
仍然适用的项目诊断需要重新计算。
```
---
## 16.21 保存禁止条件
### 16.21.1 禁止保存
以下状态禁止普通保存和保存副本：
```text
存在损坏 Event Instrument 占位
存在损坏 Logical Track 占位
Project 内部稳定 ID 不唯一
保存前一致性检查失败
对象序列化失败
project.json 将引用不存在对象
Project 内存结构无法生成合法包
```
### 16.21.2 不禁止保存
以下状态不禁止保存：
```text
settings 缺失 / 损坏并已回退默认值
conductor-track 缺失 / 损坏并已回退默认值
外部 SF2 缺失
外部 SF2 hash 变化
内嵌 SF2 损坏 / 无法加载
存在打开时 Info：保存后会移除多余文件
Logical Track 绑定已删除 Event Instrument 后处于未指定 / 断裂绑定状态，只要内存模型允许该状态
```
保存这些状态时，应写出当前内存 Project 表示。
---
## 16.22 普通保存事务
### 16.22.1 基本流程
普通保存必须采用安全保存事务。
总体流程：
```text
0. 将原项目文件复制一份，使用唯一名称作为本次保存事务备份。
1. 在目标 .midora 同目录创建隐藏临时文件夹。
2. 在临时文件夹中构建项目包内容，可并行序列化多个对象。
3. 将临时文件夹内容打包为临时 .midora / Zip 文件。
4. 重新打开并自校验临时 .midora。
5. 使用操作系统支持的安全替换 / 原子替换机制，将临时 .midora 替换目标项目文件。
6. 保存成功后清理备份文件、临时文件和临时目录。
```
### 16.22.2 临时位置
临时目录应创建在目标 `.midora` 文件同目录。
临时 `.midora` 文件也应创建在目标 `.midora` 文件同目录。
理由：
```text
更利于最终替换在同一磁盘卷内完成。
降低跨卷移动失败风险。
```
### 16.22.3 事务 ID
备份文件、临时 `.midora`、临时目录命名应带唯一事务 ID。
事务 ID 不写入 Project 内容。
只用于：
```text
临时文件命名
失败诊断提示
避免多个失败保存残留互相覆盖
```
### 16.22.4 自校验
生成临时 `.midora` 后、替换目标文件前，必须重新打开并校验临时文件。
至少校验：
```text
Zip 可打开
manifest 可解析
magic 正确
核心入口文件存在
schemaVersion 记录一致
对象索引与文件存在性一致
hash 一致
```
自校验失败时：
```text
保存失败。
不得替换目标文件。
```
### 16.22.5 失败处理
阶段 1 / 2 / 3 / 自校验阶段失败：
```text
尚未触碰目标项目文件。
清理备份文件、临时文件、临时目录。
报告保存失败。
Project 保持已修改状态。
```
最终替换阶段失败：
```text
删除临时目录。
保留备份文件。
保留已生成临时 .midora 文件。
告知用户错误原因、备份文件路径、临时文件路径。
提醒用户自行处理。
Project 保持已修改状态。
```
最终替换阶段失败后不自动尝试用备份文件恢复目标文件。
理由：
```text
这类失败常见原因可能是文件被占用、权限异常、磁盘状态异常、杀毒软件拦截等。
在该状态下继续自动写入目标文件可能再次失败，甚至扩大损坏风险。
```
### 16.22.6 清理失败
如果保存成功，但清理备份文件 / 临时文件 / 临时目录失败：
```text
保存仍视为成功。
Project 标记为未修改。
显示清理警告。
```
### 16.22.7 保存状态
普通保存失败：
```text
Project 仍保持已修改状态。
```
普通保存成功：
```text
Project 标记为未修改。
更新 metadata 修改时间。
更新 manifest lastSavedWithSoftwareVersion。
```
### 16.22.8 目标路径异常
如果保存时目标文件不存在：
```text
按原路径重新创建项目文件。
```
如果目标路径目录不存在或不可访问：
```text
保存失败，提示用户处理或保存副本。
```
如果目标文件被其他程序占用导致最终替换失败：
```text
按最终替换阶段失败规则处理。
```
### 16.22.9 只读文件
如果 `.midora` 文件位于只读位置或文件属性只读：
```text
允许打开并编辑内存 Project。
普通保存可能失败。
初版不在打开时额外提示只读。
保存失败时只显示错误，不额外提供保存副本入口。
```
用户可手动执行保存副本。
---
## 16.23 保存副本 / Save Copy As
### 16.23.1 不采用传统 Save As
初版不提供传统“另存为 / Save As”语义。
传统 Save As 通常意味着保存到新路径后，当前 Project 的工作路径切换到新文件。
Midora 初版不采用该语义。
### 16.23.2 保存副本语义
初版支持：
```text
保存副本 / Save Copy As
```
语义：
```text
将当前内存 Project 以完整 .midora 文件形式写出到用户指定的新路径。
不改变当前 Project 的原保存路径。
不改变当前打开 Project 与原文件之间的关系。
不把当前工作文件切换到副本路径。
不进入 Undo / Redo。
```
### 16.23.3 保存副本事务
保存副本使用与普通保存相同的安全写出流程：
```text
临时目录构建
临时 .midora 打包
自校验
最终写入目标副本路径
清理临时文件
```
如果目标副本文件不存在：
```text
无需创建原文件备份。
```
如果目标副本文件已存在：
```text
必须询问用户是否覆盖。
用户确认覆盖后，使用安全替换流程，并为目标文件创建临时备份。
```
### 16.23.4 保存副本与当前 Project 状态
保存副本成功后：
```text
当前 Project 路径不改变。
当前 Project 修改状态不改变。
当前内存 metadata 修改时间不改变。
当前内存 lastSavedWithSoftwareVersion 不改变。
```
副本文件中：
```text
metadata 修改时间写为保存副本时间。
manifest lastSavedWithSoftwareVersion 写为当前软件版本。
```
保存副本失败：
```text
不影响当前 Project 修改状态。
按与普通保存相同的阶段规则处理临时文件和备份文件。
```
### 16.23.5 旧版本迁移后的保存副本
旧版本项目打开后已在内存中迁移，但尚未普通保存时：
```text
允许保存副本。
副本使用当前格式。
不改变原路径。
```
保存副本前必须确认：
```text
副本会写为当前格式。
```
---
## 16.24 版本迁移
### 16.24.1 打开旧版本项目
如果文件格式版本低于当前软件版本：
```text
打开时在内存中迁移。
不自动保存。
```
旧版本项目迁移成功后：
```text
提示用户该项目已在内存中迁移，保存后会变成当前格式。
Project 视为已修改 / 已迁移未保存状态。
```
对于首次引入 Audio Render Settings 的旧格式迁移，允许创建明确默认值；但当前格式中必需的 `settings/audio-render-settings.json` 缺失不得伪装成普通旧格式迁移。迁移结果必须符合当前严格 schema。
### 16.24.2 保存旧版本项目
用户点击普通保存时：
```text
需要确认将保存为当前格式。
```
如果用户取消确认：
```text
不保存。
Project 仍保持已修改 / 已迁移未保存状态。
```
保存成功后：
```text
保存为当前最新格式。
清除需要迁移保存状态。
```
不支持保存回旧格式。
### 16.24.3 迁移失败
如果旧版本项目在打开时迁移失败：
```text
打开失败。
原文件不修改。
不创建部分 Project。
不自动创建修复副本。
```
### 16.24.4 关闭迁移未保存项目
旧版本项目打开并完成内存迁移，但用户未保存就关闭：
```text
按已修改 Project 处理。
提示保存 / 不保存 / 取消。
```
---
## 16.25 Zip 写出策略
### 16.25.1 写出顺序
保存 `.midora` 时，Zip entry 写出顺序必须稳定。
建议顺序：
```text
manifest.json
project.json
metadata.json
conductor-track.json
settings/*.json
event-instruments/*.pb
logical-tracks/*.pb
resources/soundfonts/*.sf2
```
但由于 manifest 需要记录所有文件 hash，实际内容生成顺序为：
```text
先生成除 manifest 外的所有文件内容并计算 hash。
最后生成 manifest。
再按稳定 Zip entry 顺序打包。
```
Event Instrument / Logical Track 对象文件写出顺序：
```text
按 project.json 中的显式顺序。
```
### 16.25.2 时间戳
Zip entry 时间戳应统一写固定值或规范化值，避免同内容多次保存产生无意义差异。
Project 修改时间属于 metadata。
Zip entry 时间戳只是容器元数据。
manifest 不记录 Zip entry 时间戳。
### 16.25.3 压缩
初版默认压缩包内所有文件，包括：
```text
JSON
protobuf
SF2
```
SF2 不应默认视为已压缩资源。大量 SF2 内部采样可能是原始 PCM，Zip 压缩可能获得显著体积收益。
压缩等级不作为用户设置，由实现选择合理默认值。
系统语义上不要求每个 entry 必须压缩；具体压缩方式属实现细节。
manifest 不记录压缩等级。
### 16.25.4 并行序列化
保存时允许并行序列化 Event Instrument / Logical Track 等对象到临时目录。
但并行只影响执行计划，不得影响：
```text
最终文件内容
Zip entry 顺序
hash 结果
对象排序
诊断结果
```
如果某个对象序列化失败：
```text
整个保存失败。
不写出 partial 项目文件。
```
### 16.25.5 保存前一致性检查
保存前必须执行 Project 内存一致性检查，例如：
```text
ID 全局唯一
必要顶层对象存在
对象引用合法
project.json 可生成合法索引
对象文件路径可确定
settings 可序列化
metadata 可序列化
conductor-track 可序列化
TPQ 与全部 Time Signature 分母满足开发期 v1 整除约束
```
一致性检查失败：
```text
禁止保存。
产生保存诊断。
不进入 Undo / Redo。
```
---
## 16.26 保存期间锁定
保存或保存副本期间：
```text
禁止编辑 Project。
禁止 Undo / Redo。
禁止播放 / 预览。
禁止并发保存。
禁止并发保存副本。
```
如果播放中触发保存：
```text
按 第 13 章《播放与预览》 规则先自动 Stop，完成清理后再保存。
保存完成后保持 Stopped。
不自动恢复播放。
```
保存期间禁止后台继续编辑，避免保存快照一致性复杂化。
---
## 16.27 关闭 Project 时的保存提示
### 16.27.1 未保存新项目
新建但从未保存过的 Project，关闭时如果已修改：
```text
提示保存 / 不保存 / 取消。
```
### 16.27.2 已保存项目
已有保存路径的 Project，关闭时如果已修改：
```text
提示保存 / 不保存 / 取消。
```
### 16.27.3 迁移或回退导致修改
以下状态按已修改 Project 处理：
```text
旧版本项目已内存迁移但未保存。
settings 缺失 / 损坏后回退默认值。
conductor-track 缺失 / 损坏后回退默认值。
删除损坏对象占位后未保存。
保存失败后 Project 仍已修改。
```
### 16.27.4 不视为修改
以下状态不视为 Project 已修改：
```text
仅外部 SF2 缺失。
仅内嵌 SF2 hash 不匹配 / 资源损坏诊断。
仅存在多余文件保存后会移除 Info。
含损坏对象占位但用户未删除且没有其他编辑。
```
含损坏对象占位本身禁止保存，因此仅因该状态提示保存没有意义。
### 16.27.5 保存副本与关闭
如果当前 Project 已修改，用户保存副本成功后关闭：
```text
仍按已修改 Project 处理。
```
因为保存副本不改变当前 Project 保存状态。
保存副本失败不影响当前 Project 修改状态。
### 16.27.6 保存成功与关闭
普通保存成功后：
```text
Project 未修改。
关闭不再提示保存。
```
普通保存成功但清理临时文件失败：
```text
Project 仍视为未修改。
关闭不提示保存。
保留清理警告。
```
---
## 16.28 不保存内容清单
`.midora` 初版不保存：
```text
Undo / Redo 栈
Project 修改状态
诊断结果
诊断面板展开 / 过滤状态
当前选中对象
当前视图滚动 / 缩放
播放光标位置
最近播放位置
播放进度
Mute / Solo
播放设备选择
Render-Ahead Buffer
Device Buffer Request
设备实际采样率、实际 buffer 与 callback period
本机最近打开路径
本机最近导出路径
最近导出一次性参数
MIDI 导出产物
音频渲染产物
播放预渲染 buffer
canonical compiled result
编译缓存
编译器 fingerprint
C# Mapping Function 编译产物
BASSMIDI Stream 状态
音频设备信息
窗口布局
```
如果未来需要恢复 UI 状态或本机偏好，应作为本机会话状态或用户偏好另行设计，不写入 Project 文件语义。
---
## 16.29 初版不支持内容
初版 `.midora` 文件格式 / 持久化系统不支持：
```text
自动保存
崩溃恢复文件
保存事务失败残留作为崩溃恢复机制
项目模板
跨项目导入 Event Instrument
单独导出 Event Instrument 文件
程序级全局 Event Instrument Library
传统 Save As
用户手工扩展包内容
插件 / modding 扩展文件
加密 Zip / 密码保护
读取未知字段并保留
保存未知文件
保存孤立对象文件
保存编译结果 / 播放缓存 / 导出产物
打开时自动编译 C# Mapping Function
打开时将 SoundFont 加载到 BASSMIDI
```
打开项目只负责：
```text
读取并校验 .midora 包。
反序列化 Project 源数据。
生成内存 Project。
生成打开诊断。
```
C# Mapping Function 编译由后续诊断、编译、播放或导出流程触发。
SoundFont 实际加载由播放、预览或音频渲染触发。
打开时可以检查外部 SF2 文件存在性与 last known hash，但不加载到 BASSMIDI。
---
