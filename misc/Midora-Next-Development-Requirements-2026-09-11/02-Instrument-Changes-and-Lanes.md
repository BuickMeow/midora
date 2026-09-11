# 乐器变化点、Lane Tabs 与事件呈现

覆盖 R27（P0）、R28（P1）、R12/R29（P2）；关联 R24/26、R06/07。基线 `0bb9670`，只读设计调查；方案须按[决策清单](04-Decisions-and-Preparation.md)定案后再修改规格和代码。

## 1. 目标与不能误改的底层能力

核心目标是让已存在的 Instrument Catalog 真正帮助选音色：在 MIDI Segment / SubVoice 直接按 Bank、Program名称选取并试听，而不是只在全局配置名称后仍四处填数字。

用户明确要求保留 Pure MIDI 独立 CC0、CC32、Program Change 的原始编辑和导入兼容。新“乐器变化点”是包装体验，不是批准把所有导入事件自动改写成新模型；用户允许更合理方案，不要求采用“开始下标 + 长度”关联。

不变事项：

- Project / canonical / MIDI 输出仍用正式数值与顺序；Catalog名称不成为资源身份或编译输入。
- 不依赖某个用户安装的Catalog / SoundFont才能打开项目；缺失只数值回退。
- 非音符事件精确碰撞仍遵守正式编辑合同；导入已有重复在未被相关编辑命中时保留。
- Bank MSB/LSB、Program以及SubVoice精确Mapping目标的既有能力不能在合并UI时丢掉。
- 新试听必须有独立所有权，不阻碍编辑、不错误停止正常项目播放。

## 2. 源码现状与可复用边界

下表路径相对仓库根；行号冻结于审计基线。

| 能力 | 来源 | 已确认现状 / 限制 |
|---|---|---|
| Bank/Program名称 | `src/midora-core/Midora.Application/InstrumentCatalogResolver.cs:108/155` | 有正式数值→名称及来源resolver；没有统一可选 / 试听弹窗 |
| Catalog规模 | `InstrumentCatalog.cs:405` | 上限256 Profile、每Profile16,384 Bank、每Bank128 Program、文件64MiB；不能穷举所有三元组生成WPF项 |
| 现有Initial State | `src/midora-desktop/Midora.Desktop/PresentationModels.cs:3727/3749`；`ObjectPropertiesProjection.cs:1821/1866` | 三个独立数值字段，部分Properties有辅助只读名称；需要完整三值才解析 |
| 旧验收前提 | `Midora.Desktop.Tests/InstrumentCatalogPropertiesProjectionTests.cs:88` | 有SubVoice Program Properties只保留数值的测试，新需求应有意识替换该前提 |
| 琴键表面 | `Midora.Desktop.Presentation/Controls/PianoKeyboardSurface.cs:17/59/71` | 可复用Key/Velocity事件及键范围；滚动宿主、音色选择、试听调度不包含在内 |
| 原始列表 | `Midora.Desktop/TimelineObjectListSource.cs:11/110/135` | 一正式源对象一行；已有稳定ID、formal order、分页目录；包装行需新投影与选择映射 |
| 现有Lane | `MainWindow.xaml:684/939`；`PresentationModels.cs:2397/2484` | LANE下拉切目标；内容发现带后台revision gate，默认kind/number排序，并非用户Tab顺序 |
| SubVoice Lane命令 | `src/midora-core/Midora.Application/ProjectSubVoiceEventLaneEditCommands.cs:7/161` | 创建可能建正式SubVoiceEventMapping owner；删除可能删数据和owner，不等于关闭Tab |
| Tempo阶梯参考 | `Midora.Desktop.Presentation/Rendering/TimelineTempoTileRasterizer.cs:10/43/90` | 有前驱、first/last/min/max、水平线、竖跳和取消；绑定Conductor source，不能不改接口直接用于全部事件 |

### 2.1 两个新增工作前必须核实的技术风险

