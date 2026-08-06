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

### Q-NUI-009：Logical Parameter 类型、范围与 Enum 结构变更时的既有 Lane 迁移

- 类型：大决定
- 状态：待确认；只暂停会使既有 Lane/Enum 数值失配的 Definition 变更分支
- 发现日期：2026-08-06
- SRS 依据：第 9.8.2、9.8.10～9.8.12、11.12.3～11.13.4、11.19.2～11.19.5、18.5.2 节。
- 已确认事实：Logical Parameter Definition 可编辑名称、Integer/Double/Enum 类型、defaultValue、legal/display range 与 Enum items，修改必须进入全 Project Undo/Redo；Lane 按稳定 Parameter ID 绑定，正常重命名不破坏绑定。Integer、Double、Enum 对点值与插值有不同合法性，Enum 只允许已定义整数值与 Step。SRS 已为“把一条 Lane 显式重绑定到另一个 Parameter”规定 Clamp/Discard 和 Enum 语义警告，但没有把该规则扩展到“原 Parameter Definition 自身改变”。
- 不确定点：当类型、legal range、Enum 显式模式/数值/顺序/删除使当前 Project 中一条或多条既有 Lane Point 不再合法时，系统应拒绝 Definition 修改、保留失配数据并让编译失败，还是原子转换所有引用 Lane；若转换，还需决定逐 Lane 还是整次操作统一选择 Clamp/Discard、Double→Integer 中点、Linear→Enum、隐式 Enum 重排和删除项的语义。
- 影响范围：可能跨所有绑定同一 Event Instrument 的 Logical Track/Segment 批量改写 Lane Point，对默认状态、插值、Mapping 输出和可听结果产生大范围影响；还涉及 Undo 快照体积、诊断、复制、持久化源数据及未来 UI 确认工作流。稳定 ID 和 protobuf 字段可保持不变，但数据语义会改变。
- 推荐方案：Definition 编辑采用显式迁移计划并形成单个原子 History entry。仅重命名、合法 defaultValue、display range、不会使任何 default/Enum item/现有 Lane Point 失效的 legal range，以及不改变 Enum 数值身份的 item 重命名可直接提交。任何类型变更、Enum 显式模式切换、Enum 数值/顺序/删除或会使既有数据失效的 range 缩窄，都要求调用方明确选择 `Clamp` 或 `DiscardInvalidValues`，并对目标 Enum 确认语义警告；转换规则复用 Q-NUI-007 的 AwayFromZero、Enum 最近值等距取较小值和 Linear→Step，原子处理全 Project 所有引用 Lane。Enum item 稳定 ID 保留；被删除 item 的 Lane 数值按同一迁移策略处理。
- 推荐依据与限制：复用已有显式 Lane 重绑定规则，避免同一种不兼容转换出现两套舍入/Enum 语义；一次 Definition 修改与所有受影响 Lane 同事务，既不会留下中间非法状态，也能完整 Undo。限制是大 Project 的准备快照和转换成本较高，且 Enum 语义转换即使数值相近也不能保证音乐含义相同，因此必须显式确认。
- 备选方案及差异：A. 只要任何既有 Lane 会失效就拒绝 Definition 修改，要求用户先逐 Lane 修复；最保守但工作流繁琐，且多 Track 项目难以一次完成。B. 允许 Definition 修改但完全保留失配 Lane，让保存成功而 canonical 失败；最少改写数据，但一个高层接口编辑可使全工程不可播放，且恢复需逐点处理。C. 对所有失配值静默 Clamp/转 Step；操作简短但会在无明确授权下改变可听语义，不推荐。
- 当前实施状态：该迁移分支尚未实现。已经完成名称、合法 defaultValue、display range、不会使现有 Lane/Enum 失效的 legal range、Enum item 重命名、引用保留删除，以及 Logical Parameter Mapping source/target/order/共享 Target Settings/确认删除；range 缩窄若会使任何现有 Lane Point 失效会明确拒绝并指向本问题。自动测试同时证明 display range 不失效已编译 Track，所有可听编辑保持 Full/Incremental 等价。
- 需要产品所有者回答：是否采用推荐方案？如不采用，请选择 A 或 B，并分别说明类型变更、range 缩窄、Enum 模式/数值/顺序/删除的处理；C 不建议采用。
- 产品回答：待填写。
- 最终处理与提交：待填写。

