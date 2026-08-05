# Midora Software Requirements Specification — Initial Release Scope

> 中文引用名：**《Midora 软件需求规格说明书（初版范围）》**  
> 日常简称：**《Midora SRS》**  
> 规格版本：**v0.1**  
> 生成日期：**2026-07-15**  
> 最近修订日期：**2026-08-05**  
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
- 实时播放跟随所选输出设备的实际采样率；音频文件渲染使用用户选择的 `8,000–192,000 Hz` 整数采样率。
- 音频文件输出改为普通 RIFF/WAVE、Stereo、Interleaved IEEE 32-bit Float；超过 RIFF 大小上限时在 Preparing 阶段失败。
- 增加启用输出设备枚举、可调 buffer、约 200 ms 性能基准、音频活动线程零托管分配和固定内部音频子进程要求。
- 性能选择在满足正确性、确定性和资源上限的前提下优先时间性能，可用受控内存换取速度。

## 文件命名规则

文件名前两位数字是稳定阅读顺序。章节内标题采用 `章.节.小节` 编号，可用于 Issue、ADR、测试和代码评审引用。