**事件索引风险。** `TimelineEventTargetIndex.cs:31–57/65–112` 会按修订全源扫描、建立目标List、每点数组、多组prefix数组和树；同Tick按ID排序，point未携带正式order。因此不是可直接宣布“百万级有界且语义正确”的状态索引。新投影应使用分页摘要 / 有界范围索引，保留formal order，不能为阶梯线再常驻一份全量事件图。

**试听风险。** `DesktopSessionController.cs:971` / `Midora.Playback/PlaybackController.cs:334` 的 BeginPitchAudition只收pitch/velocity。`Midora.Audio.Bass.Worker/Program.cs:1713–1754/1812–1938` 的裸音高路径直接SOUNDOFF/NOTE、voices有固定500值，其render source中未见Master/Limiter处理。这里是静态审计风险，不是本轮已证实的可听BUG；不能把它描述为“只加三条事件即可安全复用”的完整方案。实施R27前需核对完整调用链，并对新的试听路线做正式安全验证。

## 3. R27：建议架构

### 3.1 数据唯一来源与包装身份（待 D-IN01）

优先候选：**保留raw事件作为音乐事实，显式创建的组合拥有稳定关联；UI展示包装点。**

| Owner | 新完整包装所关联的正式成员 | 不改变的能力 |
|---|---|---|
| Pure MIDI Segment | CC0、CC32、PC三个稳定事件ID | 原始事件类型、正式order、独立编辑、opaque、导入重复 |
| SubVoice | 一个具有MSB/LSB的Bank Event、一个Program Event的稳定ID | Bank.MSB、Bank.LSB、Program.Value精确Mapping目标及既有Loop行为 |

不能用列表起始ordinal、连续长度、Tick或名称充当身份。分页、重排、碰撞和Undo均可能改变序列位置；成员ID可稳定重新解析，wrapper自身也需稳定身份以支持选中 / Properties。

此关联不复制音符 / 事件内容，不建立第二份可独立修改的音乐数值。创建 / 变换 / 删除包装只通过正式事务处理成员，保留source trace。小列表投影也不能绕开这一事务。

另一候选是新增真正 `InstrumentChange` 源事件，由Compiler展开。它能表达整体操作，但扩大Domain、Mapping ABI/context、canonical来源、wire及兼容面；目前没有证据表明首版必须走这条更重路线。

**持久化必须决定：** 显式包装如果要求重开后仍作为一个可编辑对象，关联究竟是正式编辑组织元数据，还是允许损坏丢失的presentation？建议把会改变选择/删除单位的显式关联作为严格版本化编辑组织数据，并要求它不改变raw播放顺序；代价是需评估新source契约。也可采用仅presentation关联、失效退回raw，但要接受包装体验在旧版本保存后消失。不能本轮未经批准塞进既有schema。

### 3.2 导入、同Tick与共享Root的底线

- 默认不自动包装任意导入Bank/PC序列；仅把用户在新入口明确创建的组合包装显示。导入轨道即使有CC0/32/PC同Tick，也可能夹NoteOn、多个PC或跨Track状态，不能据“碰巧同Tick”擅自改写 / 合并。
- 可将“把合法raw选择显式组合显示”作为将来扩展，不是本轮暗含操作。
- 组合创建应按正式Bank→PC顺序原子发布，定义同Tick插入位置和exact-key覆盖范围；同Tick有多个包装被操作时，使用冻结formal order，不能按HashSet或ID排序决定结果。
- 单独改动成员后建议解除包装、保留剩余raw数据，不偷偷补回缺失成员；这是D-IN02，不是已批准规则。
- List的包装行和raw行需避免对同一成员重复显示/重复选中；原raw Lane仍可访问。选中wrapper映射到成员ID集合，数量提示需区分“一处乐器变化”与“三条MIDI消息”。

跨Track状态是另一个必须验证的边界。SRS §23.4.2顺序为绝对Tick→全局Track顺序→Track内事件顺序，同Root子Segment结束不清共享Bank/Program。

