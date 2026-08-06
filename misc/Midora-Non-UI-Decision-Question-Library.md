# Midora 初版非 UI 实施决定问题库

状态：进行中
创建日期：2026-08-06
关联台账：`misc/Midora-Non-UI-Implementation-Tracker.md`

本文只收录实施“全部非 UI 初版能力”过程中真正需要产品所有者决定、确认或修改的问题。SRS 已规定的事实以及 `misc/Midora-SRS-Code-Conformance-Audit-2026-08-05.md` 中已确认的 1A～25.1A 不重复登记。

## 1. 使用规则

- **小决定**：不同选择影响局部、兼容面小且容易修改。实现方先按推荐方案实施，同时登记完整依据、影响和替代方案；产品所有者随后确认或要求修改。
- **大决定**：会改变可听结果、持久化兼容性、公共接口、确定性、并发模型、发布形态或大范围工作流。对应分支在决定前不实施；不受影响的工作继续。
- 每个问题必须记录：SRS/源码依据、事实与不确定性、影响范围、推荐方案、备选方案、当前实施状态、产品回答和最终处理提交。
- 已回答问题保留原编号，不删除，作为决定审计链。

## 2. 当前开放问题

### Q-NUI-002：初版 `.midora` 需要迁移的历史格式基线

- 类型：大决定
- 状态：待确认；只暂停“旧格式成功迁移器”分支
- 发现日期：2026-08-06
- SRS 依据：第 16.12.4、16.24.1～16.24.4 节。
- 已确认事实：当前仓库只发布 `fileFormatVersion = 1`、schema v1、两份对象 protobuf v1 descriptor 及其 golden bytes；不存在 v0 JSON Schema、protobuf descriptor、golden `.midora` 包、字段映射或曾对外发布的旧格式。提交 `4b69e72` 的基础包也标记为 v1，且仍可由当前实现读取，不构成旧格式。
- 不确定点：SRS 要求低版本项目在内存中迁移，但没有定义初版发布前究竟存在哪些合法历史版本，以及各版本到 v1 的逐字段转换。实现方不能从版本号 `0` 推断包结构、缺失字段默认、稳定 ID 生成或 protobuf wire 契约。
- 影响范围：`.midora` 文件兼容承诺、迁移后的可听语义与稳定 ID、Modified/保存确认工作流、golden 兼容资产，以及未来版本升级策略。
- 推荐方案：初版 v1 声明“当前没有已发布且受支持的历史格式”；本轮实现严格版本预检和可扩展迁移注册边界，但注册表为空。低于 v1 的未定义包以结构化 `MigrationUnavailable` 失败，不能伪装成损坏 v1 或静默补默认。未来首次升级到 v2 时，必须随 v1→v2 migrator、v1 golden packages 和字段级迁移测试一起发布。
- 推荐依据与限制：避免发明不存在的 v0 文件格式，同时建立未来真实迁移所需的入口。限制是第 16.24 节在初版 v1 没有成功迁移样本，只能验证“无已定义来源版本时严格失败”；若产品确有需要兼容的内部原型格式，则必须先冻结其完整契约。
- 备选方案及差异：A. 现在正式定义 v0 并提供完整 schema/descriptor/golden/字段映射，再实现 v0→v1；工作量和永久兼容面显著增加。B. 指定某个现有提交生成的包为历史格式，但必须给它新的真实版本号并明确与当前 v1 的差异，不能把两个不同契约都标成 v1。C. 对任意 `fileFormatVersion < 1` 猜测缺失字段默认；该方案不可验证且可能改变可听语义，不推荐。
- 当前实施状态：成功迁移器未实施；严格版本预检、未来迁移注册接口和故障注入可继续。
- 需要产品所有者回答：是否采用推荐方案？如果不采用，请明确需兼容的历史版本号，并提供或指定其 schema、descriptor、golden package/生成提交及字段迁移规则。
- 产品回答：待填写。
- 最终处理与提交：待填写。

### Q-NUI-003：Project MIDI Export Settings v2 字段与初始默认值

