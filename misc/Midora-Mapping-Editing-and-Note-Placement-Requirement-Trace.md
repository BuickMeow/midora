# Midora Mapping 编辑与 Note 放置优化 Requirement Trace

状态：Implemented
日期：2026-08-15
范围：Event Instrument Mapping 编辑、Instance Velocity 语义例外、Segment/SubVoice Piano Roll Note 放置

## 1. 需求依据与冲突记录

- 用户明确要求：Step 的 Parameter/Envelope/Function 引用使用对象下拉；Parameter Mapping 可修改 route 并重排；Mapping Chain 有完整属性编辑区；Add Step 只显示当前链上下文合法的 Source。
- 用户明确要求：无 Per-Note Instance Isolation 时允许 `TriggerVelocity → Note Velocity`，并新增默认开启的 `Follow Instance Velocity`。
- 用户明确要求：Draw 空白创建 Note 时，按住后可上下拖动改变 Key，并通过既有独立 pure-Note preview route 试听。
- SRS 8.53.3 的 fixed template velocity 默认、SRS 9.9 的 blanket isolation、SRS 18.2.4 的 placement 不发声规则与本次明确决定冲突。本轮按产品所有者的新决定实施并记录 ADR-UI-031/032；不静默修改 SRS 原文。

## 2. Requirement Trace

| 项目 | 正式约束 |
|---|---|
| 输入 | Event Instrument、精确 Mapping Chain owner/target、SubVoice、Mapping Step、Logical Note instance velocity；Piano Roll pointer position 与 Editor Snap。 |
| 正式输出 | 所有 Mapping 修改仍通过 `IProjectEditCommand` 修改 Project Source Data；Compiler 产生 Canonical Compiled Result；Note 放置在 MouseUp 产生一个 Note 创建 command。 |
| UI 投影 | 对象所属 Properties choice 的 value 保存 Stable ID，label 显示对象名称；UI 不按名称建立身份。Parameter Mapping route 修改与重排分别是原子 History 项。 |
| Source 过滤 | 由精确 chain target 和 Isolation 状态计算；UI 不提供已知必定非法的 Source/Function，但 Semantic Validator仍是正式门。 |
| Velocity 例外 | 只允许共享 `TemplateEventKind.Note + Value` 链直接使用 `TriggerVelocity`；其他 per-note source/target 组合保持 `MIDORA1214`。 |
| 默认 Follow | 新建默认 SubVoice/new SubVoice 创建普通 `TriggerVelocity / Override` Step；复制与加载保留源链，不重置默认。 |
| 边界 | Note Number 有任何 active Step 且无 Isolation 仍失败；Envelope、per-note C# context、Loop、Let Overlap 等既有 Isolation 约束不变。 |
| 放置范围 | Segment Logical Note 与 SubVoice Template Note Piano Roll；pitch 始终 clamp 为 0..127，最终 length 至少 1 tick。 |
| 试听运行时 | 复用持久音频 Worker 内独立 1-channel pure-Note stream；pitch 变化执行 All Sound Off + NoteOn，结束执行 NoteOff + All Sound Off。 |
| 失败条件 | 引用对象消失、目标非法、Note Number overflow policy 非 Fail、Stable ID 重复、音频 preview 不可用；Project command必须失败原子。 |
| 诊断 | 非法持久数据继续由现有 Mapping/Isolation diagnostics报告；UI 过滤不新增伪 canonical 诊断。试听失败属于 runtime error，不改变 Note 创建数据。 |
| 持久化归属 | Mapping Chain/Step 属于 `.midora` Project Source Data；Follow 不新增字段。对象下拉、hover、draft Note 和 audition 状态不持久化。 |
| 非目标 | 不放宽 TriggerVelocity 到 CC/Program/RPN/NRPN/Pitch/Note Number；不改变 Channel Unit 分配；不让 UI 绕过 Compiler；不引入每事件 Mapping Chain。 |

## 3. 验证门

- Compiler：无 Isolation 的 TriggerVelocity→Note Velocity 可消费并输出 instance velocity；同 Source→CC 仍产生 `MIDORA1214`。
- Application：Parameter Mapping route 一次原子修改/Undo；Mapping Chain target settings；Follow preset enable/disable/Undo；默认创建与复制保持确定 Stable ID。
- Desktop：Release build/XAML compile；对象所属 Properties choice 使用 Stable ID value + display label；Source policy 覆盖 Note Number、Note Velocity、非 Note 与 Logical Parameter chain。
- Piano Roll：最终创建使用 MouseUp 时的 pitch；跨 lane 只在 pitch 实际变化时发试听更新；取消路径结束试听且不提交。

## 4. Mapping Function 编辑器优化前纠正（2026-08-25）

