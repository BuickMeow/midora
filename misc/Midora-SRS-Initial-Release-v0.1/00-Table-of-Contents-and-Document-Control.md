# Midora Software Requirements Specification — Initial Release Scope

> 中文引用名：**《Midora 软件需求规格说明书（初版范围）》**  
> 日常简称：**《Midora SRS》**  
> 规格版本：**v0.1**  
> 生成日期：**2026-07-15**  
> 最近修订日期：**2026-08-16**
> 文档形态：**按章节拆分的 Markdown 规格书**

## 文档定位

本规格定义 Midora 初版的产品范围、领域语义、编译与输出规则、持久化格式、UI 工作流、错误边界和明确不支持内容。它是初版实现、测试、评审和需求变更的产品基线。

本规格不固定最终 C# 类型、具体算法、第三方 API 调用、线程模型或控件实现；这些实现选择必须满足本规格的外部语义和不变量。

## 阅读顺序

首次阅读建议按章节顺序进行。开发中可通过第 22 章快速定位主题和跨系统不变量。

## 目录

1. [产品范围、定位与总体目标](01-Product-Scope-and-Positioning.md)
2. [系统模型、术语与符合性约定](02-System-Model-Terms-and-Conformance.md)
3. [Project 模型与应用生命周期](03-Project-Model-and-Application-Lifecycle.md)
4. [时间、Conductor Track 与全局音乐事件](04-Time-Conductor-and-Global-Musical-Events.md)
5. [Port、Channel 与资源模型](05-Port-Channel-and-Resource-Model.md)
6. [SoundFont 与声音资源](06-SoundFont-and-Sound-Resources.md)
7. [Event Instrument Library 与 Event Instrument 定义](07-Event-Instrument-Library-and-Definition.md)
8. [SubVoice 与 MIDI 事件编辑](08-SubVoice-and-MIDI-Event-Editing.md)
9. [曲线、Logical Parameter 与映射](09-Curves-Logical-Parameters-and-Mapping.md)
10. [实例生命周期、Loop、Envelope 与重叠](10-Instance-Lifecycle-Loop-Envelope-and-Overlap.md)
11. [Logical Track、Segment 与编曲语义](11-Logical-Tracks-Segments-and-Arrangement-Semantics.md)
12. [编译系统与 Canonical Compiled Result](12-Compilation-and-Canonical-Compiled-Result.md)
13. [播放与预览](13-Playback-and-Preview.md)
14. [MIDI 导出](14-MIDI-Export.md)
15. [音频文件渲染](15-Audio-File-Rendering.md)
16. [.midora 文件格式与持久化](16-Midora-File-Format-and-Persistence.md)
17. [UI 框架、导航与全局界面](17-UI-Framework-Navigation-and-Global-Surfaces.md)
18. [编辑工作区与编辑器](18-Editing-Workspaces-and-Editors.md)
19. [Project、文件、输出与任务工作流](19-Project-File-Output-and-Task-Workflows.md)
20. [通用交互、验证与 UI 验收边界](20-Common-Interaction-Validation-and-UI-Acceptance.md)
21. [初版范围边界、实现自由度与变更控制](21-Initial-Release-Scope-Boundaries-and-Change-Control.md)
22. [主题索引与跨系统不变量](22-Requirement-Locator-and-Cross-System-Invariants.md)

## 文档版本规则

- **Initial Release Scope** 表示产品范围，不表示文档草稿序号。
- `v0.x` 表示整合和审查阶段；成为正式开发基线后可升级为 `v1.0`。
- 后续修订必须说明受影响章节，避免在实现中静默改变需求。

## 2026-08-16 修订摘要

- SubVoice 的 Note Number / Velocity Mapping 保持强制共享目标；非 Note Event Mapping 与 Logical Parameter Mapping 改为可物理删除的可选 owner。非 Note 事件点继续存在时，缺少 Mapping 表示原始值直通，普通事件编辑不得静默重建已删除 Mapping。
- 非 Note 状态目标上的 Envelope Mapping 以最近原始事件值或有效 Initial State/default 为持有基值，并按实例/Release 的整数 tick 连续求值；非零 Release 的最后一个有效 tick 达到 End Value 后才进入 NoteOff/Reset。
- Loop Start 前开始并完整跨越 Loop End 的模板 Note 在循环中保持发声且不重触发；普通 Gate/Release/Tail 结束不发送 CC120，CC120 只用于 Segment End 等明确硬边界。
- 修正普通 instance 结束后的 SoundFont release：发声 Segment 的 Unit lane 从首次使用持续保留到 Segment End；普通 NoteOff 后的原生 release 继续进入 Segment PCM，只有 Segment End/消费者范围硬边界可以硬裁剪。自然编译范围按实际生成实例所属 Segment End 结束。
- MIDI Track 0 的 Track Name 改为任务准备时冻结的 Project Name，空白防御性输入回退为 `Conductor`；`Conductor` 仍是 Track 0 的结构角色名称。