- 类型：大决定
- 状态：待确认；只暂停 Export Settings 领域默认值、schema v2 与 v1→v2 设置迁移分支
- 发现日期：2026-08-06
- SRS 依据：第 3.7.7、14.7、14.15、14.16、16.7.3、19.5.7～19.5.8 节。
- 已确认事实：`Export Settings` 是 Project 内容；可保存模式、范围策略、Track 选择策略、Routing、Readme 和 Warning 策略，但不得保存绝对输出路径。仓库已经发布并使用严格 `export-settings.json` schema v1，当前 v1 只包含 `schemaVersion`；按兼容规则不得直接在 v1 增加必填字段。SRS 明确 Readme 默认开启，并明确“按 Logical Track 导出”默认 Compact，但没有规定新 Project 的默认导出模式、Whole/Per Port 的 Routing 默认、Warning-as-error 默认，也没有说明默认 Track 策略为 Explicit 时是否持久化具体稳定 ID 集合。
- 不确定点：这些默认值决定首次打开配置时的工作流和 Port/Channel 输出；字段一旦进入 schema v2 就成为文件兼容承诺。把它们静默写进现有 v1 会同时违反严格 schema 和已发布字段冻结规则。
- 影响范围：Project 领域公共接口、Undo/Redo 设置命令、`.midora` schema 与迁移、默认 MIDI 文件组织、Routing、Readme、Warning 阻止行为，以及未来 UI 初始化。
- 推荐方案：保留 v1 不变，新增 `export-settings.json` schema v2，并提供明确的 v1→v2 文件级迁移。新 Project 与 v1 迁移默认采用：`Whole Project`、`Project Default Range`、`All Valid Logical Tracks`、`Compact`、`includeReadme = true`、`treatWarningsAsErrors = false`；只有 Manual Range 时保存合法 `startTick/endTick`。初版不在默认设置中持久化 Explicit Track ID 集合，只保存选择策略；一次性具体勾选仍属于任务快照。设置变更由一个原子领域命令提交/撤销。
- 推荐依据与限制：Whole Project 是最小惊讶的单文件入口；Compact 与当前确定性分配器一致，Readme 默认和 Warning 原级别遵循 SRS 已明确方向；不持久化具体勾选避免默认设置绑死当前编辑对象。限制是用户若期望默认分 Track、Preserve 或持久化固定 Track 集合，需要采用其他契约，且必须在 schema v2 发布前确定。
- 备选方案及差异：A. 默认 Per Logical Track + Compact，批量素材工作流更直接但首次导出产生多文件。B. 默认 Whole Project + Preserve，优先保持对应导出上下文的路由表示，但当前编译器分配本身已紧凑，两者初期通常同形。C. v2 保存 Explicit Track IDs，能形成固定默认子集，但对象删除/复制/损坏迁移与默认集合修复面显著扩大。D. 继续保留空 v1，只使用会话默认；这不满足 Project Export Settings 的正式持久化要求。
- 当前实施状态：三模式一次性任务、Routing 快照、Readme、冻结命名与文件事务已实现；未修改 `ExportProjectSettings` 和 `export-settings-v1.schema.json`，未发布 v2。
- 需要产品所有者回答：是否采用推荐方案？若不采用，请逐项给出默认模式、默认范围、默认 Track 策略（以及是否保存具体 ID）、默认 Routing、Readme 和 Warning-as-error，并确认仍采用 schema v2 + v1→v2 迁移而不是修改 v1。
- 产品回答：待填写。
- 最终处理与提交：待填写。

