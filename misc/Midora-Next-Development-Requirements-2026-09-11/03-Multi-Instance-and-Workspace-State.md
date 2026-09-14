# 多实例、跨项目剪贴板与工作区恢复

覆盖R01、R06、R07（均P2）以及文件打开R04（P3）。这两个大型专题应分别分阶段，不与几处UI小修合并成不可验收的大提交。本文是候选设计，不表示多实例、跨TPQN或新持久化协议已经获批实施。

共同入口：[需求总表](00-Overview-and-Delivery-Plan.md)、[决策与问答主文档](04-Decisions-and-Preparation.md)。源码行号基于 `0bb9670`。

2026-09-11 文档问答整理：`04` 统一承载待确认问题、推荐、用户回答与追问；本文只保留审计、范围解释和候选架构，不维护第二套回答。首个实现切片与本批最终目标已经分开列明，但下述推荐仍全部待用户确认；写入文档不是批准多实例、跨TPQN或持久化变更。

## 1. 多实例：现状不是“只有一个 Mutex”

| 来源（仓库相对路径） | 已确认事实 |
|---|---|
| `src/midora-desktop/Midora.Desktop/App.xaml.cs:23` | 启动能力检查后StartOrForwardAsync；次实例转发请求后退出，主实例回收残留并创建窗口 |
| `src/midora-common/Midora.Common/MidoraProgramDataPaths.cs:30` | scope含用户与规范化ProgramRoot |
| `src/midora-core/Midora.Application/SingleApplicationInstanceCoordinator.cs:92/319/329` | 有版本协议、1MiB参数payload、256参数、64pending请求、5秒超时；CurrentUserOnly pipe及WindowsSession区分 |
| `ProjectPersistenceCoordinator.cs:179` | 现有工作路径普通Save自动允许覆盖，不是跨进程冲突协调 |
| `ApplicationPreferencesStore.cs:206` / `RecentProjectsService.cs:112` | 临时文件+原子Replace；没有等价的跨进程合并/CAS合同 |

SRS §3.3/3.18.1/21.2、INV-019限制单实例；§16.17.2明确普通源文件打开后不长期加锁、不监控外部改动，下一次Save按内存覆盖。§16.34和INV-086/094还使启动回收依赖单实例scope。

因此移除启动互斥就会引入同文件覆盖、共享偏好丢更新、一个实例回收另一个工作目录等风险。原子Replace只能防半文件，不能防两个用户会话互相覆盖。

### 1.1 推荐的多实例基线（D-MI01/02）

- 每个进程仍单Project；不顺带实现一个进程多个音乐Project Workspace。
- 每实例独立canonical、Selection/Undo、音频Worker、进程树与会话临时目录；不共享native handle。
- 同一Project文件建议只允许一个可写Midora会话，第二次打开定位已有实例或清楚拒绝 / 另存副本；须定义路径别名、文件identity、首次Save、原路径Save、Save Copy、旧格式升级和外部修改，不新增传统Save As。独立ownership lease / 协调器不一定长期锁原.midora；只有长期锁源文件的方案才改变现行“不长锁”条款。普通Save是否检查外部identity变化是另一独立决定，不能假装现有保存已防冲突。
- 程序级Preferences/Catalog/Presets用短写锁+版本比较或明确的冲突处理，不以最后写入者静默获胜。Recent可在锁内按既定去重顺序合并；配置与用户draft不要无提示合并数值冲突。
- 一实例更新音频配置时，其他实例正在运行的任务仍用已冻结配置；何时接收新配置 / 重建Worker需批准，建议对方Stopped/Idle后应用并提示，不中途拆活动Worker。
- 清理只针对可验证owned目录和已释放lease；进程PID不是唯一存活 / 身份凭据，必须防PID复用和清理竞态。缓存quota要明确是整个ProgramRoot还是每实例；建议共享总上限，不能实例数乘以16GiB导致预算失真。
- 仍只在ProgramRoot授权的Data/.tmp树内存储，不fallback LocalAppData/TEMP；新增传输目录应作为明确路径白名单变更。