## 2026-08-15 修订摘要

- MIDI 导出事件 Track 的组织从 `Logical Track × Port` 改为严格的 `Channel Unit (Port + Channel) × 1 MIDI Track`。同一文件内一个实际有事件的 Unit 只出现一次，每个事件 Track 只含一个 Channel；按原始 Port→Channel 排序，Track Name 固定显示一基 `Port <P> / Channel <C>`。该变化用于兼容不支持单 Track 多 Channel 的 MIDI 编辑器，不改变 canonical 路由或事件语义。

## 2026-08-12 修订摘要

- Arrangement 新建 Project / 重置编辑器的默认可见 Grid 改为 `Bar`、Snap 操作粒度改为 `1/8`；Bar Grid 按完整 Time Signature Map 以主实线绘制小节边界、以更浅的低强调实线绘制分母拍内部边界。Segment/SubVoice 钢琴卷帘不采用该拍内辅助线增强。
- Arrangement Draw 空白放置改为按下并向右拖动确定 Segment 长度，单击使用默认长度；Arrangement 默认 Segment 长度固定为 `1 × TPQ`，相邻 Segment 仍按可用间隙缩短或拒绝。
- Arrangement Track Header 增加独立 hover / pressed、拖动重排、Rename / Bind / Unbind / Delete / Move Up / Down 菜单、绑定乐器次级标签，以及从 Event Instrument Library 拖放绑定；已有不同绑定必须确认 rebind。
- 普通 Logical / Template Note 多选移动使用共同 pitch delta，并删除结果 pitch 越出 `0..127` 的个别 Note；移动与删除属于一个 Undo。Note `Ctrl+Drag` 复制仍使用整组共同 clamp，不生成部分副本。
- 正式 Compiler Diagnostic message 统一为英文；Project Panel 的 Error / Warning 计数在每次编译完成时同步刷新。非法 pitch 来源的诊断导航使用安全 lane 投影，不得使应用崩溃。
- Velocity 视图改为每个 Note 在 start tick 对应一根固定窄柱，柱宽不再表达 Note 长度；顶部使用较大的方形 onset marker，同 tick 多音按高 pitch 覆盖低 pitch。
- Velocity 自由绘制与直线插值手势在按住期间只显示轻量轨迹，不逐柱重绘或提交；松开时一次性计算、提交并刷新 tile。直接按住单柱或其 marker 上下拖动仍只调整该 Note，并且不显示轨迹。
- `Alt + Left Drag` 统一为强制替代手势：Draw 模式的 Segment / Logical Note / Template Note 无视边界命中并强制 Move，`Ctrl + Alt` 强制 Copy+Move；Velocity 无视柱体 direct hit 并强制自由轨迹。操作类型在 Pointer Down 时冻结，Alt 不再绕过 Snap；已消费的 Alt KeyUp 不再激活主菜单并恢复来源 Timeline 焦点，普通 Alt 与 `Alt+F4` 不变。Draw 模式悬停可直接编辑对象时始终显示低强调 transient 外轮廓，不失效 raster tile。Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 的 Select 模式仍从单次左键按下点发起框选，不再以单独左键点击命中对象。

## 2026-08-10 修订摘要

