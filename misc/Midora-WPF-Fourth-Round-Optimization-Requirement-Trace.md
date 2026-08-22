# Midora WPF 第四轮优化需求追踪

日期：2026-08-14  
输入：`misc/user/第四轮优化需求.md`

## 1. 输入、输出与所有权

- 正式输入仍是 Project 中的 Event Instrument、SubVoice、Template Event、Logical Parameter Definition、Segment Parameter Lane、Mapping Function 与稳定 ID。
- Event Instrument、SubVoice、事件点、配置字段和 Logical Parameter Lane 的修改只能通过 Application Project Edit Command 原子提交，并进入统一 Undo / Redo；悬停、轨迹、活动页签和活动 Lane 仍只属于 Workspace Session State。
- Canonical Compiled Result 仍是播放、预览、MIDI 导出和音频渲染的唯一正式输入；本轮不允许 WPF 绕过 Compiler 解释 MIDI 事件。
- 本轮产品需求显式允许开发期破坏旧数据兼容性。实现不增加旧 Curve UI、旧剪贴板负载或旧工程格式迁移分支；当前保留的内部类型若未被新 UI 生成，不构成兼容性承诺。

## 2. SubVoice 导航、结构编辑与配置

- 双击左侧 SubVoice 条目，右侧切换到 `SubVoices` 主页并激活该 SubVoice；单击仍只改变选择。
- SubVoice 右键菜单提供 Delete、Duplicate、Cut、Copy、Paste。Copy / Cut payload 必须包含一个完整 SubVoice 源快照；Paste 在目标 Event Instrument 中重新分配全部对象稳定 ID，并允许跨 Event Instrument 粘贴。Cut 只有在 payload 已成功建立后才删除源对象。
- 删除旧的 `Select Instrument in Inspector` 入口。原 Lifecycle 页更名为 `Configurations`，按 General、Template、Routing / Isolation、Lifecycle 分组直接编辑 Event Instrument 配置；Lifecycle 仍是其中一个独立分组。

## 3. SubVoice MIDI 事件点编辑

- 非 Note MIDI 编辑统一投影为二维实际事件点：横轴 tick，纵轴为目标值；同一精确 MIDI target 在同一 tick 最多一个点。画面中显示的点就是 Project 中的 Template Event，不生成插值 Curve 或自动离散化结果。
- `Add Curve` 从正式 UI 移除。唯一入口 `Add Event…` 位于 Event Lane 顶部右侧；Piano Roll 顶部保持单行工具栏，下部页签命名为 `Event Lane`。
- 点上左键拖动只调整该点值；空白处左键拖动绘制自由轨迹；`Alt + Left Drag` 即使命中密集点也强制轨迹；右键拖动绘制直线。轨迹只在手势期间保存在 transient overlay，MouseUp 后按 Snap tick 生成或更新实际 Template Event，并作为一个 Undo 提交。
- Lane 身份包含事件种类及完整 target（CC number、RPN/NRPN number 或对应字段），不得只按粗粒度 `TemplateEventKind` 合并不同 CC。

## 4. MIDI CC 支持与显示

- CC 名称和可选范围以 BASSMIDI 2.4 官方 MIDI implementation chart 为事实来源。
- Midora 初版只暴露官方列为 recognized、且满足普通可编辑 CC 边界的编号：`0, 1, 5, 6, 7, 10, 11, 32, 38, 42, 64, 65, 66, 67, 71, 72, 73, 74, 75, 76, 77, 78, 84, 94, 98, 99, 100, 101`。
- CC120～127 属于模式或全局清理，不作为普通用户事件；CC91 / 93 按 Midora NOFX 基线继续完整禁止。Bank 与 RPN / NRPN 仍保留专用事件类型，但不会把官方明确 recognized 的 CC0 / 32 / 6 / 38 / 98～101 从普通 CC 选择器中静默删除。
- 所有 CC 选择、Lane 标签和对象所属 Properties/Details 显示使用 `number - name`。内置目录是冻结的产品数据，不在运行时联网。

## 5. Lane、对象属性、Mapping 与窗口验收

- SubVoice Event Lane 的选项集合在模型刷新后必须发出集合/属性通知；新建事件后立即选择对应精确 target Lane。
- Segment Parameter Lane 下拉框投影当前绑定 Event Instrument 的全部 Logical Parameter Definition，而不是只投影已含点的 Segment Lane；选择尚不存在的定义时原子创建 Lane 并激活。
- SubVoice Piano Ruler 中当前有效 Root Note 对应键使用低强调浅红底，其余黑白键样式不变。
- Mapping Function 的代码编辑器填满可用垂直空间并从左上角开始排版；Find 输入框不裁切。
- 当轮 Inspector 的 Boolean 值使用 CheckBox 并提交 bool，不再接受自由文本。该编辑能力已于 2026-08-22 迁移到对象所属 Properties；生产 Global Inspector 已删除。
- 所有自定义 Dialog 使用共享暗色 WindowChrome / caption 契约；内容区通过合理 MinHeight 与可滚动布局保证底部操作按钮可见。白色原生顶边、裁切按钮均视为失败。