D-MI01.a～d / D-MI02.a～d 已将上述分歧及补充边界列全：推荐外部文件identity改变时可取消/Save Copy，或明确确认后备份磁盘当前版本再覆盖；无参exe开新空窗口、窗口内New/Open替换本窗口；多实例可同时播放/试听互不停止。不同ProgramRoot的配置/缓存独立，但兼容剪贴板可在同用户同Windows登录会话跨安装目录传输。并发发声时各实例Limiter不等于Windows混音后全局峰值保证；内存/CPU按实例累加。这些均非现有已实现行为。

## 2. 跨项目 / 跨实例剪贴板

### 2.1 已有能力和不足

| 来源 | 现状 |
|---|---|
| `Midora.Application/ProjectObjectClipboard.cs:31/270` | payload持有进程内SourceSessionIdentity，Paste使用ReferenceEquals限定同会话 |
| `Midora.Desktop/MainWindow.xaml.cs:10226` | OS Clipboard只写PlainTextSummary，正式数据留私有_projectClipboard |
| 同文件`:10049/196` | 项目替换和窗口关闭Dispose正式payload |
| `Midora.Application/ProjectClipboardStorage.cs:7` | 已有分页snapshot引用计数ownership，最后lease释放才回收 |
| `ProjectClipboardTransfer.cs:44` | Copy/Cut有可取消detached准备、revision冻结与发布复核 |
| `DetachedPagedEditTransaction.cs:10` | 默认4096记录页、每256记录检查取消；working/resident各64MiB、spill16GiB、candidate100,000,000 |
| `BoundedEditStorage.cs:137` | 现有spill是process-local scratch，不是跨版本可交换协议 |
| `ProjectObjectClipboard.cs:81` / `ProjectObjectClipboardPureMidi.cs:7` | Logical Track snapshot引用源Definition；Pure Track有route snapshot，跨Project需要明确闭包 / 新ID |

允许另一进程看到内存对象、复制source session token、或把完整百万对象JSON塞进OS Clipboard都不是合适方案。应复用有界分页事务理念，但为传输设计明确版本、租约和安全边界。

### 2.2 传输候选架构

```text
冻结源Selection / revision
→ 有界准备不可变传输快照（独立于源可变Project）
→ OS Clipboard小型版本化描述符 + 可读文本摘要
→ 接收方验证协议 / 预算，分块取得数据并持有lease
→ 目标detached准备：新ID、闭包、时间、碰撞、损失确认
→ 验证目标revision后一次发布 → 本地Undo/Redo
```

可用受限同用户IPC传输块，或专门拥有的传输backing。不要把接收方打开Clipboard中的任意本地文件路径当安全协议，不使用反射对象反序列化，不复用1MiB启动参数通道承载音乐内容。

至少约束：版本/feature协商、长度/记录数/嵌套/依赖数量、总字节、分块完整性、压缩解码预算、背压、取消、超时、名称/表达式验证、错误码和旧版本可见提示。旧自由C# Mapping内容不能因剪贴板交换重新获得执行权。

### 2.3 对象范围不能由“共享剪贴板”一词推定（D-MI03）

推荐先把用户最核心的Note Art跑通，再扩展闭包；**D-MI03.a 的本批最终推荐目标是现有全部可复制对象类型，而不是把“首轮音符/事件”误写成最终完成范围。** 用户也可明确收窄，当前尚未回答：