已有 `PureMidiPagedCanonicalSource.cs:734/810` 及 `PureMidiRangeBoundaries.cs:29–60` 能按共享生命周期恢复目标末值，但没有直接提供“待生效Bank”和“最近PC实际采用Bank”的双状态接口。需测试：

```text
Bank A → PC P → Bank B（尚未下一次PC）→ 从中途开始 / 查询名称
```

不能简单把三个“最后数值”拼成“当前发声音色”。新UI可以明确显示该包装自己写入的数值与名称；若还显示“此刻有效音色”，必须使用符合正式顺序 / 状态规则的查询。这里没有新证据允许本轮直接宣布范围播放错误，更不能借UI投影重新解释音频。

### 3.3 包装编辑覆盖矩阵

实施前将下表固定到ADR，避免只做一个好看的点：

| 动作 | 必须处理 |
|---|---|
| 创建 / Properties / 双击 | draft中选Bank/PC，OK一次发布全部成员；Cancel不分配正式内容 |
| 水平移动 / Ctrl复制 | 整个包装同Tick移动；目标碰撞按批准的成员覆盖策略原子归并 |
| 删除 / 剪切 / 粘贴 | 包装与成员关系同步，有限内存、有取消、目标选择、Undo完整还原 |
| raw成员单独修改 | 解除或同步按D-IN02；禁止悬空关联、重复显示、静默复活成员 |
| Split / owner转换 / 删除owner | 重映射或解除关联的规则明确；不能指向别的Segment或保留源ID悬空 |
| 多选 / 混合对象 | 既有类型子菜单机制；整体InstrumentChanges允许哪些批量动作需明确，不把固定y当数值曲线 |
| Save / Reopen / 旧项目 | 不丢旧Bank缺分量、独立PC、Mapping；raw声音 / 顺序持续等价 |

不要求这张表所有动作都在A2a首个切片交付，但A2b整体验收前必须每项有实现或用户明确批准的限制。

## 4. R27：交互与弹窗

### 4.1 Instrument Changes 视图

- 仅 MIDI Segment / SubVoice；Logical Segment没有这个Tab。
- 一个包装点对应一次乐器变化；y固定中线，x=Tick。Draw单击创建 / 选中，不通过拖线生成很多乐器弹窗。
- 在点的上或下方择有空间一侧显示带边框标签，包含Bank、PC值和名称；密集点使用有限标签布局 / LOD，命中依据正式数据，不从bitmap反推对象。
- 对象List合并为特殊行；双击行 / 点、右键Properties复用同一个选择器。远处数据按页准备，不整表构建字符串。
- R18实施后右拖负责框选，不能再为此Tab保留暗含的右拖创建。

### 4.2 统一音色选择器

用户已指定：

- Bank / Program两个列表，每行包含值和名称；支持虚拟化，不展开所有组合。
- 独立可输入的Bank MSB、Bank LSB、Program数值框；列表选择与有效数值即时同步，无名称时仍可正常指定数值。
- 可水平滚动的钢琴键盘，按键按其自身Key/Velocity试听；默认自动试听Key60、Velocity100，可修改。
- 列表选项变化触发自动试听。试听先停旧音，任何编辑/确认/取消/关闭/跳转都先终止该弹窗拥有的试听，不让用户重复操作一次才能编辑。
- 点 / List / Initial State共同使用该选择器；draft、Validate / 输入错误、OK / Cancel使用现有主题与统一按钮 / Enter / Esc / 焦点规则。

D-IN05需要补全自动试听Gate长度、快速换选节流及复合键盘行为。推荐自动试听为短单音，例如300ms作为试验初值，快速连续选择只保留最新请求；手按琴键走held生命周期，释放即结束gate。无SoundFont仍允许编辑，明确试听不可用。这个300ms是新建议，与删除的右双击300ms无关系。

