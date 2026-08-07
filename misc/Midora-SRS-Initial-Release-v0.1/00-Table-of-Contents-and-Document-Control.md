# Midora Software Requirements Specification — Initial Release Scope

> 中文引用名：**《Midora 软件需求规格说明书（初版范围）》**  
> 日常简称：**《Midora SRS》**  
> 规格版本：**v0.1**  
> 生成日期：**2026-07-15**  
> 最近修订日期：**2026-08-07**
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

## 2026-08-07 修订摘要

- 明确 Segment Editor 钢琴卷帘的两种初版交互预览：左侧 Pitch Ruler 琴键按住预览，以及放置单个 Logical Note 时的草稿音符预览。两者统一复用 held Preview 的因果 Gate、`Int64.MaxValue` 未结束哨兵和未渲染 frontier 生效规则；预览不写入 Project，预览不可用或失败不得阻止合法音符编辑提交。

## 2026-08-06 修订摘要

- Midora 初版产品定位固定为免费、开源、非商业软件，Midora 自有源代码采用根目录 `LICENSE` 中未经自定义修改的标准 MIT License，版权署名为 `Copyright (c) 2026 Midora contributors`；项目自身非商业不限制下游商业使用。该定位不把 BASS/BASSMIDI/BASSWASAPI 纳入 Midora 的开源许可证；正式发布仍须按实际主体、收入方式、平台、分发方式和届时有效条款执行许可核验并提供第三方声明。
- MIDI 导出的 Channel 10 melodic 初始化固定为：每个实际相关事件 Track 在相对 tick 0 按 GS→XG 写入两条 Normal Part SysEx，使用固定默认设备编号，不发送任何 GS/XG/GM Reset，也不改写 canonical Bank/Program；Readme 必须说明不识别 vendor SysEx 的兼容边界。
- MIDI 导出与音频文件渲染统一使用确定性 Windows 安全文件名合法化和冲突检测；精确算法固定为 NFC、固定不安全字符集合、设备保留名前缀 `_`、255 UTF-16 code unit、text-element 安全截断、NFC + OrdinalIgnoreCase 冲突键和稳定 ` (n)` 后缀。任务开始前必须预览并冻结全部最终路径，源名称不被修改，已有目标不参与后缀分配且仍需明确覆盖授权。整曲、分 Track、逐 Port、Readme 与 MIDI Track Name 模板已经固定，多文件模式不自动增加嵌套目录。
- MIDI 导出兼容档固定为 SMF Type 1、无 Running Status、严格 UTF-8 文本 Meta、事件 Track 的 Track Name + MIDI Port 最小组合、CC0→CC32→Program、Time Signature `cc=24` / `bb=8`、Tempo 十进制换算后一次 `AwayFromZero`，并禁止导出器在 canonical 之外追加 Channel 清理；所有 Track 的 EOT 对齐统一 endTick。
- 工程总耗时固定按 Project 成功打开后的完整会话时间累计，包含空闲、最小化、失焦、Buffering、导出和渲染；系统睡眠 / 休眠及关闭流程暂停。当前会话使用单调时钟，自动累计不单独标记 Project Modified，也不影响编译语义。
- Project 外部 SoundFont 固定为项目根目录或直属 `soundfonts/` 的相对 SF2；路径精确大小写优先、唯一 ignore-case 回退并警告、歧义拒绝。原始字节 SHA-256 只在用户明确绑定/接受时更新，被动变化不修改 Project；内嵌资源的 settings/manifest/hash 必须一致。
- 初版持久化兼容基线固定为 JSON Schema Draft 2020-12、内部版本化 `System.Text.Json` source-generated DTO、protobuf Edition 2024、Google.Protobuf 3.35.1 与 Grpc.Tools 2.83.0；严格拒绝重复/未知 JSON 属性和未知 protobuf tag，已发布 schema 以 descriptor/golden bytes 锁定。
- 持久化文本、相对路径、opaque sRGB 颜色、UTC 时间和工程总耗时的 v1 表示已固定；路径保留大小写与原 Unicode，不做 normalization，时间使用七位小数秒 UTC `Z` 格式，总耗时使用非负 int64 毫秒。
- 初版 C# Mapping 固定 ABI v1：版本化函数体、只读独立 MappingContext 契约、C# 14/`Microsoft.NETCore.App.Ref 10.0.10`，以及按 Project 当前源码修订管理的 collectible AssemblyLoadContext 缓存；编译产物不持久化，引用白名单不构成 sandbox。
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
- 稳定 ID 文件兼容布局固定为 32 位小写十六进制 JSON/文件名表示，以及 protobuf `StableId { fixed64 high = 1; fixed64 low = 2; }`；`fixed64` wire 字节序遵循 protobuf 标准。
- 同一 tick 允许多个普通 Marker；它们不按名称或 tick 去重，以稳定 ID 区分，并在 canonical 结果中按稳定 ID 确定同 tick 顺序。
- 初版 Envelope Preset 统一为第 10.11 节规定的固定 ADSR-like 结构；第 18.6.5 节编辑器不得扩展为任意有序点或曲线段模型。
- `Channel Unit >= 248` 的诊断级别统一为 `Info`，不受“Warning 视为 Error”策略影响。
- Segment Split 必须为右侧 Segment 保留或生成必要参数起点状态，维持参数状态及相关曲线在分割前后的听感；不改变跨分割点 Logical Note 的提前结束规则。
- 正式 BASSMIDI 后端启用 `BASS_MIDI_NOFX`，初版不支持 Reverb / Chorus，也不允许 CC91 / CC93。
- 正式 BASSMIDI 后端启用 `BASS_MIDI_NOTEOFF1`；同 Port、Channel、pitch 的重叠 Note 实例按 FIFO 逐个释放，Cut Previous 的释放重叠与硬边界 Reset 必须保持精确 NoteOff 配对。
- 正式 BASSMIDI Stream 固定使用 8-point sinc 和 CPU 属性 `0`，并在 Preparing 预加载计划引用的 SF2 presets；实时与离线 sample voice 上限分别配置，默认均为每 Stream `750`，同一任务所有 Port 使用同一值。
- 实时播放跟随所选输出设备的实际采样率；音频文件渲染使用用户选择的 `8,000–192,000 Hz` 整数采样率。
- 音频文件输出改为普通 RIFF/WAVE、Stereo、Interleaved IEEE 32-bit Float；超过 RIFF 大小上限时在 Preparing 阶段失败。
- 增加启用输出设备枚举、可调 buffer、约 200 ms 性能基准、音频活动线程零托管分配和固定内部音频子进程要求。
- 性能选择在满足正确性、确定性和资源上限的前提下优先时间性能，可用受控内存换取速度。

## 文件命名规则

文件名前两位数字是稳定阅读顺序。章节内标题采用 `章.节.小节` 编号，可用于 Issue、ADR、测试和代码评审引用。
