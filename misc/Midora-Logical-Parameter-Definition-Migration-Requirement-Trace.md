# Midora Logical Parameter Definition 原子迁移 Requirement Trace

日期：2026-08-07
状态：已完成
决定依据：Q-NUI-007、Q-NUI-009、ADR-CORE-039
规格依据：《Midora SRS》9.8、11.13、11.19、16.27

## 输入与正式输出

- 输入：目标 Definition 完整类型/范围/default/Enum 结构、显式 `Clamp` 或 `DiscardInvalidValues`、目标 Enum 语义确认，以及全 Project 引用该 Parameter ID 的 Lane。
- 输出：Definition、保留/新增/删除的 Enum item 与全部引用 Lane Point 的单个原子 History entry。

## 转换规则

- Double→Integer/Enum 使用 `MidpointRounding.AwayFromZero`。
- `Clamp` 先限制到合法范围；Enum 再选择最近已定义值，等距取较小值。
- `DiscardInvalidValues` 删除转换后不在合法范围或不属于目标 Enum 的点。
- 目标 Enum 的全部保留点强制使用 Step；点稳定 ID 保留。Enum item 以调用方提交的有序完整集合处理，现有 item ID 保留，新 item 只在首次 Apply 分配。

## 边界与失败

- 目标 Enum 必须非空、名称/数值唯一、default 指向已定义值；已有 item ID 必须属于当前 Definition 且不重复。
- 目标为 Enum 而未确认语义警告、未知迁移策略或非法完整计划时，在分配 ID 和修改对象图前失败。
- 编译失败回滚 Definition、Enum item 与全部 Lane；已分配的瞬态 ID 按 Q-NUI-005 在当前会话烧掉。

## 持久化与非目标

- Definition、Enum item、Lane/Point 是 Project 源数据；迁移策略和确认只属于一次命令，不持久化。
- 不按名称重绑定、不生成新 Point ID、不静默选择 Clamp、不保证 Enum 的音乐语义仅因数值接近而等价。