### Q-NUI-010：空 SubVoice 是否产生 Info 诊断的 SRS 冲突

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 8.46～8.47、12.7.4 节。
- 已确认事实：两处都规定空 SubVoice 合法、不得自动忽略且仍计入 Channel Unit；第 8.47 明确要求实际编译使用时产生 Info，第 12.7.4 却明确写“空 SubVoice 是合法状态，不产生诊断”。Info 不导致失败，也不受 Warning-as-error 影响。
- 不确定点：同一 v0.1 SRS 对是否存在 Info 给出直接相反要求；不能同时满足。
- 影响范围：只影响实际参与实例的空 SubVoice 是否出现在编译诊断/未来诊断面板，以及相关自动测试；不改变资源占用、canonical MIDI、播放、导出、音频、持久化、Modified 或 Undo/Redo。
- 推荐方案：保留当前 `MIDORA1225 / Info`，仅当所属 Event Instrument 确有范围内实例时产生，并精确定位 Track/Instrument/SubVoice；不升级为 Warning，不阻止消费。后续修订 SRS 时把第 12.7.4 的“不产生诊断”改为“不产生 Warning/Error”。
- 推荐依据与限制：第 8.47 是专门定义“空 SubVoice 的诊断”的细化条款，包含等级和资源浪费理由，比第 12.7.4 的概述更具体；该选择也已形成编译与逐 Track 静音音频测试。限制是当前实现与第 12.7.4 字面不一致，必须由产品所有者确认冲突解释。
- 备选方案及差异：A. 删除 Info，严格采用第 12.7.4；资源占用仍正确，但用户无法从诊断理解静音 Channel Unit。B. 只在资源接近上限时提示；SRS 没有该条件，且会让相同空结构的诊断依赖其他实例，不推荐。
- 当前实施状态：已按推荐保留 `MIDORA1225`，现有编译器和 Audio Render 测试锁定 Info、静音输出与 Channel Unit 占用。
- 需要产品所有者回答：是否采用推荐方案？如不采用，将删除 `MIDORA1225` 及对应测试，但不改变空 SubVoice 的资源需求。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-011：共享音频 Worker 状态快照的 ABI v2 并发契约

- 类型：大决定
- 状态：待确认；只暂停共享状态快照协议升级分支
- 发现日期：2026-08-06
- SRS 依据：第 2 章确定性/失败原子性原则、第 13.30 节内部音频子进程与热路径零分配要求、INV-018～INV-028；`misc/Midora-Audio-Backend-Architecture-Decisions.md` 的 ADR-AUDIO-005。
- 已确认事实：当前共享内存 ABI v1 对每个对齐的 `Int32/Int64` 字段分别使用 Volatile 读写，且 Worker 是状态单写者；这能避免单字段撕裂，但不能保证包含 State、Position、RenderPosition、Underrun 和分配计数的整组快照来自同一次发布。发布方连续写两次相同 Playing/Rendering 状态时，读取方可能组合前一次与后一次字段；当前 v1 的 offset 68 被定义为必须为零的 reserved 字段，不能在仍声称兼容 v1 时静默改作序列号。
- 不确定点：初版尚未正式发布，SRS 没有固定内部共享 ABI 的版本号，也没有指定使用 seqlock、双缓冲还是允许仅保证逐字段一致。选择会改变进程间并发契约、协议兼容和确定性验证方式。
- 影响范围：实时播放、设备 Probe、离线渲染的状态/进度/故障读取，Native AOT Worker 与主进程的二进制兼容，零分配热路径和并发压力测试；不改变 Project、canonical、MIDI/WAVE 文件、可听语义或 UI 公共业务模型。
- 推荐方案：在初版发布前把共享控制协议升级为 ABI v2；保留总大小和其余 offset，把 offset 68 明确定义为对齐的 32-bit `statusSequence`。单一 Writer 每次发布先以原子增量变为奇数，再写完整字段，最后以 release 写/原子增量发布下一个偶数；Reader 读取偶数序列、复制全部字段、再次读取序列，只有两次相同且为偶数才接受，否则在固定上限内无分配重试，超过上限作为 IPC 一致性故障。Create 只建立 v2，Open 只接受 v2，主进程与 Worker 不做混合版本回退；压力测试验证从未观察到跨代组合、序列 wrap 不破坏相等判定、读写热路径零分配。
- 推荐依据与限制：seqlock 适合当前单 Writer、多次无锁 Reader 的小型固定快照，不增加映射大小，也不在音频热路径加锁或分配；显式升 v2 保持版本声明诚实。限制是极端持续写入时 Reader 可能达到重试上限并使任务失败，因此需要选择足够高且有界的上限；协议 v1 的测试 Worker 与 v2 主进程将被明确拒绝，必须同版本部署。
- 备选方案及差异：A. ABI v2 使用双状态槽加活动索引；Reader 更容易取得稳定槽，但要扩大并重排共享布局，复制/验证面更大。B. 保持 v1，只把 State 视作最后提交标志并允许同状态发布的字段跨代组合；兼容面最小，但无法证明整组进度和计数来自同一次发布，不推荐。C. 给状态读写加跨进程锁；可提供强快照，但阻塞与故障进程持锁风险不符合热路径约束，不推荐。
- 当前实施状态：已确认 v1 存在跨发布混合快照风险；未修改 `ProtocolVersion`、reserved 字段或读写算法。其余命令 ring、字段值域、故障传播和 Worker 生命周期加固继续进行。
- 需要产品所有者回答：是否采用推荐的 ABI v2 seqlock？如不采用，请选择双缓冲 A，或明确接受 B 的弱快照；C 不建议采用。
- 产品回答：待填写。
- 最终处理与提交：待填写。

