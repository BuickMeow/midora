# Midora 单 `long` 稳定 ID 全链重构 Requirement Trace

日期：2026-08-07
状态：实现与专项自动验证完成；等待并入后续全量发布门
决定依据：Q-NUI-024、Q-NUI-025、Q-NUI-011
规格依据：《Midora SRS》8.52、16.5.3、16.11、16.13.2、21.3、INV-003、INV-029、INV-030、INV-040；ADR-CORE-035。

## 输入

- `MidoraProject.NextStableId` 及全部需要稳定身份的 Domain 对象。
- 对象引用、集合键、canonical source trace、fingerprint 和确定性 tie-break 输入。
- C# Mapping Context 中的对象身份字段与持久化 `abiVersion`。
- `.midora` v1 的 JSON、对象路径、protobuf、descriptor 与 golden bytes。

## 正式输出

- `MidoraId` 只承载单个 C# `long`，合法范围 `1..long.MaxValue`。
- Project 分配器单调递增、不补缺、不复用；现存对象 ID 全局唯一且小于 `NextStableId`。
- Mapping ABI v2 只通过 `MappingStableIdV2(long Value)` 传递身份。
- JSON 使用 canonical 十进制 integer；对象文件名使用无符号无前导零十进制 ASCII；protobuf 在既有外层字段号上使用标量 `int64`。
- Full/Incremental Compile、Playback、Preview、MIDI Export、Audio Render 对身份来源和确定性保持一致。

## 边界与失败条件

- `0`、负值、溢出、非 canonical JSON token、非 canonical 文件名和 protobuf 非正值均拒绝。
- `NextStableId == long.MaxValue` 时不能分配该值，因为分配后将不存在可表示的下一个计数器；操作在修改 Project 前失败。
- 任何重复稳定 ID、现存 ID 不小于 `NextStableId`、文件名/索引/对象内部 ID 不一致均按既有结构或保存门失败。
- Mapping ABI v1、旧 32 位十六进制文本和旧 high/low protobuf 不提供兼容读取或迁移。

## 诊断

- Domain 分配耗尽使用稳定、可测试的结构化异常边界。
- JSON/protobuf/路径错误继续由 Persistence 的 structure/schema/file-damage 分层诊断承载。
- Mapping ABI 版本或源码不合法继续产生 Mapping Function 编译错误，不回退 ABI v1。

## 数据归属

- 持久化：对象稳定 ID、引用和 `NextStableId`。
- 派生：canonical source trace、fingerprint、protobuf runtime 对象。
- 运行时：Mapping 编译产物、collectible ALC、解析缓存和绝对包路径。

## 明确非目标

- 跨 Project 或分布式全局唯一身份。
- 以稳定 ID 数值表达业务顺序或优先级。
- 旧 128-bit 开发格式迁移、双写或兼容读取。
- 为 JavaScript Number 增加 `2^53-1` 上限；第三方工具必须自行精确解析 64-bit integer。

## 自动验证证据

- Core 与 Audio 两个 Release solution 构建通过，0 warning / 0 error。
- Compiler 218/218、Persistence 85/85 通过；JSON exact-token、allocator exhaustion、protobuf scalar/descriptor/golden 与 Mapping ABI v2 契约均有专项覆盖。
- `MidiRenderPlan` 专项 9/9 通过，覆盖正 `long` source table、唯一性、运行时计划版本 3 round-trip 与旧 Guid source-table 版本拒绝。
- 完整非 UI 发布门计数已更新为 894；其零跳过结果在后续统一发布门中记录，不在尚未运行时预先宣称通过。
