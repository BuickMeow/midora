# Midora SRS—代码一致性核对与修正记录

状态：现有代码面的全量核对与直接矛盾修正已完成（2026-08-05）；本状态不表示初版功能已经全部实现或达到发布验收。
上位规范：`misc/Midora-SRS-Initial-Release-v0.1/`。本文件是核对、需求追踪和实现决策记录，不修改 SRS，也不替代 SRS。

核对方法：逐章读取 SRS 00–22，随后检查仓库内全部正式源码、项目文件、自动测试、控制台原型、原生互操作声明和当前 ADR。下文把“现有实现与 SRS 直接相反”与“该模块尚未实现”分开记录。

## 1. 核对范围与 Requirement Trace

- 输入：完整内存 Project、编译上下文、Canonical Compiled Result、播放/预览运行时状态、sample-domain 计划、BASS/BASSMIDI/BASSWASAPI 和 WAVE 输出配置。
- 正式输出：领域源数据；确定性的 Canonical Compiled Result；仅由 canonical 结果派生的播放、预览、MIDI 导出和音频渲染输入。
- 边界：统一使用 `[startTick, endTick)`；稳定 ID 表示身份；Port/Channel 只由编译器分配；实时采样率来自设备实际值，文件采样率为 8,000–192,000 Hz 整数。
- 失败条件：结构/引用/值域/资源/Mapping/范围错误使正式消费者不可消费；无 SF2 只阻止发声消费者；CC91/CC93 在进入 canonical 结果前拒绝。
- 诊断：保持 Error/Warning/Info/Debug 原级别；Warning-as-error 只影响成功判定；`Channel Unit >= 248` 固定为 Info。
- 持久化归属：`.midora` 只保存源数据和 Project 级稳定 ID 计数器；不得保存 Project ID、canonical 结果、缓存、Mute/Solo、播放位置或任务状态。
- 运行时归属：Mute/Solo、设备、buffer、播放状态、sample-domain 缓存和 native handle 均不属于 Project。
- 非目标：MIDI 2.0、传统 MIDI OUT、VST/DAW host、录音、Voice Stealing、多 SoundFont、Pause/Scrub 和多 Project。

## 2. 已由 SRS 闭合、必须直接修正的差异

| 项目 | SRS 依据 | 核对时差异 | 已实施修正 |
| --- | --- | --- | --- |
| Project ID | 16.5.2 | `MidoraProject.Id`、source reference 和 canonical result 错误携带 Project ID | 已删除独立 Project ID 及其 fingerprint 输入 |
| 稳定 ID 分配 | 8.42、16.5.3、16.13.2 | 正式对象默认使用 `Guid.NewGuid()`，不符合 Project 级持久化单调计数器 | 已改为 Project 所有的 128-bit 单调计数器；零保留，不补缺、不复用；文件兼容布局已按决定 3A 固定 |
| Project End Marker 身份 | 4、16.8.2 | 只有 `long? EndMarkerTick`，缺少稳定 ID | 已建模为带稳定 ID 的可编辑事件，移动时保留身份，canonical conductor 保留该身份 |
| Event Instrument 文件夹层级 | 18.8.2–18.8.3 | `ParentFolderId` 允许嵌套 | 已删除父文件夹语义；加入单层名称、保留名、引用校验及删除后移入 Unfiled 的领域操作 |
| Marker 名称 | 18.7.4、20.8.4 | Validator 错误拒绝空名称 | 已允许空名称和重复名称；同 tick 数量存在 SRS 内部冲突，转入待确认项 |
| Logical Track 名称 | 3.7.3、11.2.2、20.8.4 | Validator 错误拒绝空名称/未 trim 名称 | 已允许为空和重复，名称不参与身份 |
| Template Note 边界 | 7.27、8.51 | 只校验 NoteOn 位于 Template，未校验 NoteOff 不越界 | 已把越界源数据诊断为 Error；编辑器未来应先扩展 Template Length |
| Bank Select 结构 | 8.55.2 | 事件强制同时生成 MSB/LSB，不能表达仅一部分存在 | 已分别保存存在性，只映射和输出实际存在的部分，并保持 MSB 先于 LSB |
| 编译用途 | 12.2、14.1.2、15 | `CompilationPurpose` 缺少 MIDI Export 与 Logical Track 音频渲染用途 | 已增加专用用途，避免把播放运行时状态混入成品消费者 |
| partial 语义 | 12.20、13.1 | Range/Preview 成功结果被错误标记为 partial | 已把成功 CompileContext 结果标记为完整可消费；当前失败路径不返回音乐数据，因此也不伪称含 partial 数据；消费者拒绝任何 partial/不可消费结果 |
| canonical 确定性 | 12.21.2、22 INV-002/003 | canonical statistics 含耗时、缓存命中和重编数量，Full/Incremental 形式不可能相同 | 已从 canonical 移除非确定性运行遥测，缓存/重编计数只通过编译器运行遥测暴露 |
| 实时采样率 | 13.14、15.5 | 8,000–192,000 Hz 文件限制被误用于实时设备实际采样率，且预览仍走文件适配器 | 文件适配器保留范围；主播放和全部实时预览均接受设备报告的任意正整数实际采样率 |
| 播放任务状态 | 13 | 零长度范围和 Preparing 异常后可能残留 Active Task | 已保证这些路径释放编辑锁并恢复 `ActiveTaskKind=None` |
| Channel 10 初始化顺序 | 13、音频后端约束 | stream 先设 melodic 再 Reset，Reset 可能覆盖规范初始值 | 已改为每个 Channel 先 Reset、再显式设置 melodic，包括 MIDI Channel 10 |
| 原生资源释放 | 13.19、15、21 | 部分 `StreamFree`/`FontFree`/全局 `Free`/WAVE `CloseHandle` 返回值被忽略，WASAPI 清理错误未被正式后端上报 | 已逐次检查返回值并立即读取线程本地错误码；WASAPI 先建立正确 device context 再清理；正式后端聚合并报告清理失败，finalizer 保持不抛异常 |
| 未决音频参数的“默认值” | 21.3、音频 ADR | 原型参数被命名为 Initial Release Default，且实时后端可隐式采用 | 已改名为明确的 Prototype/Limiter v1 Candidate，并要求实时后端调用方显式传入选项 |
| BASS 本地获取工具 | 21.3、发布约束 | 隐藏选择 win-x64、引用不存在的校验脚本、可无提示下载不固定的当前包 | 已要求显式架构和显式接受“未固定开发候选”，补充 manifest/SHA-256 校验；正式修订与发布 hash 仍待确认 |