### Q-NUI-012：非 UI 发布门的精确 .NET SDK 与 NuGet 锁定策略

- 类型：小决定
- 状态：已按推荐实施待确认
- 发现日期：2026-08-06
- SRS 依据：第 1 章/.NET 10 技术边界、第 21.3 节正确性与确定性优先级、INV-027；Mapping ABI 的 `Microsoft.NETCore.App.Ref 10.0.10` 仍由 INV-029 独立固定。
- 已确认事实：仓库全部项目目标框架为 `net10.0`，此前没有 `global.json`、NuGet lock files 或单命令非 UI 发布门；同一工作树会使用机器默认 SDK和当次解析出的传递包图。当前完整验证环境安装并使用 `.NET SDK 10.0.302`，对应 .NET 10.0.10 runtime/reference pack；所有直接 PackageReference 已有显式版本。
- 不确定点：SRS 固定 .NET 10 和 Mapping reference pack，但未固定一般项目的 SDK feature band、是否允许 patch roll-forward、是否提交每项目 NuGet lock file，也未规定开发期可移植测试缺少原生 BASS/SF2 时应失败还是 Skip。
- 影响范围：开发/CI 机器准备、依赖还原、编译器与 Native AOT 产物可复现性、测试发现完整性和发布门维护；不改变 Project 文件、canonical、MIDI/WAVE、运行时用户设置或音乐语义。
- 推荐方案：提交 `global.json`，精确使用 SDK `10.0.302`、`rollForward=disable`、禁止 prerelease；仓库级声明 `RuntimeIdentifiers=win-x64` 与 `RestorePackagesWithLockFile=true`，提交 32 个 `packages.lock.json`。普通开发测试在未配置原生集成资源时明确 Skip；正式 `Test-NonUIRelease.ps1` 必须显式给出经固定 manifest/hash 验证的 BASS 目录和一个现存 SF2，执行 locked restore、六个 solution Release build、Native AOT publish，再按版本化测试基线要求 10 个项目精确 760 项全部通过且零 Skip。
- 推荐依据与限制：精确 SDK和锁文件把构建输入从机器隐式状态变为提交内容；零 Skip 的正式门避免把缺少硬件/资源误报为通过。限制是安装了其他 .NET 10 SDK但没有 10.0.302 的机器会在仓库根目录直接拒绝构建，安全升级 SDK/包时必须显式更新 `global.json`、lock files、基线并重跑完整门。
- 备选方案及差异：A. SDK 使用 `latestPatch` roll-forward，安全补丁采用更方便，但不同时间/机器可能产生不同 AOT 与编译输出。B. 只固定直接包版本、不提交 lock files，文件较少但传递图仍可变化。C. 不固定 SDK，仅在发布记录中手工写版本；日常构建仍可能漂移，不推荐。
- 当前实施状态：已按推荐实现并在本机完整运行发布门；760 tests 全通过、0 Skip，固定 BASS 校验通过，Native AOT Worker 产物包含 `.exe`、三项 DLL、native manifest、MIT License 与 Third-Party Notices。
- 需要产品所有者回答：是否采用推荐方案？如需允许 SDK patch roll-forward，请明确选择 A；NuGet 锁文件与正式零 Skip 门建议保留。
- 产品回答：待填写。
- 最终处理与提交：待确认后填写。