试听必须冻结当前Enabled SoundFonts、Bank映射、Channel Mode、voices配置，完成所需preset预载；不能用Catalog名称或Profile索引作为声音身份。D-IN05还需决定它是“干净控制器状态的独立preset audition”还是“当前时间线Root有效状态下试听”；后者还需正式范围前状态查询，不能把Root初始Mode误当后续SysEx改变后的活动Mode。推荐首切片前者，明确所采用模式。建立临时受控canonical试听计划或先批准明确的audition合同；不得让项目文件提供自由raw命令绕过白名单。Master→Limiter、NoteOff、抢占、关闭、原生错误和工作线程资源门都要覆盖。

SRS §8.55.1曾规定Program显示1–128，而内部0–127；现有各处需源码核对统一。D-IN04建议所有新数值字段清楚采用0–127，并配名称，不混用“一基PC”和内部字节；如果保留一基显示，要中央转换、明确标签，不让预览与保存差1。

### 4.3 Initial State 与旧SubVoice

用户明确范围为Event Instrument全局与SubVoice Initial State，**不是自动扩展到Project Global Initial State**。

用一行Instrument selector替代三行裸数值，但初始状态每字段的空值 / override / 继承是正式语义：

- 打开弹窗、只浏览Catalog再取消不得补0或显式固定继承值。
- 若只修改一个原有override，必须有方法保留其余继承状态；选择一个完整preset可以明确一次覆盖三个字段。
- 旧项目独立Bank/PC、只有MSB或LSB、scalar Mapping依然可查看与编辑。SubVoice可优先推荐包装创建入口，但不能把已有独立数据变为无法访问。
- 若要彻底删除SubVoice独立Bank/PC编辑能力，需批准替代的高级展开UI与兼容方案，不能误以为“底层还在”就等于功能仍可用。

## 5. R28：每目标一个 Lane Tab

用户已确定布局：原LANE标题、下拉框及通用Event/Parameter Tab退出；每个实际事件/参数一个Tab，名称作为Tab头。坐标、Snap、Snap值、Add入口搬到Tab头同一行的最右侧，内容区不再占第二行工具栏。

Tab行为：

1. Velocity固定第一；Instrument Changes若存在固定第二；其他按用户视图顺序。
2. 头部溢出支持鼠标滚轮水平滚动、拖拽重排、最右下箭头弹Tab列表；活动Tab键盘可达、长名称不裁按钮。工具区和溢出按钮宽度不足时需有最小布局规则。
3. Add成功激活目标Tab；导入只为实际涉及的目标创建，不预建全128CC。
4. 每Tab独立纵向缩放 / value viewport；横向时间轴仍与同Workspace的Piano/Velocity同步。
5. 以owner+正式target身份识别，不用Tab文本或数组位置；重命名不丢状态，视图重排不改MIDI事件顺序/Mapping顺序。
6. 不为每个隐藏Tab常驻一个活跃Surface、全事件索引或定时器；只保留轻量状态并懒加载可见面板，快速切换丢弃旧revision任务。

### 5.1 是否允许关闭（D-LANE01）

用户提出“仅空Tab可关”或“不允许关闭”，也允许评估。建议第三种更明确的语义：**Close只隐藏视图，不删数据 / Mapping owner**，可从Tab列表重新打开；Delete Lane / Delete Mapping仍是独立正式操作。这样无需为了关Tab扫描所有事件，而且有Mapping无点的owner不会被误删。

若用户选择仅空可关：用维护的target计数/curve存在摘要判断，不在点击时扫描全Segment；空owner仍保留。任何一种选择都必须说明“没有Tab”不等于“没有正式数据”。新导入自动发现规则也不能在刷新后把用户刚隐藏的Tab强制打开。

### 5.2 与状态持久化衔接

R07按Track共享的是通用编辑profile；**Lane存在性仍由该Segment内容/owner决定**，不能在另一个Segment里凭共享Tab创建新的参数或事件。

建议Track记同类target的默认纵轴 / 顺序偏好，Segment记其实际Tab开关 / 当前target / 局部viewport；最终所有权表在R06/07定案。R28先实现会话内稳定状态，后续独立schema保存，不现在序列化整个Tab VM。

## 6. R12：友好CC显示