### Q-NUI-004：Application Preferences 的本机路径与编码

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 17.2.2、20.14.1、20.14.6～20.14.9 节。
- 已确认事实：Application Preferences 自动保存于当前 Windows 用户本机，与 `.midora` 版本独立；读写失败使用安全默认值并显示非模态 Notice；不影响 Project、Modified 或 Undo/Redo。SRS 已固定偏好内容、音频默认值和禁止持久化项。
- 不确定点：SRS 没有规定本机具体目录、Windows Registry/文件选择、文本/二进制编码、文件名、大小上限或原子写入实现。
- 影响范围：仅当前 Windows 用户的本机偏好迁移、故障恢复和诊断；不影响 Project 文件兼容、canonical、可听语义、导出产物或公共音乐领域接口。
- 推荐方案：使用 `%LOCALAPPDATA%\Midora\preferences-v1.json`；采用独立 `schemaVersion = 1` 的 source-generated UTF-8 JSON、固定字段顺序、未知字段拒绝、1 MiB 读取上限，以及同目录临时文件 + flush + 原子 move/replace。文件不存在使用默认值且不报错；版本、格式、值域或 I/O 失败使用默认值并返回 `PreferenceReadFailed`/`PreferenceWriteFailed` Notice。
- 推荐依据与限制：LocalApplicationData 符合当前用户本机、无管理员权限的 Windows 应用惯例；JSON 便于开发期审计，独立版本可迁移；原子替换避免部分写入。限制是未来若改用 MSIX app data、Registry 或数据库，需要迁移或一次性回退默认值；初版本实现不提供手工导入/导出。
- 备选方案及差异：A. Windows Registry，权限和原子单值较成熟，但嵌套偏好、版本迁移和人工诊断不如文件直观。B. `%APPDATA%` roaming，可能被用户配置漫游，与 SRS“当前 Windows 用户本机”边界不完全一致。C. 二进制/SQLite，扩展和事务能力更强，但对当前小型单快照数据增加不必要依赖与兼容面。
- 当前实施状态：已实现 `ApplicationPreferencesStore` 与 `ApplicationPreferencesService`；覆盖确定性往返、原子替换、损坏/未知/超大/版本/写失败回退、边界值、Stopped-only 提交和缓存失效。
- 需要产品所有者回答：是否采用推荐方案？如需修改，请指定本机存储位置与编码；偏好字段、默认值及失败回退仍服从 SRS，不在本问题中重新决定。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-005：创建对象 Undo 后 `nextStableId` 与 Modified 的关系

- 类型：大决定
- 状态：待确认；只暂停会分配新稳定 ID 的创建/复制/分割 Undo 命令
- 发现日期：2026-08-06
- SRS 依据：第 3.10、3.12、16.5.3、16.13.2、16.27 节。
- 已确认事实：Project 保存 `nextStableId`；计数器必须持久化、单调递增、不补缺、不复用，所有现存对象 ID 全局唯一且小于它。创建、复制、Segment Split 右侧等操作必须分配新 ID；Project History 本身不持久化。Undo/Redo 要恢复对象原状态，Save 成功清除 Modified。
- 不确定点：创建对象后 Undo 若保留已推进的 `nextStableId`，可见对象和 canonical 已恢复但持久化源仍与保存点不同；若回退计数器，则违反“单调递增/不复用”的字面规则，并可能与 redo branch 中保留的对象 ID 冲突。SRS 没有说明仅存在于已撤销、从未保存且无存活引用的瞬态 ID 是否计入“不复用”。
- 影响范围：所有 Create/Duplicate/Split/Paste 等 Undo、Redo 身份、Modified 星号、关闭保存提示、`project.json` 的 `nextStableId`、redo 分支丢弃和跨会话 ID 安全。
- 推荐方案：`nextStableId` 在当前打开会话内永不回退；Redo 恢复原对象与原 ID，新分支分配更高 ID。History 判断 Modified 时把“只有已撤销瞬态分配导致的计数器空洞”视为不需要单独保存：Undo 回到保存点可清除 Modified；若以后因其他编辑 Save，则把更高计数器一并持久化。关闭一个 otherwise-clean Project 时允许丢弃这些从未持久化、无存活对象、无存活 redo history 的瞬态 ID；把“不复用”解释为当前会话及任何可存活/可持久化身份不得复用。
- 推荐依据与限制：该方案在当前会话内严格保持身份单调和 Redo 稳定，不会仅为不可见 allocator 空洞强迫用户保存；对象、引用、canonical 均无隐藏差异。限制是关闭后从旧保存点重开，未来可能再次分配一个只在已丢弃瞬态历史中出现过的数值；该瞬态身份没有文件、对象或 Undo 引用可观察。
- 备选方案及差异：A. 任何计数器推进都永久 Modified，Undo 创建后仍要求保存一个只有 `nextStableId` 变化的文件；最严格遵守字面单调，但用户工作流反直觉。B. Undo 创建时把计数器回退到命令前值，并在新分支复用；可实现字节级保存点恢复，但直接放宽当前“单调/不复用”规则。C. 将 allocator 高水位另存为 Application/会话状态；会让 Project 身份分配依赖文件外历史，违反 Project 自包含边界。
- 当前实施状态：不分配 ID 的属性/设置命令、History、Modified/savepoint、branch、external dirty、锁和编译回滚已实现；Track/Instrument/Folder/Damaged Placeholder、Segment、Conductor、Project Settings、Note/Lane/Point、Instrument Lifecycle 与 SubVoice 基础命令已接入；所有 ID 分配型正式命令尚未接入 History。
- 需要产品所有者回答：是否采用推荐方案？如果不采用，请选择备选 A 或 B；C 不推荐且需要同时修改 Project 自包含不变量。
- 产品回答：待填写。
- 最终处理与提交：待填写。