### Q-NUI-013：正式分发 BASS 二进制的发布主体与许可放行

- 类型：大决定 / 外部发布门
- 状态：待确认；只暂停包含 BASS/BASSMIDI/BASSWASAPI DLL 的正式对外分发
- 发现日期：2026-08-06
- SRS 依据：第 21.6 节、INV-037、INV-038；根目录 `THIRD-PARTY-NOTICES.md`。
- 已确认事实：Midora 自有代码使用标准 MIT License，初版产品定位为免费、开源、非商业；该定位不把 BASS 变成开源依赖，也不自动证明任意发布主体满足 Un4seen 的免费使用条件。仓库不提交 DLL；当前技术发布门只从操作员目录复制三项固定版本/hash DLL到本地测试产物，并明确打印“分发授权仍是独立门”。
- 不确定点：尚未获得实际正式发布主体（个人/组织及其商业性质）、Midora 是否通过销售/广告/订阅/付费分发或其他方式获利、计划发布渠道与分发方式、正式发布日期有效条款的复核结论，以及要随包提供的供应商原始许可文件清单。技术实现不能代替权利人授权判断。
- 影响范围：任何包含 `bass.dll`、`bassmidi.dll`、`basswasapi.dll` 的 GitHub Release、安装包、压缩包、镜像或其他对外分发；不影响仓库 MIT 源码发布、不含 BASS 的构建、用户自行提供 DLL 的开发测试、编译/MIDI/持久化等非发声功能。
- 推荐方案：正式分发前由产品所有者冻结并书面记录：发布主体法定/公开身份及非商业性质；产品全部收入模式为无销售、无广告、无订阅、无付费分发；平台仅 Windows win-x64；具体渠道和是否由第三方镜像；按发布当日官方条款确认免费资格；把供应商要求的原始许可文本与现有 notices 一并纳入最终包。若任一事实不明确、发布主体具有商业性质、未来引入收入或条款解释有疑问，先联系 Un4seen 取得书面确认或购买适用许可，再放行含 DLL 产物。
- 推荐依据与限制：该方案严格执行 SRS 已确认的许可边界，不用项目“开源/免费”口号替代第三方授权。限制是实现方无法仅凭源码和自动测试自行完成主体资格、收入和届时条款的法律/商业事实核验；最终许可判断应由发布主体承担，必要时咨询专业人士或权利人。
- 备选方案及差异：A. 正式发布包不含任何 BASS DLL，仅提供校验工具和用户自行取得/配置流程；避免仓库方重新分发二进制，但首次使用流程更复杂，且仍需核验实际使用条件。B. 取得商业/其他明确许可后随包分发；成本与条款由权利人决定。C. 仅凭当前非商业声明直接随包分发；无法闭合 SRS 要求的主体/收入/渠道/届时条款核验，不可采用。
- 当前实施状态：技术构建、固定 hash/version 校验、runtime version gate、MIT/Third-Party Notices 复制和本地 Native AOT 测试产物均已实现；没有创建或推送正式发行包，也没有把 BASS DLL提交到 Git。
- 需要产品所有者回答：请提供实际发布主体、主体商业/非商业性质、全部收入方式、计划平台/渠道/分发形式，并选择推荐方案、A 或 B；在这些事实和届时条款核验完成前，本分支保持不放行正式含 DLL 分发。
- 产品回答：待填写。
- 最终处理与提交：待填写。

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