- 明确 Arrangement、Segment Piano Roll 与 SubVoice Piano Roll 的 Draw / Select / Split / Erase 互斥工具状态、直接编辑边界、对象命中指针以及移动/Resize transient 预览；Select 不再直接移动、Resize 或双击创建 Segment / Note。
- 将无修饰键 `D` / `S` / `E` 固定为活动 Timeline Workspace 的 Draw / Select / Erase 快捷键，并明确文本、代码、ComboBox、菜单、Popup、内联编辑和 Modal 的焦点例外；其余单字母工具快捷键仍不注册。
- 补充 Grid / Snap 显示同步、ComboBox 可编辑文本与 Fluent 下拉图标居中、显式垂直 ScrollBar、Diagnostics 筛选框和 Segment 标题布局验收；固定 Arrangement / Segment Piano Roll / Velocity 的蓝灰色层级与 Velocity onset marker、Segment/SubVoice 真实黑白 Pitch Ruler 与逐八度 C 标签、空 Timeline 无覆盖卡片，以及可复制的 Status Error 详情入口。
- 明确 Arrangement Segment Note Preview 使用固定 MIDI pitch `0..127`、最小 1 px Note 高度、布局取整和按 Segment 稳定 ID/内容指纹复用的手工渲染缓存；tick 0 Note 不得遗漏。
- 明确 Velocity 普通点击、自由拖动、右键直线插值、选择集过滤和单柱顶部边缘调整语义；所有手势在按下时立即生效并保持一次手势一次 Undo。
- 明确 Piano Roll 与数值 Lane 的纵向视口边界、右侧滚动条、随平移更新的标尺和边界标签可见性；显式滚动条 Thumb 按实际可见范围计算。Segment 下部编辑区的显隐与高度属于 Project Session UI State，其分隔条在完整上部 Timeline 区域与下部编辑区之间调整高度。
- 明确 Draw 模式下 Arrangement Segment、Segment Logical Note 与 SubVoice Template Note 的主体 `Ctrl+Drag` 使用原子“复制并拖拽”语义；复制集使用共同 delta、生成新稳定 ID、成功后只选择副本并只形成一个 Undo，普通 `Ctrl+Click` 与边缘 Resize 语义不变。
- 补充 Timeline 工具栏、状态栏已读、选择边框、键位明暗、Disabled Ghost Button 和顶部 Transport 信息的 WPF 验收要求。

## 2026-08-08 修订摘要

- 解决整数 Project tick 与 `Bar:Beat:Tick` 在部分 TPQ/拍号组合下不可逆的冲突：开发期 v1 中每个 Time Signature 必须满足 `4 × TPQ % denominator == 0`。Domain 创建/编辑、语义验证和持久化统一拒绝不兼容组合，不创建 v2 或迁移器。
- Time Signature 变化 tick 立即成为新小节 Beat 1；若该 tick 不是旧拍号下的自然小节边界，旧小节被截断并产生 Warning。统一的 Project Time Signature Map 负责可逆 `Bar:Beat:Tick`、自然小节/拍网格和 Snap 基础。
- Q-NUI-034～042 固定五层缓存：canonical range、Segment/Unit fragment、Unit raw PCM、playback span、短 Render-Ahead ring。相同完整 key 的 exact replay 命中不得重复语义编译或 BASSMIDI 合成；缓存只在 Project session 内有效，不进入 `.midora`。
- underrun 在失败位置锁存，按自然小节准备连续恢复区间；小节中途为“当前剩余 + 下一完整小节”，小节起点为当前完整小节，并以播放终点和 16 个四分音符裁剪。完整区间准备好之前不得短块断续推进。
- 音频在 canonical 成功后按抽象 Unit 使用 1-channel BASSMIDI Stream 语义和有界 stream pool。Realtime/Offline Maximum Sample Voices per Unit Stream 分别保存，默认均由 750 改为 500。
- Application Preferences 增加 session 音频缓存 root（默认 `%LOCALAPPDATA%\Midora\AudioCache`）和 reusable byte quota（默认 16 GiB，可为 0）。Transient recovery spool 独立于 quota；spool/RAM 均不可用时受控 Stop。

## 2026-08-07 修订摘要

- 明确 Segment Editor 钢琴卷帘的两种初版交互预览：左侧 Pitch Ruler 琴键按住预览，以及放置单个 Logical Note 时的草稿音符预览。两者统一复用 held Preview 的因果 Gate、`Int64.MaxValue` 未结束哨兵和未渲染 frontier 生效规则；预览不写入 Project，预览不可用或失败不得阻止合法音符编辑提交。
- Project 内稳定 ID 的核心与分配器统一改为单个正 `long`，合法范围为 `1..long.MaxValue`；JSON 使用 canonical 十进制 integer，对象文件名使用无符号、无前导零的十进制 ASCII，protobuf 在各既有外层字段号上直接使用标量 `int64`。这是尚未冻结的开发期 v1 直接修订，不提供旧 128-bit 布局迁移器。
- C# Mapping 内部 ABI 升级为 v2：签名改为 `double Transform(double value, in MappingContextV2 context)`，`MappingStableIdV2` 只承载单个正 `long`；语言、引用面、确定性编译、collectible AssemblyLoadContext 和非 sandbox 边界保持不变。

