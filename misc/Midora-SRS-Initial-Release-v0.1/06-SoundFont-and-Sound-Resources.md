# 第 6 章 SoundFont 与声音资源

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义程序级 SoundFont 列表、BASSMIDI 直接读取、无可用 SoundFont 状态，以及 SoundFont 与 Project、编译、缓存和消费者的边界。

## 6.1 系统归属

SoundFont 是 Application Preferences，而不是 Project Source Data。

`.midora` 不得保存：

```text
SF2 文件字节
内嵌 SF2 资源
外部 SF2 引用
本机 SF2 绝对或相对路径
SF2 文件名、大小、SHA-256 或其他内容身份
SoundFont 列表顺序和启用状态
```

Project Domain、Compiler 与 Canonical Compiled Result 不得包含、解析或验证 SoundFont。无 SoundFont 不影响 Project 新建、打开、保存、编译和 MIDI 导入/导出。

## 6.2 程序级有序列表

Application Preferences 必须保存一个有序 SoundFont 列表。每项只包含：

```text
本机 SF2 绝对路径
Enabled / Disabled
```

列表提供新增、删除、启用/禁用、上移和下移。不得保存重复的、按 Windows 路径语义指向同一位置的项。

列表从上到下是正式 BASSMIDI SoundFont 优先顺序。所有实时播放、全部预览和全部离线音频渲染使用同一次任务开始时冻结的相同启用列表和顺序；不得按 Project、Port、Track、Usage、Root 或 Event Instrument 选择不同列表。

Disabled 项保留在程序设置中，但不传给音频 Worker、不参与缓存身份，也不影响声音。

## 6.3 文件选择与校验边界

程序设置只验证路径字段的结构：必须是本机完全限定路径且扩展名为 `.sf2`。设置保存阶段不得：

```text
复制 SF2
读取完整 SF2
计算 SF2 内容 SHA-256
解析 SF2 内容
调用 BASS/BASSMIDI 验证可加载性
建立临时 SF2 副本
```

文件选择器可以要求选择时文件存在，但这不构成持久身份或后续内容校验。路径在任务开始时缺失、不可读或无法由 BASSMIDI 加载时，该音频任务在 Preparing 失败并报告具体路径；不得因此修改 Application Preferences 或 Project。

Midora 不监控并自动接受运行中被替换的 SF2。修改 SoundFont 列表、顺序或 Enabled 状态只允许在播放停止且没有前台任务时提交；提交后必须销毁现有持久音频 Worker，并使依赖旧列表的 sample-domain 缓存代际失效。若用户在 Midora 外替换文件，应显式重新应用设置或 Reset Playback Engine。

## 6.4 BASSMIDI 直接读取

音频 Worker 必须把每个启用项的原始绝对路径直接传给 `BASS_MIDI_FontInit`，固定使用 `BASS_MIDI_FONT_MMAP`。不得为了播放、预览或音频渲染把 SF2 复制到 Project、缓存目录或临时目录。

持久实时 Worker 在启动时按列表顺序创建并持有全部 Font handle；同一 Worker 内的 Pitch Audition、Held Preview、普通 Preview 和主播放共用该冻结列表。列表变化时销毁并重建 Worker，不在活动任务中热替换 handle。

每个实际 Unit 的 1-channel BASSMIDI Stream 必须通过一次 `BASS_MIDI_StreamSetFonts` 接收完整有序 Font handle 列表。Preparing 从正式计划收集引用的 Bank/Program，并对每个 Font handle执行所需 `BASS_MIDI_FontLoad`；缺失 preset 继续服从 BASSMIDI fallback，不成为 Project/Compiler Error。

离线音频渲染同样直接读取冻结的原路径列表，不建立“冻结 SF2 文件副本”。任务期间文件被外部修改、删除或变得不可读造成的失败属于外部资源竞争；已有输出仍服从临时文件—校验—原子发布事务。

## 6.5 可用性

至少一个 Enabled 项时，播放、预览和音频渲染入口可以启动 Preparing。列表为空或所有项 Disabled 时：

```text
Project 编辑、保存、编译、MIDI 导入和 MIDI 导出可用
播放、Pitch Audition、Held Preview 和音频渲染不可用
状态栏显示 No Enabled SoundFonts
不产生编译诊断
```

入口可用不代表路径一定可加载；路径和 BASS 加载错误在音频 Preparing 阶段报告。

## 6.6 缓存身份

缓存不得重新读取完整 SF2 或计算 SF2 内容哈希。每次应用程序设置或建立音频任务快照时，可从以下冻结信息生成确定的运行时缓存指纹：

```text
启用项的有序规范绝对路径
任务开始时可取得的文件长度
任务开始时可取得的 UTC 最后修改时间
```

该指纹可以使用 SHA-256 编码上述小型描述符，但它不是 SF2 内容校验，不得命名或展示为 SoundFont SHA-256。任一启用路径缺失或元数据不可读时不得建立可复用 PCM 命中；任务随后按正常 BASS Preparing 失败。

路径、顺序、Enabled、长度或修改时间变化必须使相关 Unit PCM 和 playback-span 缓存失效。缓存命中不能证明 SF2 内容未变，不能替代 BASS 打开文件。

## 6.7 与 MIDI 和编译的关系

SoundFont 不改变：

```text
Event Instrument / Mapping / Lifecycle
Logical / Pure MIDI Track 与 Segment
Canonical MIDI 事件
Port / Channel / Unit 分配
MIDI 导入/导出
Program/Bank/CC/RPN/NRPN 语义
```

Midora 不假设 SoundFont 符合 GM，不解析 preset 名称，也不根据当前列表验证 Program/Bank 是否存在。Program Change 与 Bank Select 始终是 Project MIDI 语义；列表变化不得改写它们。

MIDI Export Readme 不记录程序级 SoundFont 路径、名称、列表或可用状态。MIDI 产物不依赖当前机器的 SoundFont 设置。

## 6.8 UI 与诊断

Application Preferences 的 SoundFonts 区域必须使用暗色基线样式展示有序列表。每项显示文件名和完整路径，并提供 Enabled 控件；新增使用多选 SF2 文件选择器，删除和排序只修改 Draft，用户按 Apply 后一次性提交。

Apply/Cancel 继续服从程序设置事务：Apply 成功后保存完整列表并重建音频服务；Cancel 不改变设置、Worker 或缓存。

以下属于任务级 Error，不进入 Compiler Diagnostics：

```text
没有 Enabled SoundFont
启用路径缺失或不可读
BASS_MIDI_FontInit / FontLoad / StreamSetFonts 失败
音频任务期间文件被外部替换或移除导致的读取失败
```

状态栏只展示程序级概括，例如 `2 SoundFonts Enabled` 或 `No Enabled SoundFonts`，不得把它描述为 Project SoundFont。

## 6.9 初版范围

初版只支持本机 `.sf2` 文件和一个全局有序列表。不支持 SFZ、DLS、VST、网络路径、Project 内嵌资源、Project 相对引用、每 Project 列表、每 Port/Track/Instrument 独立列表或运行中热切换。

SoundFont 授权仍由用户和分发者负责。Midora 仓库和 `.midora` 项目不包含用户 SF2 字节；这不构成对第三方 SoundFont 使用或分发权利的判断。