### Q-NUI-006：正常绑定期间 Last Known Instrument Name 的维护时机

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 7.12、7.20.2、11.3、16.10.3、20.8.4 节。
- 已确认事实：Logical Track 以稳定 ID 正式绑定 Event Instrument；名称不构成引用，也不得用于自动重绑。引用断裂或被引用 Instrument 删除后，应保留可用的最近绑定名称，仅供提示。名称和 Last Known Name 都按持久化 short text 保存。
- 不确定点：SRS 明确了删除/断裂后的结果，但没有逐操作规定正常绑定、显式取消绑定和已绑定 Instrument 重命名时，`lastBoundEventInstrumentName` 字段应何时刷新。
- 影响范围：仅以后发生删除、损坏或引用断裂时显示的提示文本，以及 Logical Track protobuf 中该快照字段的值；不改变稳定 ID 引用、编译、播放、MIDI、音频、资源分配或现有编辑是否进入 History/Modified。
- 推荐方案：成功绑定/改绑时把快照更新为目标 Instrument 当前名称；当前绑定的 Instrument 重命名时同步更新其所有已绑定 Track 快照；显式取消绑定时保留刚离开的 Instrument 名称；删除仍写入删除当时名称。Undo/Redo 精确恢复操作前的 ID 与快照值。
- 推荐依据与限制：这样“Last Known”在未来真正断裂时是最近一次用户可见名称，同时仍完全禁止按名称解析或修复。额外快照写入只发生在本来就会修改 Project 的绑定/重命名命令内，不新增独立 History entry。限制是 `.midora` 中已绑定 Track 也会保存一个可由 ID 解析出的冗余名称快照。
- 备选方案及差异：A. 只在删除或打开时发现断裂时写快照；正常绑定期间字段保持 null/旧值，文件更少冗余，但若目标对象内容已经无法读取，可能没有可用最新名称。B. 正常绑定时更新，但 Instrument 重命名不更新；实现更少联动，不过断裂提示可能显示历史旧名，不符合“最近”直觉。
- 当前实施状态：已在 `ProjectDomainEditCommands` 的绑定、取消绑定、Instrument 重命名和删除命令中按推荐方案实现，并覆盖精确 Undo/Redo；不影响底层按 ID 绑定规则。
- 需要产品所有者回答：是否采用推荐方案？如不采用，请选择 A 或 B；无论选择哪项，都不会启用按名称自动绑定。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-007：Logical Parameter Lane 重绑定的整数中点与 Enum 插值转换

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 11.13.1、11.19.2～11.19.5、18.2.5 节。
- 已确认事实：不兼容 Lane 重绑定必须由调用方显式选择 Clamp 或“丢弃范围外的值”；Double 转 Integer 先按目标 Integer 规则取整；转 Enum 先得到整数值，Clamp 时匹配最近已定义值且等距选较小值；Enum Lane 只允许 Step 状态。命令必须原子进入 Project History，并保留点稳定 ID。
- 不确定点：SRS 没有固定恰好位于 `n + 0.5` 时采用 AwayFromZero、ToEven 或其他中点规则，也没有逐句规定原 Linear point 重绑定为 Enum 后是自动改为 Step、拒绝整个重绑定，还是删除相关点。
- 影响范围：只影响用户明确执行类型不兼容 Lane 重绑定时的转换结果和曲线形状；不改变 `.midora` wire/schema、正常点编辑、编译器既有曲线求值、导出格式、音频后端或并发模型。命令可完整 Undo，因此修改成本局部。
- 推荐方案：整数中点统一采用 `MidpointRounding.AwayFromZero`；目标为 Enum 时，所有保留点的 interpolation 明确转为 `Step`。Clamp/Discard 仍是每次命令的显式参数，不设置隐藏默认；Enum 最近值等距时选较小值。
- 推荐依据与限制：现有 Mapping 整数目标的 `Round` 已使用 AwayFromZero，可避免应用修复命令与编译器常规整数化出现两套中点规则；Enum 的连续插值在 SRS 中本来就非法，转为 Step 可让显式修复完成后立即形成合法源数据。限制是原 Linear 段会变为阶梯状态，属于用户已选择重绑定到 Enum 时可预期但可听的变化。
- 备选方案及差异：A. 中点采用 ToEven，可减少统计偏差，但与现有 Mapping `Round` 不一致。B. 目标 Enum 遇到任何 Linear point 时拒绝，要求用户先单独改成 Step；最保守但会把一次修复拆成多步。C. 删除 Linear point；会产生比改为 Step 更大的数据损失，不推荐。
- 当前实施状态：`RebindLogicalParameterLane` 已实现显式 Clamp/Discard、强制 Enum 语义警告确认、AwayFromZero、Enum 最近值/等距较小值、Linear→Step、稳定 ID 保留和原子 Undo；自动测试覆盖确认门、Clamp、Discard、边界、精确恢复及 Full/Incremental 等价。
- 需要产品所有者回答：是否采用推荐方案？如不采用，请分别指定整数中点规则和目标 Enum 遇到 Linear point 时采用备选 B 或其他明确转换。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-008：删除非空 Mapping Chain 的确认与 v1 空链表示

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 9.1.4～9.1.6、9.10.1、20.4.8、20.14.5 节。
- 已确认事实：空 Mapping Chain 等同无映射并使用原始值；删除 Mapping Chain 只解除其中对 Mapping Function 等资源的引用，不删除资源；禁用链保留配置。当前已发布 v1 Domain/Protobuf 对每个事件参数以及每条 Logical Parameter Mapping 都要求一个非空 `MappingChain` 对象，属性只读且含稳定 ID，因此不能在不改变 v1 文件契约的前提下把整个 Chain 属性真正设为 null/移除。删除确认决定不持久化。
- 不确定点：SRS 没有明确单个非空 Mapping Chain 删除是否必须确认，也没有规定当前“永久 Chain 对象”实现应如何表达删除后的 Chain enabled 配置。若只清空 Step 但保留 `IsEnabled = false`，当前声音仍等价，但以后新建/粘贴 Step 可能继承一个用户以为已删除的禁用状态。
- 影响范围：只影响删除映射链这一局部编辑工作流、删除后的空 Chain sentinel 值和随后再次添加 Step 的默认启用状态；不改变 Chain/Step protobuf 字段、canonical 空链语义、Mapping Function/Envelope/Logical Parameter 资源、输出格式或并发模型。操作可完整 Undo。
- 推荐方案：删除非空 Chain 要求调用方给出一次显式确认；已确认后清空全部 Step，把永久空 Chain sentinel 的 `IsEnabled` 复位为 `true`，同时保留 Chain 稳定 ID 和目标参数的 Rounding/Overflow 设置。Undo 恢复删除前的 Chain enabled、相同 Step 对象、引用和顺序。删除已空且 enabled 的 Chain 是无操作；删除已空但 disabled 的 Chain 只复位 sentinel 并进入 History。
- 推荐依据与限制：非空 Chain 可包含多个有序 Step，删除是集中数据损失，单次明确确认与现有非空容器删除命令一致；enabled 空 sentinel 最接近“链不存在后未来重新创建”的默认状态，同时不破坏已发布 v1 必填对象。限制是内存/文件中仍保留不可见 Chain ID，严格说是 v1 表示等价而不是物理删除对象。
- 备选方案及差异：A. 不要求确认，直接清空并复位 enabled；操作更快但更容易误删整组 Step。B. 要求确认但保留原 `IsEnabled`；Undo 更简单，但未来重新添加 Step 可能意外继续禁用。C. 修改 Domain/Protobuf 允许 nullable/optional Chain 并发布新 schema 版本；能物理表达不存在，但会扩大文件兼容、迁移和公共接口影响，不适合作为本轮局部命令修改。
- 当前实施状态：已按推荐实现 `DeleteMappingChain`；现有测试覆盖拒绝未确认删除、空 sentinel、资源/ID 保留、同对象同顺序 Undo、禁用状态恢复及 Full/Incremental 等价。
- 需要产品所有者回答：是否采用推荐方案？如不采用，请选择 A、B 或明确要求另开持久化版本设计；无论选择哪项，删除链都不会级联删除 Mapping Function/Envelope/Logical Parameter 资源。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

