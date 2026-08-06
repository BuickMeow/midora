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
- 当前实施状态：不分配 ID 的属性/设置命令、History、Modified/savepoint、branch、external dirty、锁和编译回滚已实现；所有 ID 分配型正式命令尚未接入 History。
- 需要产品所有者回答：是否采用推荐方案？如果不采用，请选择备选 A 或 B；C 不推荐且需要同时修改 Project 自包含不变量。
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