| 类型 | 首轮建议 | 额外合同 |
|---|---|---|
| 三类Note | 同TPQN基础支持，复用共同字段转换 / Direct NoteOff velocity规则 | 目标分配新ID、精确碰撞、未保留字段确认、合法owner范围 |
| 数值MIDI Event | 同正式target可表达时支持 | CC/压力/PB值、目标lane存在性、SubVoice禁入目标、同Tick覆盖 |
| Logical Parameter Point | 不能直接把源Parameter ID当目标ID | 显式选择目标参数或复制已批准定义闭包；禁止按名称猜绑定 |
| Segment | 后续闭包阶段 | crop/hidden内容、位置/长度、事件owner、跨类型损失确认 |
| Track / Root / Usage | 后续闭包阶段 | 所复制子集共享关系、Fixed Port.Channel冲突、新Root生命周期、Track顺序 |
| Definition/SubVoice/Mapping/Curve | 后续闭包阶段，现有可复制类型纳入本批最终推荐范围 | 稳定ID闭包重映射、受限表达式ABI、名称冲突、外部引用、空owner；不凭“全部”新增原来不存在的Copy入口 |

支持范围逐项对Copy/Cut/Paste菜单给出可用性或精确错误，不静默只复制一部分。名称不是身份，不同项目相同数字ID没有关联；所有新对象使用目标allocator，闭包内部引用重映射，不保留源进程的活引用。

D-MI03.b～f 推荐同时冻结以下合同，待 `04` 回答：完整对象携带必要依赖，一次payload内共用依赖只clone一次、不同次Paste默认独立，不按名称自动覆盖/绑定；粘贴进既有Track/Segment不偷偷换其Usage/Root/Instrument，参数须显式映射。复制Track/Root到另一项目时保留所复制子集内部共享关系和Channel Mode，但新Root默认Auto，不自动合入目标同Port.Channel；这不顺带修改现有同项目Paste的Fixed复用行为。不复制SoundFonts或整个源Project环境，因此不同目标默认状态/绑定/音色库下不承诺相同听感。

跨实例“Cut”建议仍是源端安全复制快照后删除源，目标Paste是目标独立command；不要制造跨进程共同Undo栈或声称两次操作是一个分布式原子移动。源Cut取消/失败不删内容，目标Paste失败时仍保留可再次粘贴的快照。

### 2.4 TPQN（D-MI04）

同TPQN仍需检查owner起点/合法范围与精确碰撞。不同TPQN不能只缩放NoteStart：还有Gate、Segmentcrop/hidden、TemplateLength、PreRoll、Loop、Envelope、Curve、Mapping的时间Context。

表达式可能含用户写死的Tick常量，自动改源代码不存在通用保真算法。D-MI04.a～c 已把本批范围拆清：推荐首个切片仅同TPQN，但**本批最终支持基础Note/可表达Event及不含复杂逻辑依赖的Pure MIDI Segment/Track跨TPQN粘贴** 。默认保节拍比例，可明确选保原Tick；节拍换算不保证相同秒数，因为Tempo可能不同。采用精确有理数、起止边界单次 `AwayFromZero` 取整，Gate由边界得出并执行既有最小长度、溢出和精确碰撞规则，不重复取整。

携带Instrument/SubVoice/Loop/PreRoll/Envelope/Mapping闭包的复杂对象，推荐本批仍同TPQN；裸逻辑音符不因此受限。若用户希望复杂依赖也跨TPQN，需要在 `04` 明确追加转换语义，而不是仅对显式Tick字段乘比例却声称保持表达式含义。以上是待批最终方案，不表示跨TPQN代码已经存在。

### 2.5 生命周期（D-MI05）

应分别规定：源Project关闭、源实例正常退出/崩溃、Clipboard被替换、接收已开始、接收未开始、目标取消。

推荐完整快照发布后，在用户未替换Clipboard且backing仍有效时，即使源Project关闭、源实例正常退出或崩溃，也能在当前Windows登录会话新发起Paste；需要独立owned backing和延迟回收，不能再依赖源窗口私有字段。已开始接收者持有lease，Clipboard被替换不破坏它；最后lease释放且不再作为当前Clipboard后才回收。手动删除backing或磁盘损坏不在有效性保证内。