## 3. 问题模板

### Q-NUI-XXX：标题

- 类型：小决定 / 大决定
- 状态：待确认 / 已按推荐实施待确认 / 已确认 / 已修改 / 已关闭
- 发现日期：
- SRS 依据：
- 已确认事实：
- 不确定点：
- 影响范围：
- 推荐方案：
- 推荐依据与限制：
- 备选方案及差异：
- 当前实施状态：
- 需要产品所有者回答：
- 产品回答：
- 最终处理与提交：

## 4. 已回答问题

### Q-NUI-001：损坏内嵌 SF2 的再次保存表示

- 类型：大决定
- 状态：已确认
- 发现日期：2026-08-06
- SRS 依据：第 6.7.1、6.7.3、16.7.6、16.15.4、16.21.3、16.27.4 节。
- 已确认事实：内嵌 SF2 的 settings hash、manifest hash、未压缩实际字节及文件大小必须一致；导入与写包必须流式处理；hash 不匹配或资源损坏不阻止 Project 打开、不标记 Project 已修改；被动资源状态不得改写 `soundfont-settings.json`。
- 不确定点：SRS 原文没有定义 Embedded 引用仍存在而资源已损坏/缺失时，如何同时满足允许保存、三方内容身份一致和禁止被动改写 Project。
- 影响范围：`.midora` v1 文件兼容性、损坏恢复工作流、普通保存与 Save Copy 原子事务、自校验、资源诊断，以及未来 UI 对损坏资源的操作提示。
- 推荐方案：保存前必须由用户明确选择修复动作；无动作时持久化层返回结构化的资源修复错误且不发布文件。可选动作固定为重新绑定/替换内嵌 SF2，或明确清除 SoundFont 引用；修复后重新保存。
- 推荐依据与限制：保持 settings、manifest、实际字节三方一致，不静默改变 Project，也不生成新的已知损坏包。该决定是对第 16.15.4 节“允许保存”的产品解释：允许在完成显式修复选择后保存。
- 备选方案及差异：A. 原样保留损坏字节会故意生成三方不一致的新包。B. 省略资源会生成已知缺失包。C. 静默清空引用会被动改变 Project。D. 更新 hash/size 会把损坏内容当作用户主动接受。
- 当前实施状态：已实现 `MidoraEmbeddedSoundFontRepairRequiredExceptionV1`；明确携带资源状态、预期/实际身份和仅有的两个修复动作。Preflight 可识别缺失、损坏或错配租约；staging 可识别复制期间变化；两者都在发布前原子失败。
- 需要产品所有者回答：已回答。
- 产品回答：2026-08-06，采用推荐方案。
- 最终处理与提交：代码和测试已完成，提交 `4efb0f6`。

## 5. 人工试听 / 手动硬件测试问题

本节不是产品决定。只在自动测试无法替代时，集中记录可复制命令、前置条件、预期结果和产品所有者返回结果。

当前尚无待执行项目；将在音频工作包达到可验收状态后统一生成。