## 6. 失败条件与非目标

- 跨 Event Instrument Paste 若目标 Project 不可编辑、payload 损坏或复制出的内部引用不能确定地重映射，必须在修改前整体拒绝。
- Event 轨迹超出模板 tick 或 target value 范围时按正式范围裁剪；稳定 ID、同 tick 唯一性和一次手势一次 Undo 不得破坏。
- 本轮不改变 Note、Velocity、Segment、音频 Worker、SoundFont、缓存、Mute / Solo 或 canonical consumer 的既有语义。
- 本轮不为旧 Curve UI、旧 clipboard payload 或旧 `.midora` 数据建立迁移与回退；若旧数据仍因现有底层代码偶然可读，不视为受支持能力。

## 7. 后续修正：SubVoice 导航与共享 Mapping

- 左侧 SubVoice 双击必须在当前 routed input 退栈后，以稳定 ID 重新选择目标 SubVoice，并把当前 Event Instrument 工作区的内层页签切换到 `SubVoices`。内层页签位于 DataTemplate namescope，不能依赖跨 namescope 的 `FindName`；页签索引由当前 `InstrumentWorkspaceViewModel` 的 session state 双向绑定，双击直接更新该状态。
- Mapping 的正式所有者改为 SubVoice。一个精确事件标量目标只有一条 Mapping Chain：Note Number / Velocity、Bank MSB / LSB、Pitch Bend Range Semitones / Cents 分开；CC、RPN、NRPN 的 number 属于 Lane 身份，因此不同 number 不合并。
- 新增一千个同一 CC target 的事件点时，除事件点自身稳定 ID 外不得再按点分配 Mapping Chain；Mapping 数量只随精确目标种类增长。编译、校验、Project fingerprint、持久化、SubVoice 完整复制和 Mapping 列表均按共享集合处理。
- Timeline 事件点 clipboard 不携带 Mapping 定义；粘贴后使用目标 SubVoice 对应的共享 Mapping。完整 SubVoice clipboard 携带且只携带一次共享 Mapping 集合。旧 per-event Mapping protobuf 与 clipboard 负载不保留兼容读取能力。

## 8. 后续修正：Event Instrument 结构栏与 Mapping Function 布局

- Event Instrument 左侧结构栏的 SubVoice、Logical Parameter、Parameter Mapping、Mapping Chain、Mapping Step、Envelope Preset 与 Mapping Function 均提供单项 Cut / Copy / Paste / Delete 右键入口；SubVoice 继续额外保留 Duplicate。
- 本轮产品要求显式覆盖当时 SRS 20.3 的异构多选默认：该左侧结构栏在所有类别之间共用一个视觉焦点和一个对象属性 primary，任一次左键或右键命中都执行 Replace，并清除其他结构列表遗留的 `SelectedItem`。2026-08-22 后该 primary 只驱动 Event Instrument 本地 Properties，不再驱动 Global Inspector；时间线、钢琴卷帘和事件点的多选语义不受影响。
- 定义类 payload 是不可变快照；粘贴 Logical Parameter、Envelope Preset、Mapping Function 时分配全新稳定 ID，并作为单次 Undo 原子提交。Mapping Step 粘贴到目标 Chain 的选中 Step 后方或末尾。Mapping Chain 继续复制/替换有序链内容；不可删除的 Note Chain 同时禁止 Cut。
- Logical Parameter Mapping 的 target 身份必须保持唯一，因此 Copy / Paste 只把 Target Settings 与 Mapping Chain 配置替换到显式选中的目标 Mapping，不复制其 Parameter / SubVoice / MIDI target 身份，也不创建重复 Mapping。
- Mapping Chain 的 Delete 直接使用右键命中的 Chain ID，不再依赖其他结构列表残留的 primary；非空 Chain 继续要求确认并按既有正式命令清空，Note Chain 不允许删除。
- Mapping Function 编辑器显式覆盖通用单行 `TextBox` 的固定 `Height=34`：代码区使用 `Height=Auto`、Stretch 和顶部内容对齐以占满 `*` 行。Find 工具栏改为内容自适应高度，并使用通用 34 px 输入框/按钮，避免固定 40 px 父行加内边距后裁掉底边。