这是便利性较好的候选，但比“源退出即失效”多出清理和跨版本预算成本。D-MI05.a～c 推荐不承诺Windows重启、剪贴板历史/云同步旧条目仍有效，失效明确报错；它不是永久素材库。按显式协议版本互通，不要求exe版本完全相同，也不承诺任意新旧版本互通。若选择较窄版本，必须在UI清楚说源退出后不可粘贴，不能表面成功Copy却静默丢内容。不能为此恢复任意长期崩溃快照机制。

### 2.6 多实例验证门

真实2 / 4实例并发启动、同文件同路径 / 别名打开和保存、配置冲突、Recent合并、关闭一个实例不杀另一个Worker / 删除另一个临时目录；源退出/崩溃/传输中断/Clipboard替换/目标取消；恶意描述符/版本不兼容/超限长度/路径注入；百万Note Copy/Cut/Paste与Undo、各种Root/Usage闭包、同TPQN/获批的基础跨TPQN换算/复杂依赖拒绝差异、源/目标revision race。按切片实际能力报告，不把首切片拒绝跨TPQN等同于最终范围完成。

同时播放和设备丢失须真实测试，不能从WASAPI Shared Mode推定所有隔离成立。记录全进程树资源而不是只主进程；全局quota必须对并发实例仍成立。

## 3. R06：完整视图与Mute/Solo保存

### 3.1 当前基础

- `Midora.Application/ProjectPresentationSessionV3.cs:9` 已有独立revision、saved revision、recovery dirty。
- `ProjectPersistenceCoordinator.cs:214/259` 普通Save / Save Copy已冻结presentation；Save成功才更新对应baseline，Copy不清原会话状态。
- `Midora.Persistence/ProjectPresentationV3.cs:34` 当前只承载Onion和All Tracks模式；Format3独立presentation读1/2、写2。
- `Midora.Desktop/DesktopSessionController.cs:849` 四套Track / group Mute/Solo集合属于runtime，换项目清空；`:2455` 打开后只OpenArrangement，未恢复Tabs / viewport。

R06明确替代“普通UI和Mute/Solo不保存”的旧规定，但保留以下用户确认：只随显式Save保存，不进Undo/Redo，不标记音乐Modified。**Mute/Solo会改变运行期监听，不能写成完全不影响播放；它不改变源音乐、canonical、SMF/WAV成品。**

### 3.2 “完整”的逐字段候选白名单（D-STATE01）

| 分类 | 建议保存 | 不默认包含 |
|---|---|---|
| Workspace集合 | 打开的已提交对象Tab、顺序、活动Tab；已关闭对象视图的轻量状态也保留；Arrangement唯一且固定 | 模态窗口、未提交draft、任务窗口、上下文菜单 |
| Piano / Arrangement / Conductor | 缩放、独立滚动、Grid/Snap、工具模式、显示面板与尺寸、活动子页；具体字段以owner表为准 | Selection全部ID、拖动候选、鼠标位置、预览音符 |
| Instrument Workspace | 当前子页/SubVoice、左栏宽度、滚动/折叠、预览键盘的可恢复显示设置 | 正在按住的键、音频任务、草稿表达式 |
| Lane Tabs | 已显示target及顺序、活动target、每lane纵轴、lane区高度；存在性仍受正式数据控制 | 数据索引 / bitmap / 后台source leases |
| 对象List / Tracks面板 | 开关、宽度、合法列宽/排序或筛选等明确获批字段 | 百万行对象VM、全量选区、过期ordinal |
| Diagnostics / Settings Workspace | 可恢复筛选 / 子页 / 布局（若确认纳入） | 一份复制诊断报告、模态Properties草稿 |
| 导航指针 | 推荐保存Edit/Playback Cursor静止位置，恢复时Clamp到有效范围 | Playing/Buffering/Preview继续运行、任务续跑 |
| 监听过滤 | Track Mute/Solo；建议一并保存独立Usage/Root组状态 | 改写成员状态、改变canonical / 导出选择 |

这不是允许序列化整个ViewModel。字段应有类型、合法范围、默认值、owner身份和未知/损坏恢复策略；不保存内部ID到可见UI，但序列化引用仍使用Stable ID。

