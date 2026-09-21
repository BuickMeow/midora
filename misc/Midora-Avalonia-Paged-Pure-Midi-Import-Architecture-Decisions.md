# ADR：Avalonia 侧消费分页 Pure MIDI 内容（方案 A）

状态：已接受（2026-09-21，产品所有者选择方案 A：对齐 WPF 的分页导入能力）

## 1. 背景

- WPF/参考实现的正式导入走 `MidiProjectImportService.ImportFile(path, ...)`：两遍流式扫描，Segment 内容为
  分页源，`UsesPagedContent = true`，编译器因此走分页发布；实测大文件导入约 13 秒。
- Avalonia 移植版的评审/会话导入走 `MidiProjectImportService.Import(byte[])`：音符全量在内存，
  `UsesPagedContent = false`，编译器退化为"全量物化 + 全量排序"。
- 实测 `tau2.5.9.mid`（50 MB，61 轨，12,573,475 canonical 事件）：
  - 导入服务 72,979 ms（其中为校验而做、随后被丢弃的完整发布编译 62 s）→ 改为只校验后 7,817 ms；
  - 创建/激活路径仍要对 1257 万内存事件做一次完整发布（`PublishingMidi` 55 s）+ 激活（>40 s）；
  - 打开总耗时 >115 s，UI 全程阻塞。
- 根因不是编译器慢，而是 **Avalonia 侧完全没有消费分页内容的能力**：
  `src/midora-avalonia` 中 `UsesPagedContent` / `HasPagedSource` 零引用，
  `EditableMidiProject` 与 `MidiTimelineSource` 都建立在全量内存列表上。

## 2. 决策

把 WPF 的分页链路移植到 Avalonia，**分页内容成为大 Segment 的唯一真源，任何层都不得为"方便"整段物化**：

1. **导入入口**：`ProjectSessionHost` 新增路径版导入，使用
   `MidiProjectImportService.ImportFile(path, projectName, portMapping, cancellationToken, progress)`；
   进度回调 `IProgress<MidiProjectImportProgress>` 直接驱动导入进度 UI。`byte[]` 版本保留给非文件来源，
   并在文档中明确它无法分页。
2. **展示层**：`MidiTimelineSource` 的 Segment 预览源改为分页支撑——用 `NoteCount` 与
   `GetNote(index)`（索引有序）做区间二分查询，`ContentFingerprint` 作为 GPU 顶点批缓存的失效键；
   预览查询只取可见区间，禁止把整段内容读入列表。
3. **编辑层**：`EditableMidiProject` 采用**两级模型**：未被编辑的 Segment 保持分页（只读），
   编辑命中时才把该 Segment（或页）物化为可编辑音符，此后该 Segment 以物化内容为准
   （copy-on-write，按 Segment 粒度）。物化不得改变稳定 ID、同 tick 排序或 SRS 编辑语义。
4. **编译器与消费者**：不改动。`UsesPagedContent` 已驱动分页发布与 `CombinePaged` 指纹。
5. **持久化**：`.midora` 必须能保存分页来源的 Project Source Data（不保存 canonical）；
   分页内容的写入/重开校验属于本 ADR 的验收范围，不得因为分页而丢事件或改变稳定 ID。
6. **命中测试/选择/诊断**：仍使用同一份 CPU 数据（分页源按需查询），不从顶点批或位图反推语义。

## 3. 不变量

- 稳定 ID 是身份；分页/物化切换不得重编 ID。
- 同一输入的分页与内存路径必须产生语义与形式一致的 canonical 结果（含指纹）。
- Full 与 Incremental 编译等价；乱序集合输入、同 tick 排序、范围起点状态恢复不受影响。
- 内存预算：分页源常驻大小与事件数无关；编辑物化按 Segment 计，且必须有明确释放时机。
- Mute/Solo 仍只是运行时消费过滤；分页不改变 Project/canonical/MIDI 导出/音频渲染内容。

## 4. 分阶段实施

- **A1**：导入入口切到 `ImportFile` + 路径版会话导入；展示层只读消费分页（预览区间查询）；
  验收：`tau2.5.9.mid` 打开耗时接近"解析 + 构建 + 校验"（≈9 s），UI 不阻塞，预览/滚动正确。
- **A2**：编辑层两级模型（copy-on-write 物化）+ 命中测试/选择/编辑命令在分页 Segment 上的语义。
- **A3**：持久化往返（保存/重开分页来源工程）+ 大文件编辑回归。

## 5. 验证门

- `Midora.Application.Tests`（含 `StreamingMidiProjectImportTests`）、`Midora.Playback/Common` 全绿；
- `--smoke-shell` failures=0；
- 同一 MIDI 的分页导入与内存导入：canonical 事件数、指纹、MIDI 导出字节一致（同平台逐字节）；
- 大文件打开耗时与内存峰值记录（`MIDORA_IMPORT_TRACE`）；
- 编辑分页 Segment 后：稳定 ID 保留、Undo/Redo、保存/重开一致。

## 6. 风险

- 编辑语义若在分页 Segment 上实现不当，可能出现"看不到的音符被编辑"或 ID 重编；
  因此 A2 必须先补测试再改实现。
- `.midora` 写入分页内容若未覆盖，会在保存时静默丢事件——A3 前不得宣称支持大文件编辑。
- 导入期间的 UI 响应：A1 需同时把导入放到后台线程并接进度/取消（WPF 三件套：进度、Port 映射、报告）。