| 项目 | 正式约束 |
|---|---|
| 输入 | ABI v2 `CSharpMappingFunction`、Mapping Step 当前累计值、声明的 Context Fields、Logical Parameter / SubVoice Event Mapping 上下文。 |
| 正式输出 | `CustomCSharp` 固定以当前累计链值作为 `Transform(value, in context)` 的 `value`；其 Mapping Step `Source` 不参与求值。所有 Mapping 上下文均填充正式的 `EffectiveRootNote`。 |
| 编辑规范化 | 新建或修改 `CustomCSharp` Step 时将无意义的 `Source` 规范化为 `CurrentValue`；不改变持久化格式。旧数据中的其他 Source 仍可读取，但编译时忽略；Undo 恢复编辑前的精确旧值。 |
| UI | Properties 明确标注 Source 仅用于内建 Operation，并说明 Custom C# 接收当前累计链值；本轮不加入语法着色、括号配对或补全。 |
| 失败条件 | Function 缺失、ABI/源码编译失败、返回非有限值、用户代码抛出异常，继续使正式编译失败。 |
| 诊断 | 运行异常诊断保留 Mapping Function / Step / 来源对象定位，并显示实际异常类型和消息；不吞掉异常原因。 |
| 持久化归属 | Function 源码、声明 Context Fields 与 Step 引用仍属于 Project Source Data；Roslyn 产物和运行异常不持久化。 |
| 运行时归属 | Function 编译与执行仍属于 Compiler/Mapping Engine；不进入音频 callback，也不改变 canonical consumer。 |
| 非目标 | 不变更 ABI v2、不自动推断 Declared Context Fields、不引入脚本 sandbox、不实施代码编辑器体验增强。 |

补充说明：SRS 第 9 章已经规定 Custom C# 的 `value` 为当前累计值、Mapping Context 包含 Effective Root Note，以及运行异常必须失败；本节只是纠正实现与 UI，不修改正式语义。

## 5. Mapping Function 正式编辑体验优化（2026-08-25）

| 项目 | 正式约束 |
|---|---|
| 输入 | 独立 Draft Body、固定 ABI v2 参数 `value` / `context`、`MappingContextV2` 公开属性、固定 Contract 枚举、`System.Math` 公共静态成员。 |
| 编辑器 | 使用内嵌多行 C# 编辑器，提供 C# 词法着色、行号、`()` / `[]` / `{}` 配对输入与高亮、未配对错误高亮、查找和本地文本 Undo / Redo。代码着色复用 Batch Edit 的暗色 token 调色板，不使用 AvalonEdit 内置浅色 C# 主题；括号与补全必须忽略字符串及注释。 |
| 补全 | `Ctrl+Space` 或标识符输入触发；支持 ABI 参数、`context.` 全部正式属性、`Math.` 方法/常量和 Contract 枚举成员；Enter/Tab 接受，Esc 关闭，F1 轮换重载签名。补全项从实际 ABI 类型和 `System.Math` 反射生成，不维护平行字段清单。 |
| Context 声明 | 补全只插入源码成员，不自动修改 `DeclaredContextFields`；属性说明明确提示用户同步声明依赖，正式 Semantic Validation 继续负责未知/缺失声明。 |
| Draft/提交 | 键入、补全、查找和本地文本 Undo / Redo 只改变 Workspace Draft；`Compile Draft` 只验证；`Apply` / `Ctrl+S` 才通过既有原子 Project command 提交。 |
| 失败条件 | ABI、引用、源码、非有限返回值及运行异常规则不变；编辑器不吞掉 Compiler diagnostic，也不提供与正式 profile 不一致的伪成功。 |
| 持久化归属 | 只有 Apply 成功后的 Function Body / Name / Declared Context Fields 属于 Project Source Data；补全列表、光标、括号高亮和弹窗状态不持久化。 |
| 非目标 | 不改变 ABI v2、Roslyn/C# profile、允许引用集、collectible ALC、Mapping 求值或 canonical consumer；不引入 sandbox、语言服务进程或自动修改 Context 声明。 |

## 6. Mapping Function 能力收缩与安全增强（2026-08-25，取代第 4～5 节中 ABI/语言/安全边界）

| 项目 | 正式约束 |
|---|---|
| 输入 | Project 内的 Mapping Function 单行表达式、ABI v3、当前累计 `value` 与只读 `MappingContextV2`。Batch Edit 表达式属于非 Project 工具数据，明确不在本次范围。 |
| 正式输出 | 经精确白名单验证和绑定的 `double` 表达式委托；输出仍进入既有 Mapping Chain、取整/越界和 canonical 编译主线。 |
| 语言边界 | 只允许字面量、`value`、批准的数值/枚举 Context 字段、算术/比较/布尔/条件运算、批准的 Contract 枚举和纯数值 `Math` 成员。禁止语句、循环、赋值、lambda、对象创建、任意 API、字符串、名称、Stable ID、反射和副作用。 |
| 资源边界 | 单行、最多 8,192 Unicode scalars、512 syntax nodes、64 depth；不存在用户可构造的循环/递归执行结构。 |
| Context 依赖 | Validate/Apply 自动推导；用户不手工填写。持久化集合参与 fingerprint，但正式编译重新推导并要求精确一致。 |
| 执行 | Roslyn 只用于 Expression 语法解析；正式 binder 逐节点建立 `System.Linq.Expressions`，不 Emit/加载 Project 源码程序集，不开放运行机器引用面。 |
| 兼容 | ABI v1/v2 自由 C# 只可识别并明确诊断，绝不执行，也不保留隐藏迁移执行器。重新编辑并 Apply 生成 ABI v3。 |
| 缓存 | 键为 ABI/profile/精确源码 UTF-8 SHA-256；只缓存当前 Project 仍存在的修订，切换/关闭/删除时释放，不持久化。 |
| UI | 单行换行式编辑、暗色着色、括号高亮、查找和白名单补全；按钮为 `Validate`，说明依赖自动推导。 |
| 诊断与测试 | 明确定位 Function/Step/来源；覆盖合法求值、Full/Incremental、旧 ABI、任意 API、语句、lambda、对象创建、赋值、名称/ID、超长/超深与非有限输出。 |