B1进入编码前还须通过资源实验冻结可执行的数值预算：presentation总字节、Tab/profile/lane条目数、单文本长度、恢复在途任务数和后台缓存上限。不能仅以“轻量/几十个Tab”替代输入上限。超过恢复预算时隔离对应presentation区并说明，不影响音乐加载；保存端不得静默截断用户视图。D-STATE02.c 已列推荐处理：明确提示并允许保存音乐及可用视图部分，或取消；待批准后写入schema/工作流。预算数值由实验固定，不要求用户猜字节数。

### 3.3 保存与恢复合同

1. 音乐无Modified时，用户显式Save也能刷新presentation；标题不新增音乐星号，关闭不为单纯视图变化新增保存提示。相应Save可用性必须检查，不能仍因音乐未改而不写。
2. 开始Save冻结Project revision与presentation快照；保存期间新视图变化不能被误清为已保存。Save Copy携带同份冻结状态但不重置原保存基线。
3. 只保存已提交的视图描述，不自动写磁盘每次Pan/Zoom；内存profile轻量即时更新。
4. 恢复后Stopped，不自动开始音乐/试听；监听过滤在首个播放计划前生效。底层canonical仍包含未过滤音乐。
5. 恢复Tabs先轻量描述符，活动Tab按需加载；不把几十个后台workspace一口气同步解码或构建百万索引。
6. 已删owner/失效target过滤或dormant处理；Arrangement固定第一且唯一。对象Undo恢复是否自动重开Tab由明确导航策略决定，不能伪装成Undo视图状态。
7. presentation损坏/未知版本继续不阻碍音乐加载；D-STATE02.b 推荐尽可能独立section回退，而非任意一个字段使全部布局丢失，但维持严格JSON/版本检查和可理解警告。
8. 新presentation schema不原地修改v1/v2，保留reader/golden。D-STATE02.d 已列是否接受旧软件重写后丢失未知新视图字段，推荐接受这一限制但不损坏音乐；不能承诺旧软件也完整恢复新字段。

只扩presentation通常可继续用外层Format3的独立schema分派；若音乐source同时增加不可表达内容，另评估Project Format。保留旧Format1/2 detached migration与确认后备份再原路径保存规则。

## 4. R07：Track通用profile与Segment独立状态

### 4.1 现状差异要讲清楚

`DesktopSessionController.cs:349/2187` 当前所有Segment主Piano共享一个controller级 `PianoRollEditorSettings`；`PresentationModels.cs:815` 包含Grid/Snap/默认音长力度。其他zoom、scroll、下部面板多在每Workspace，`:1055/1153` 的LaneEditorSettings也独立；关闭Tab（`DesktopSessionController.cs:2251`）销毁VM。

所以新要求不仅是“多记几个值”，还会将部分**跨所有Track共享** 缩为**同Track共享** 。应在D-STATE03明确批准，避免某些设置仍全局串改。

### 4.2 推荐所有权

| Owner | 字段建议 | 同步 / 生命周期 |
|---|---|---|
| Project presentation registry | Tab顺序、活动Workspace、全局布局 / 指针（获批时） | 小型typed registry，独立revision |
| Track profile | 水平/垂直缩放、Grid；主Piano Snap与底部Event Snap两套独立配置及各自Snap值；默认Note长度/Velocity、Lanes/List/Tracks开关与通用面板尺寸 | 同Track即时同步；不同Track独立；与Usage/Root共享状态无关；不因新Tab工具栏合并而把Piano/Event Snap合并 |
| Segment view state | 各自horizontal start、vertical origin、活动lane、该owner实际Tab显隐/顺序与局部布局 | 关闭保留轻量字段，不保留VM/source缓存 |
| Segment + lane target state | 每target纵轴、显隐/顺序、实际局部状态 | 推荐Segment独立而非Track默认联动；不在其他Segment创建正式Lane / Mapping |
| SubVoice view state | 自己的编辑profile、lane状态与局部viewport | 独立于Logical Track共享profile；不因绑定或Usage改变而串改 |