## 2026-08-06 修订摘要

- Midora 初版产品定位固定为免费、开源、非商业软件，Midora 自有源代码采用根目录 `LICENSE` 中未经自定义修改的标准 MIT License，版权署名为 `Copyright (c) 2026 Midora contributors`；项目自身非商业不限制下游商业使用。该定位不把 BASS/BASSMIDI/BASSWASAPI 纳入 Midora 的开源许可证；正式发布仍须按实际主体、收入方式、平台、分发方式和届时有效条款执行许可核验并提供第三方声明。
- MIDI 导出的 Channel 10 melodic 初始化固定为：每个实际相关事件 Track 在相对 tick 0 按 GS→XG 写入两条 Normal Part SysEx，使用固定默认设备编号，不发送任何 GS/XG/GM Reset，也不改写 canonical Bank/Program；Readme 必须说明不识别 vendor SysEx 的兼容边界。
- MIDI 导出与音频文件渲染统一使用确定性 Windows 安全文件名合法化和冲突检测；精确算法固定为 NFC、固定不安全字符集合、设备保留名前缀 `_`、255 UTF-16 code unit、text-element 安全截断、NFC + OrdinalIgnoreCase 冲突键和稳定 ` (n)` 后缀。任务开始前必须预览并冻结全部最终路径，源名称不被修改，已有目标不参与后缀分配且仍需明确覆盖授权。整曲、分 Track、逐 Port、Readme 与 MIDI Track Name 模板已经固定，多文件模式不自动增加嵌套目录。
- MIDI 导出兼容档固定为 SMF Type 1、无 Running Status、严格 UTF-8 文本 Meta、事件 Track 的 Track Name + MIDI Port 最小组合、CC0→CC32→Program、Time Signature `cc=24` / `bb=8`、Tempo 十进制换算后一次 `AwayFromZero`，并禁止导出器在 canonical 之外追加 Channel 清理；所有 Track 的 EOT 对齐统一 endTick。
- 工程总耗时固定按 Project 成功打开后的完整会话时间累计，包含空闲、最小化、失焦、Buffering、导出和渲染；系统睡眠 / 休眠及关闭流程暂停。当前会话使用单调时钟，自动累计不单独标记 Project Modified，也不影响编译语义。
- Project 外部 SoundFont 固定为项目根目录或直属 `soundfonts/` 的相对 SF2；路径精确大小写优先、唯一 ignore-case 回退并警告、歧义拒绝。原始字节 SHA-256 只在用户明确绑定/接受时更新，被动变化不修改 Project；内嵌资源的 settings/manifest/hash 必须一致。
- 初版持久化兼容基线固定为 JSON Schema Draft 2020-12、内部版本化 `System.Text.Json` source-generated DTO、protobuf Edition 2024、Google.Protobuf 3.35.1 与 Grpc.Tools 2.83.0；严格拒绝重复/未知 JSON 属性和未知 protobuf tag，已发布 schema 以 descriptor/golden bytes 锁定。
- 持久化文本、相对路径、opaque sRGB 颜色、UTC 时间和工程总耗时的 v1 表示已固定；路径保留大小写与原 Unicode，不做 normalization，时间使用七位小数秒 UTC `Z` 格式，总耗时使用非负 int64 毫秒。
- 初版 C# Mapping 最初采用 ABI v1；该内部 ABI 已于 2026-08-07 因稳定 ID 改为单 `long` 而升级为 ABI v2，当前有效规则见同日修订摘要。版本化函数体、只读独立 MappingContext 契约、C# 14/`Microsoft.NETCore.App.Ref 10.0.10`，以及按 Project 当前源码修订管理的 collectible AssemblyLoadContext 缓存等边界保持不变。
- 初版产品发布架构固定为 `win-x64`；主应用、Native AOT 音频子进程和三项 BASS 原生库必须同为 x64，不发布 x86、Arm64 或 AnyCPU 正式产物。
- 初版正式原生基线固定为 BASS 2.4.18.3、BASSMIDI 2.4.16.0、BASSWASAPI 2.4.4.1 及三项 win-x64 DLL 的明确 SHA-256；仓库只保存 manifest，正式构建由操作员提供并校验二进制，vendor current/latest 只能生成开发候选。

