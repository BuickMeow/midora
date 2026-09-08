# 内存优化：操作覆盖索引与阶段基线

日期：2026-09-08。配套 [六阶段实施 / 验收计划](Midora-Memory-Optimization-Execution-and-Acceptance-Plan-2026-09-08.md)。本文件是工程执行索引，不修改 SRS，不表示列出的每种大样本或 UI 场景本轮均已测量。

## 1. 使用方式

每一阶段复用本表，仅重跑受影响链路及其公共回归门；阶段 6 再串联全部操作。结果记录在各阶段报告，并关联 TRX / 探针原始输出。不得把某个通用测试套件通过写成该操作所有对象类型、规模、入口和 UI 效果已经验收。

符号：N = MIDI / Logical / SubVoice Note；P = Channel / Logical Parameter / SubVoice Event point；S = MIDI / Logical Segment。实际适用对象仍以 SRS / 现有命令为准。不适用组合不新增能力。

正常结果必须校验字段、稳定 ID、正式序列与碰撞；历史校验提交一次、Undo / Redo、选择恢复与分支；长任务校验准备、排序 / spill、提交前取消、异常清理，不只看操作返回。持久化要覆盖改完后 Save / Save Copy / Open，而非只在 Undo 回原状后保存。

## 2. 操作与已有自动入口

以下测试名指同名 `*Tests.cs`；用 `rg --files src` 定位。具体本轮运行范围与 opt-in 大样本是否启用，必须以阶段报告为准。