用户调zoom时写一份Track profile，当前可见Tab即时更新、隐藏Tab在激活时读取同一revision；不互相广播复制VM属性制造反馈环。共享缩放不共享鼠标锚点或滚动起点，另一Tab保留自己的世界坐标观察位置。每次写入应与受影响Tab数成比例，不与音符数成比例。

关闭Tab不删除profile，关闭Project释放整个会话registry。Track删除 / Undo、Duplicate、Segment跨Track移动 / 转换、加入共享Usage/Root的政策已列 D-STATE03.a～c 待回答：推荐profile归Track ID；新Duplicate复制轻量偏好、之后独立；迁入Segment使用目标Track通用profile但保留/校准自己合法滚动位置；删除owner可保留轻量dormant状态供音乐Undo恢复，但不自动重开Tab。

现有从Arrangement显式打开Segment会在编辑指针位于Segment时将其置中（`DesktopSessionController.cs:2201`、SRS §18.2）。建议优先级为：显式“定位/打开到编辑指针”导航覆盖一次局部位置；普通切Tab / 项目恢复遵守已记忆viewport。用户是否要改变此习惯见D-STATE03。

### 4.3 状态专题验收

同Track多个Segment同步、不同Track隔离、独立滚动、关闭重开、跨Track移动/复制、删除Undo、重命名；R28重排/隐藏/每lane纵轴；只变视图Save、Save中再变视图、Save Copy、损坏section、未知version、旧格式迁移；几十个隐藏Tab首次打开/释放内存、无额外编译、canonical/SMF/WAV等价。

恢复大量Tabs不能显著增加首次可用时间，也不能使所有隐藏Onion/All Tracks索引常驻。自动化检查订阅计数、关闭后引用释放和缓存上限，人工只验恢复准确与焦点。

## 5. R04：外部文件拖入 / Open With

源码确认：`MainWindow.xaml.cs:163` 已接收第一个`.midora`启动参数、相对请求WorkingDirectory解析，接入打开流程。`:4706`菜单MIDI导入有Port复审、可取消任务、原子会话替换、完整报告和Worker预热；未发现FileDrop处理或Open With注册路径。存在内部AllowDrop不等于外部文件打开已实现。

建议先抽统一“请求打开一个外部文件”协调器，让菜单、命令行、转发和拖入共同使用：扩展名/路径→停止与未保存确认→detached打开/导入→一次会话提交→报告/Recent/预热。DragOver只判断可接受类型，不读取MIDI或构建项目。

一次多文件、扩展名、忙碌/模态、窗口选择、同文件重入已列 D-OPEN01.a～d 待本次集中回答：推荐每次一个，`.mid/.midi`复用现有导入范围，多文件明确提示而非偷偷只取第一个；拖入/窗口内Open替换当前并守未保存确认，多实例完成后系统Open With/命令行带文件开新窗口、同文件已有可写实例则定位它。模态/前台长任务期间明确拒绝，不暗排迟到打开队列。

Open With可以先保证用户手工选Midora.exe时能正确处理带空格/Unicode路径；不因此自动注册默认程序或修改用户注册表。安装/portable发布的显式关联入口如需增加单独批准。

测试：干净/Modified/从未保存项目、确认取消、打开失败保留旧项目、非法扩展、文件夹/多文件、冷启动/已有进程、忙碌/模态、MIDI Port复审、导入Warning完整报告、预热失败保留已打开项目。多实例上线后相同入口不能发生双重打开或覆盖。

## 6. 后续实现分阶段建议

见[总计划B/C](00-Overview-and-Delivery-Plan.md)。两专题不是“做完一个功能才想状态”：C先管共享写入/租约，B先管字段所有权；协议/schema在各自进入实现前冻结。各轮仍需详细自动测试+小型人工清单，不要求用户手工验证所有多进程竞争或损坏文件组合。