## 3. 尚未完成的初版模块（不是“现有代码语义矛盾”）

- Project Metadata、Application Preferences、SoundFont 完整校验/可移植引用及正式应用生命周期尚未实现。
- Event Instrument、Mapping、Lifecycle、Logical Track/Segment 的若干编辑器级数据与命令仍只有核心垂直切片，不是 SRS 07–11 的完整实现。
- `.midora` ZIP/manifest/schema/迁移/安全保存/损坏隔离尚未实现。
- 正式 MIDI Export workflow、SMF Type 1 组织、文件事务和导出报告尚未实现。
- 正式 Audio Render workflow（整曲/分轨、任务快照、取消、结果报告）尚未实现；当前只有底层 renderer 与 WAVE 输出能力。
- WPF UI、导航、编辑器、对话框和 Project 打开/关闭工作流尚未实现。
- SRS 12.21 要求的 Segment checkpoint、输入/输出状态 hash、dirty 传播与收敛停止尚未实现；现有 Track 级缓存只能作为过渡实现，不能标记为初版合规。
- Canonical CompileContext 摘要、失败阶段、完整来源追踪和可选 Debug 诊断仍不完整。
- Native interop 尚未固定支持修订；32/64 位布局、calling convention、版本不匹配、重复 init/free 和泄漏的独立自动化门不完整。
- WASAPI 设备移除/默认设备变化、不同 callback block、deadline/underrun 和约 200 ms 端到端基准仍需要在正式候选硬件矩阵上验收。

## 4. ADR-SRS-AUDIT-001（已接受）：Project 级稳定 ID

SRS 已确定的部分：初版领域对象 ID 至少 128-bit；Project 保存 `nextStableId` 单调计数器。分配返回当前值后递增；零值保留为“未分配”；删除不回退计数器；复制、拆分右侧、新建事件和编辑器新增对象都必须通过所属 Project 分配。

Project 本身没有独立稳定 ID。诊断来源和 canonical result 只携带 Project 内对象 ID 与 tick；编译 fingerprint 只依赖 Project 内容和 CompileContext，不引入运行时随机 Project 身份。

加载器未来必须从 `project.json` 恢复 `nextStableId` 并验证所有现存稳定 ID 非零、全局唯一且小于该计数器。加载损坏文件时不得通过扫描最大 ID、补缺或随机生成来“修复”。新对象图的 ID 分配属于编辑/创建阶段，不得由 Validator 或 Compiler 隐式修改源 Project。

文件兼容布局已按决定 3A 确认：JSON、对象文件名和 `nextStableId` 使用固定 32 位小写十六进制字符串，高 64-bit 在前、低 64-bit 在后；protobuf 使用 `StableId { fixed64 high = 1; fixed64 low = 2; }`，数值组合为 `(high << 64) | low`，wire 字节序服从 protobuf `fixed64` 标准。当前 `Guid` 只是在内存中承载该数值的实现细节，不构成 GUID 文件格式。