| 编号 | 对象 / 入口 / 操作 | 必须保持的行为 | 自动入口 / 补测方向 |
| --- | --- | --- | --- |
| O01 | N/P/S 单选、框选、Ctrl/Shift、全选、取消 | compressed selection、范围边界、视觉与逻辑同选区 | TimelineObjectSelectionTests、WorkspaceSelectionScalabilityTests、SelectionUiFailureReproductionTests |
| O02 | 对象列表单 / 混合选择 | 双向选择、类型分组操作、定位与 Lane | TimelineObjectListIntegrationTests、TimelineTypedSelectionSourceMappingTests |
| O03 | Read Selection、Scale 准备、Read Properties | owner hint / revision 精确；不能每字段扫描全源 | ProjectTimelineSelectionReaderTests、SelectionReadOptimizationTests、RealMidiSelectionReadPerformanceTests（显式 opt-in） |
| O04 | N/P 创建与删除 | Note later-loses、Event later-wins，仅处理命中键 | BoundedDirectMidiNoteEditTests、BoundedDirectMidiEventEditTests、BoundedPointCommandTests |
| O05 | N/P/S Move；Draw / Select 浮动工具 | 位置 / 值 / owner / 边界与真实选择同步 | ProjectBatchTimelineEditCommandsTests、BoundedLogicalNoteCommandTests、WpfInteractionRegressionTests |
| O06 | Ctrl 复制拖动、Alt 与 Shift 修饰 | 新 ID、复制后选择、固定 tick / key、精确碰撞 | ProjectObjectClipboardPureMidiTests、Stage4DesktopSelectionIntegrationTests、TimelineRenderingTests |
| O07 | N/S 左边界 Resize | 隐藏数据、起点与长度、最小值、Undo | BoundedDirectMidiSegmentTransformTests、BoundedLogicalNoteCommandTests、TimelineRenderingTests |
| O08 | N/S 右边界 Resize | gate、边界、选择全体；不改变其他字段 | BoundedDirectMidiNoteEditTests、BoundedLogicalNoteCommandTests、TimelineRenderingTests |
| O09 | Snap / 无 Snap、创建 delta、视图外拖动 | 时间 / 值取整与原有创建语义 | TimelineNavigationTests、TimelineEventContextGestureTests、TimelineValueTraceSamplerTests |
| O10 | N/P 水平翻转 | 正式范围、同键碰撞、原有 endpoint order | ProjectSelectionTransformEditCommandsTests、ProjectAdvancedTimelineEditCommandsTests |
| O11 | S 仅内容 / 连同窗口水平翻转 | 只影响暴露对象，保留隐藏内容 | BoundedDirectMidiSegmentTransformTests、BoundedLogicalNoteCommandTests、ProjectMultiOwnerAdvancedEditCommandsTests |
| O12 | N/S 垂直翻转 | key 映射、隐藏对象及参数不被误动 | ProjectAdvancedTimelineEditCommandsTests、ProjectAdvancedTimelineRootSwapProductionTests |
| O13 | N/P/S Scale | 四舍五入、长度与位置、同类 / 混合 owner | ProjectSelectionTransformEditCommandsTests、ProjectMultiOwnerAdvancedEditCommandsTests、SelectionReadOptimizationTests |
| O14 | N/S Transpose | 越界删除、保留 survivor ID、Undo 原集合 | ProjectAdvancedTimelineEditCommandsTests、ProjectAdvancedTimelineEditIntegrationTests |
| O15 | N/P/S Batch Edit | 表达式依赖、clamp / 删除、失败零发布 | ProjectBatchTimelineEditCommandsTests、BatchEditExpressionProgramTests、BoundedEditRoundTripTests |
| O16 | N Humanize | 固定 seed；Redo 不重抽；稀疏 unchanged holes | ProjectAdvancedTimelineEditCommandsTests、BoundedDirectMidiQueryRegressionTests |
| O17 | N Split，三种模式及预设 | 端点 / NoteOff velocity、上限 / 取消、ID | BoundedDirectMidiSplitJoinTests、BoundedLogicalNoteCommandTests、NoteSplitPresetStoreTests |
| O18 | N Join | 同 key 接合、原始顺序、选择与 Undo | BoundedDirectMidiSplitJoinTests、BoundedLogicalNoteCommandTests |
| O19 | N/P Quantize | 舍入、Note / Event 相反碰撞规则、tombstone | BoundedDirectMidiQueryRegressionTests、BoundedPointCommandTests、ProjectLogicalParameterQuantizeTests |
| O20 | N/P Batch Create、初值 / 表达式 / 预设 | 依赖与最大数量、终止、失败 / 取消 | TimelineGenerationIntegrationTests、GeneratorExpressionProgramTests、TimelineGenerationPresetStoreTests |
| O21 | N/P/S Properties，Mixed、逐字段还原 | OK 一次事务；取消无修改；类型共同属性 | AdvancedEditDialogsTests、Stage4DesktopSelectionIntegrationTests、ProjectBatchTimelineEditCommandsTests |
| O22 | Copy / Cut / Paste、Clipboard 替换 | bounded storage、精确 ID 和重新选择、可用菜单 | ProjectClipboardStorageTests、ProjectClipboardPasteTargetTests、ClipboardSelectionRoutingTests |
| O23 | Duplicate、跨轨道、Shared / Independent | 独立身份与共享执行状态，正式顺序不变 | BoundedDirectMidiSegmentTransformTests、BoundedLogicalNoteCommandTests、ProjectObjectClipboardTests |
| O24 | MIDI ↔ Logical Segment 转换 | 损失确认、共同 Note 字段、hidden window、原子 Undo | SegmentConversionServiceTests、SegmentConversionPlacementAndHistoryTests、SegmentConversionRoutingTests |
| O25 | P 自由画线 / 直线 / y=k / 固定 tick | 每 snap 点、越界坐标、同 tick 后者覆盖 | BoundedPointCommandTests、TimelineValueTraceSamplerTests、TimelineEventContextGestureTests |
| O26 | Lane 新建、切换、删除、参数定义 | 选择清除 / 保留契约、焦点、mapping owner | BoundedPointCommandTests、MappingEditingPolicyTests、LogicalParameterEventBindingDialogTests |
| O27 | S Split / 窗口扩充与裁剪 | 跨边界音符、参数起点状态、隐藏对象 | BoundedDirectMidiSegmentTransformTests、BoundedLogicalNoteCommandTests、BoundaryCleanupTests |
| O28 | Track / Root / Usage 新建、排序、改绑、删除 | 全局顺序 / 非空 Root / Definitions 不误删 | ProjectObjectClipboardTests、DesktopSessionControllerTests、Stage5ArrangementRoutingTests |
| O29 | Instrument / SubVoice / Mapping / Loop / Pre-Roll 编辑 | 依赖失效、共享状态、编译诊断 / 可听语义 | MappingEditingPolicyTests、LogicalParameterEventBindingCompilerTests、LoopEntryCompilationTests、PreRollCompilationTests |
| O30 | 首次 / 重复 / 跨区域 Undo，Redo，清分支 | 旧 root / selection 精确恢复，无全源 ID 退化 | PagedSelectionAndEditTransactionTests、PureMidiCowRootTests、BoundedEditRoundTripTests；M01 探针 60k |
| O31 | Arrangement / 三种 Piano Roll / Velocity / Event Lanes | 现有细边框 / 高亮、cache-only UI、局部失效 | TimelineRenderingTests、PagedTimelineCacheOnlyTests、PureMidiPagedPresentationTests、PagedLogicalPresentationTests |
| O32 | Conductor | Tempo Step、高密度点、时间 / 状态、拖动 | ConductorInteractionTests、ConductorProjectionTests、ConductorRenderingPerformanceTests |
| O33 | Onion / All Tracks | 层序、只读、来源、Compiled 混合显示及生命周期 | OnionWorkspaceTests、TimelineOnionTests、OnionPresentationLifecycleTests、CompiledOnionNoteIndexTests |
| O34 | Full / Incremental Compile | canonical / 诊断 / 来源完全等价 | PureMidiCompilationTests、PagedPureMidiCompilationTests、IncrementalCompilationTests、SourceTraceTests |
| O35 | Playback / Preview / Seek / Mute-Solo | 只消费 canonical，范围 / FIFO / 状态恢复 | PagedPureMidiAudioPlanTests、PreviewCompilerTests、音频专用门（阶段 3 / 6） |
| O36 | MIDI / Audio Export | 正式事件 / frame、命名与原子取消 | PureMidiExportTests、Midora.MidiExport.Tests / Midora.AudioRender.Tests 全套；音频性能非 M01 |
| O37 | New / Open / Save / Save Copy / Close | 保存时间与内容、严格校验、资源释放 | PureMidiReadOnlyPersistenceTests、ProjectPersistenceCoordinatorTests、M01 real-MIDI probe |
| O38 | Format 1/2 迁移、Format 3 严格性 / 故障 | 旧副本、未知 / 重复 / checksum、原子发布 | PersistenceContractV1Tests、PersistenceContractV2Tests、ProjectPresentationSchema2Tests、MidoraProjectPackageFaultInjectionV1Tests、FormatMigrationDesktopTests |
| O39 | Tab 切换 / 关闭、Project 切换 / 取消在途任务 | 不保留旧图与旧 owner；不会僵尸订阅 | SelectionPresentationLifecycleTests、OnionPresentationLifecycleTests、WpfMemoryProbeTests；M02–05 |
| O40 | Imported Meta / SysEx 的属性、Copy / Cut / Paste / Delete、随 Segment 变换 | payload 原样、正式顺序、只开放现有可编辑属性；不可把 opaque 当数值 point 任意生成 / 量化 | ProjectObjectClipboardPureMidiTests、MixedTimelineSelectionCommandsTests、BoundedDirectMidiSegmentTransformTests；大 payload 计账归 M07 |

