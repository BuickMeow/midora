# Midora 稳定 ID 分配型 History 命令 Requirement Trace

日期：2026-08-07
状态：已完成
决定依据：Q-NUI-005、ADR-CORE-038
规格依据：《Midora SRS》3.10、3.12、7、8、9、11、16.5.3、16.27

## 输入与正式输出

- 输入：Project 当前对象图、`nextStableId`、History cursor，以及创建/复制/Segment Split 的完整参数。
- 正式输出：经统一 Project History 和 Compilation Session 提交的新对象图；所有新身份来自同一 Project allocator。
- Redo 必须恢复首次 Apply 得到的同一对象和 ID，不重新分配或重跑深拷贝映射。

## 边界与失败

- ID 只在首次 Apply 分配；Undo 不回退 allocator；丢弃 Redo 后的新分支分配更高 ID。
- 创建参数、父对象、引用、名称、范围和冲突在可分配前尽量完成验证。
- 首次构造或编译失败时尽最大努力移除已挂接对象，不建立 History；已经取得的 ID 在当前会话内烧掉，避免复用。
- allocator 耗尽在可见对象提交前失败；Project/History 不出现 partial 新对象图。

## Modified 与持久化

- 创建后 Undo 回保存点可以 clean；allocator 瞬态空洞不单独维持 Modified。
- 任何后续 Save 都写出当前高水位；History 栈本身不持久化。
- 关闭 otherwise-clean 会话后，无持久化、无对象、无 History 引用的瞬态身份可以丢弃。

## 自动验证计划

- 每类命令覆盖首次分配、Undo 高水位不退、Redo 同 ID、分支后更高 ID、保存点 Modified、Full/Incremental 等价。
- 深拷贝覆盖所有内部可变对象、引用重映射与 source/duplicate 独立编辑。
- Split 覆盖左侧保留 ID、右侧及必要数据新 ID、曲线起点状态、跨点 Note 截断与精确 Undo。

## 实施结果

- 已覆盖 Track、Instrument/Folder、Segment/Split、Logical Note/Lane/Point、Conductor 事件、SubVoice、全部 Template Event、Value Curve/Point、Logical Parameter/Enum item、Envelope、Mapping Function、Logical Parameter Mapping、Mapping Step、Mapping Chain 粘贴与 Embedded SF2 resource identity。
- Mapping Chain 粘贴原子替换目标 Chain 对象，因此 Chain 与全部 Step 均获得新 ID；目标参数的 Rounding/Overflow 保持不变，Undo 恢复原 Chain。
- Embedded SF2 先建立无 Project 副作用的流式快照并验证；首次 Apply 才分配 resource ID，Undo/Redo 在匹配的资源租约之间切换，保存只读取当前引用对应的租约。
- Application 专项测试覆盖高水位、分支、深拷贝、冲突替换、资源租约和 Full/Incremental 等价。