## 2026-08-05 修订摘要

- 初版正式实时音频工作 block 固定为最多 `256 frames`；音频子进程内 Render-Ahead 使用单个有界 SPSC PCM ring，容量按 `ceil(actualSampleRate × RenderAheadMilliseconds / 1000)` 计算，不固定 ring block 数；实时 PCM 不跨进程。
- 初版 WASAPI 输出固定为 Shared Mode、event-driven、stereo interleaved float32；采样率采用端点初始化后的实际混音采样率，buffer 请求不等于实际值，period 与 callback frame 数由设备决定。
- 初版 Limiter 版本 1 固定为 stereo-linked sample-peak 算法：瞬时 attack、zero-look-ahead、线性 ceiling `1.0`、50 ms 单极指数 release；实时与离线输出共用逐样本状态语义。
- tick→sample frame 固定为完整 Tempo Map 的 decimal 区间积分乘采样率后只执行一次 `AwayFromZero`；实时播放、预览与音频渲染共用该语义，不得逐 Tempo 段取整。
- 连续值源离散化固定以每个整数 tick 的最终目标值为参考语义；canonical 输出首次有效值及后续整数变化值，任何跳跃求值或缓存优化都必须与逐 tick 参考结果完全一致。
- 整数目标参数默认使用 `Round / Away From Zero`，只在完整映射链最终输出时取整一次；取整与最终越界策略属于目标参数，不属于 Mapping Step。
- 新建 Event Instrument 的 Overlap 策略固定默认为 `Reject`；`Reject` 重叠产生 Error，`Warn` 重叠产生 Warning，并服从全局“强制 Warning 导致编译失败”策略而不改变诊断级别。
- 稳定 ID 文件布局最初采用 32 位小写十六进制文本和 protobuf high/low；该开发期布局已于 2026-08-07 在首版冻结前由单 `long` 契约直接取代，不构成已发布兼容承诺。
- 同一 tick 允许多个普通 Marker；它们不按名称或 tick 去重，以稳定 ID 区分，并在 canonical 结果中按稳定 ID 确定同 tick 顺序。
- 初版 Envelope Preset 统一为第 10.11 节规定的固定 ADSR-like 结构；第 18.6.5 节编辑器不得扩展为任意有序点或曲线段模型。
- `Channel Unit >= 248` 的诊断级别统一为 `Info`，不受“Warning 视为 Error”策略影响。
- Segment Split 必须为右侧 Segment 保留或生成必要参数起点状态，维持参数状态及相关曲线在分割前后的听感；不改变跨分割点 Logical Note 的提前结束规则。
- 正式 BASSMIDI 后端启用 `BASS_MIDI_NOFX`，初版不支持 Reverb / Chorus，也不允许 CC91 / CC93。
- 正式 BASSMIDI 后端启用 `BASS_MIDI_NOTEOFF1`；同 Port、Channel、pitch 的重叠 Note 实例按 FIFO 逐个释放，Cut Previous 的释放重叠与硬边界 Reset 必须保持精确 NoteOff 配对。
- 正式 BASSMIDI Stream 固定使用 8-point sinc 和 CPU 属性 `0`，并在 Preparing 预加载计划引用的 SF2 presets；Realtime/Offline Maximum Sample Voices per Unit Stream 分别配置，默认均为 `500`，同一任务所有 Unit Stream 使用同一冻结值。
- 实时播放跟随所选输出设备的实际采样率；音频文件渲染使用用户选择的 `8,000–192,000 Hz` 整数采样率。
- 音频文件输出改为普通 RIFF/WAVE、Stereo、Interleaved IEEE 32-bit Float；超过 RIFF 大小上限时在 Preparing 阶段失败。
- 增加启用输出设备枚举、可调 buffer、约 200 ms 性能基准、音频活动线程零托管分配和固定内部音频子进程要求。
- 性能选择在满足正确性、确定性和资源上限的前提下优先时间性能，可用受控内存换取速度。

## 文件命名规则

文件名前两位数字是稳定阅读顺序。章节内标题采用 `章.节.小节` 编号，可用于 Issue、ADR、测试和代码评审引用。