阶段 1 只修改 O34 / O37 读取方式及基础设施；公共编辑与 UI 回归用于防止间接退化，不实施 O39 或其他阶段预算重构。

## 3. M01 只读消费者逐项审计

| 消费者 | 确认的现状 / 本阶段动作 |
| --- | --- |
| `MidoraProjectPackageV1.ValidateSupportedProject / EnumeratePureMidiTrackIds` | 原来枚举 mutable Note / Channel / Opaque 导致三表增长；改为正式值序列并传递取消。仍校验全局唯一 ID / 高水位，包括暴露范围外数据。 |
| `ValidateLoadedStableIds` | 原来打开和保存自校验都会再次物化；共用新值遍历。没有删除或跳过自校验。 |
| `PureMidiContentPackPersistenceV1.WriteMergedPack` | 编辑后、不能直接复用 pack 的正式 writer 改为值流。保持序列顺序、所有字段和 payload；byte-identical reference test。 |
| `FindReusablePack / CopyReusablePackAndHash` | 元数据 / pristine 检查及流式复制，本来不物化；保持不动。 |
| `PureMidiCompilation` 小型非分页计划 / opaque 冻结 / materialize | 只读枚举改为值；之后原有 tick / order / ID 排序及 MIDI 投影不变。 |
| `SemanticValidator` Pure MIDI 路径 | 使用 `EditedValues` 加已校验不可变源；不遍历 editable facade，保持既有验证协议。 |
| `PureMidiPagedCanonicalSource` | fingerprint 使用正式根 / edited values，播放端点、状态、统计为 value query；本阶段不改。 |
| `PureMidiPresentationSources` / Onion / 对象列表 | 使用 query snapshot / value object source / page 查询，不以 mutable full enumeration 绘制；生命周期和 retained view cache 属于阶段 2，不在本轮改。 |
| bounded selection、Clipboard、变换 | 使用 revision-bound object source 与值页 / ID；保留避免破坏 indexed lookup、collision 和 Undo。 |
| `ShiftMidiSegmentContent`、兼容碰撞路径 | 存在 mutable enumeration，但负责 setter 或实际对象删除，不是只读消费者。不能机械换成 readonly 值或删 identity 表；本阶段保留。bounded 主路径已有独立测试。 |
| Collection `GetEnumerator / CopyTo / IndexOf` 兼容接口 | 仍是明确可编辑对象接口。保留 setter / identity / collapse-to-source；正式全量只读消费者不再使用它。新 API 不是“所有访问都不分配”的承诺。 |
| Logical / SubVoice protobuf DTO 和 Conductor JSON | 不使用这三个 Direct MIDI 集合，属于 M08；避免把本阶段扩大为另一次序列化重写。 |