## 5. 必须由产品所有者确认、当前不得静默定版的项目

本节只登记 SRS 内部冲突或 SRS/ADR 明确留待决定的选择；SRS 已闭合的规则不列入。

按后续确认顺序登记；已确认项目保留原编号并标记，未标记项目均待确认：

1. **已确认：1A（2026-08-05）**。初版 Envelope Preset 固定为 SRS 10.11 的 ADSR-like 结构；SRS 18.6.5 已修订为服从第 10.11 节，不采用任意有序点/曲线段模型。现有领域模型与编译器实现符合该决定，无需修改代码。
2. **已确认：2A（2026-08-05）**。同一 tick 允许多个普通 Marker；名称可空、可重复，不按名称或 tick 去重，以稳定 ID 区分。SRS 4.8.2 与 18.7.4 已统一；编译器补充稳定 ID 次级排序，避免 canonical 结果依赖源列表顺序。
3. **已确认：3A（2026-08-05）**。JSON、对象文件名和 `nextStableId` 固定使用 32 位小写十六进制字符串；protobuf 固定使用 `StableId.high` / `StableId.low` 两个 `fixed64` 字段，字段号分别为 1 / 2，wire 字节序遵循 protobuf 标准。SRS 2.5、16.11.2、16.13.2 与 21.3 已同步。
4. **已确认：4A（2026-08-05）**。新建 Event Instrument 默认 `Reject`；策略范围内重叠产生 Error，结果不可消费。用户显式选择 `Warn` 时产生 Warning，默认仍可消费；启用“Warning 视为 Error”后结果不可消费，但诊断级别仍是 Warning。SRS 7.33、10.17 与 12.19 已统一。
5. Mapping/曲线到整数 MIDI 值的默认取整：确认 Round/Floor/Ceiling 的默认值及 midpoint 规则；当前候选为 `AwayFromZero`。
6. 曲线离散化与压缩：确认每 tick 采样候选是否定版，或采用误差阈值/自适应采样；该选择影响可听结果、事件量和确定性。
7. 实际无输出的 Event Instrument 是否占用 Channel Unit：SRS 7.31 明确留给编译优化；该选择会影响资源分配和 canonical 形式。
8. tick→sample 整数舍入：当前候选为完整 Tempo Map 的 decimal 积分后只做一次 `AwayFromZero`。
9. Limiter 最终算法：当前候选是 stereo-linked、sample-peak、零 look-ahead、ceiling 1.0、50 ms release。
10. WASAPI 正式输出策略：shared/exclusive、event-driven、格式协商和 period。
11. 内部固定工作 block 与 ring block 数：当前原型使用 256 frames 和有界 ring，但尚无正式基准结论。
12. 正式进程拓扑：进程内合成或单个无 UI 音频子进程；当前两条链都只是评测候选。
13. `BASS_MIDI_NOTEOFF1`：必须基于同音高重叠、Cut、Reset 和配对 NoteOff 的真实测试决定；当前正式候选为关闭。
14. BASSMIDI 性能参数档：interpolation、voice/CPU limiting、sample loading；当前原型为 BASS default/on-demand/不设 voice 与 CPU 上限。
15. 产品 CPU 架构：win-x64、win-x86 或其他明确发布集合；工具已不再暗设 x64。
16. BASS/BASSMIDI/BASSWASAPI 固定修订和每个发布文件的 SHA-256；不得把 vendor `latest` 当正式基线。
17. C# Mapping Function 的持久兼容 ABI：函数签名、允许引用、缓存和 AssemblyLoadContext 卸载策略；“初版不做 sandbox”已由 SRS 9.11.4 确认，不再询问。
18. `.midora` 首版 schema/protobuf 兼容基线：JSON Schema、protobuf 字段号、代码生成方式、文本最大长度和颜色/路径字段表示。
19. Project SoundFont 可移植引用与哈希策略：相对路径规则、大小写/分隔符、同名冲突、哈希算法和计算时机。
20. Project 工程总耗时累计规则：是否统计空闲、最小化、Buffering 等 SRS 明确留给实现的时段。
21. MIDI 导出兼容细节：Tempo 的 microseconds-per-quarter-note 舍入、running status、必要 Meta Event 兼容档和输出命名模板。
22. MIDI/音频输出文件名规则：Unicode normalization、最大长度、非法字符替换、冲突序号和分轨命名模板。

发布前另有一项非技术选择：必须由产品所有者确认并取得适用于实际产品、平台和分发方式的 BASS 商业许可证。技术测试通过不等于具备分发授权。