用户要求只改变UI，不改原始0..127等值域。推荐中央 `MidiValueDisplayDescriptor` 定义正式target→显示变换、逆变换、单位、格式与原合法范围；各视图不能自行减64。

Pan示例为 `display = raw - 64`，边界raw0/64/127显示−64/0/63；Cutoff应精确指具体CC（如CC74），不暗示“0”是任何设备共同的物理Hz或声学中性点。

D-VAL01需批准白名单及覆盖入口。建议覆盖ruler、鼠标坐标、点/条tooltip、List和Properties；现有Batch/Generator/Humanize/Mapping表达式与预设继续使用正式raw值，并在相关Help/标签清楚说明，避免旧预设音乐结果改变。是否增加显示单位模式是以后独立需求。

测试raw↔display全边界、往返无损、Clamp、不适用目标不减64；分别识别Direct MIDI的14-bit raw与SubVoice signed PitchBend，按源编码显式转换、禁止重复偏移；Catalog字段与事件身份不转换。同操作由列表/图形/Properties进入结果一致。

## 7. R29：事件点 + 阶梯线

视觉形式为点、前值水平保持、变化Tick竖跳；不允许在两点间做线性声音插值。建议抽共享阶梯tile provider，复用Tempo的设备列first/last/min/max、前驱查询与后台取消思想，但不复制其特定Conductor模型。

范围与限制：

- Logical Parameter离散Step、MIDI/SubVoice状态型标量点适用。
- Velocity仍是NoteOn力度柱，不变成持续状态；InstrumentChanges固定y点；opaque Meta/SysEx不是数值线。
- Bank/PC、CC120–127等命令/结构目标不可被错误描述为普通持续数值，需明确显示策略。SubVoice既有Value Curve / Envelope不得被本项强制变Step；它们在正式绘制模式下保留原语义。
- 可视左边界查询最近前驱；无前驱时是否从Initial/default起线、跨Segment到哪结束、右尾如何延伸均依正式owner生命周期，不从相邻不相关Segment借状态。
- Pure MIDI同Tick允许多事件，首末取正式order；可视LOD合并只改像素、不删记录、不改变命中 / selection / export。
- 只画线，不为每点建WPF控件；线与点/选择层局部失效；全源扫描、全量prefix数组不作为默认百万级实现。

验收：左边界前驱、无点/单点/同Tick多个点、跨Track共享状态标签不误导、半开边界、PB值域、稀疏长间隔、百万密集点、局部编辑Undo与缓存修订；原编辑采样密度/碰撞/快捷键不变。若状态基线需要更大canonical查询能力，先分阶段增加，不在UI线程临时编译。

## 8. 专题实施 / 验证门

建议A2a先做“已确认包装契约→统一选择器→Initial State / 单点创建→试听生命周期”的垂直切片；A2b覆盖全编辑 / List / Tabs / 冷热缓存。R12/R29可在A4接入同一target呈现基础。

自动门至少包含：

- raw导入→浏览→保存→编译→导出序列保持；重复同Tick、Bank/PC缺分量、跨Track同Root、NoteOn夹杂、crop/相邻Segment。
- wrapper创建/移动/删除、raw成员改变、同键碰撞、复制剪贴板、Split/转换/删除Owner、Undo/Redo、失效修订，均零部分提交。
- Initial State不改变未编辑override/继承，旧SubVoice的Bank/PC/Mapping可继续操作；新旧格式golden与source trace。
- Catalog缺失/损坏/disabled/orphan/override、数值fallback、配置热更新；仅浏览不扫描SF2、更不读sample或改Modified。
- 试听快速换选、按住/释放、无SoundFont、Percussion、关闭/取消/编辑抢占、旧任务迟到、音量/Limiter/预载/voices一致。
- 目标很多但单目标很少、单目标百万、所有目标百万、长名称、多同Tick、首次冷页、切Tab/隐藏/关闭释放；阶段开始前固定可测性能预算。

本专题不得以“新点能发声”代替上述顺序、兼容性与资源测试。