## 4. 冻结资源口径

| 容器 / 层 | 当前依据 | 计账与本阶段边界 |
| --- | --- | --- |
| Pure MIDI 解码 cache | `PureMidiContentPack.DefaultDecodedCacheByteLimit` = 64 MiB | 共享解码页一次计账；并非进程总上限。M07 继续审计 Opaque payload 实际占用。 |
| Pure MIDI writer 编码缓冲 | `DefaultEncodeBufferBudget` = 64 MiB；单 decoded page ≤4 MiB | 活动 writer 独立工作缓冲；builder 尾页 / 并发预算属于 M09。 |
| detached edit working / resident | `DetachedPagedEditTransaction` 默认各 64 MiB | 不把两个独立预算或共享值页重复算为总上限；spill 归任务 / 历史 owner。 |
| bounded immutable values decoded cache | `BoundedImmutableValueSource` 64 MiB | 正式 source / lease 与 LRU retained 分开计账。 |
| WPF raster cache | `SharedTimelineRasterCache` 256 MiB | 完成位图预算不含所有在途 / retained Drawing / UI subscriptions；本阶段不调整。 |
| 三种 source facade identity 表 | 原来只读全源可各增长至 N | 新只读扫描增量必须为 0；已有显式编辑索引不强制清空。 |
| 持久化 `StableIdSetV1` | 2^20 IDs / block 的 ulong 位图 | 本阶段保留全局唯一性验证；稀疏巨大 ID 放大留给 M10。 |
| Project / source / Undo | immutable root 可共享，拥有者关闭释放 | 原项目与保存自校验项目允许同时存活；不可合并 ID 生命周期或删正常 Undo。 |

## 5. 验收职责

工程侧承担完整自动正确性、重复性能测量、失败 / 取消、容器计数和资源归因。用户在阶段 2 完成后执行验收 A 的少量代表性音乐工作流；本阶段不要求用户重测整份表。

需人工确认的是真实操作手感和视觉变化；计数、字节等价及大规模内存归因由自动证据覆盖。若发现新增 UI 延迟 / 首次长尾，即使 M01 峰值变小也不能视为验收通过。
