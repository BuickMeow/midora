# 交互、显示与常规 UI 任务

关联：[总表与执行边界](00-Overview-and-Delivery-Plan.md)、[决策与问答主文档](04-Decisions-and-Preparation.md)。基线 `0bb9670`；以下“原因”未注明运行证据时均为源码判断，本轮不实施、不运行产品测试。

2026-09-11 文档问答整理：本文保留原需求、源码审计和实施候选；所有待确认问题、用户回答与追问统一维护在 `04`，不在本文另开第二份答复记录。下述推荐均待确认，不能将“已落地到文档”解释为用户批准推荐或授权实施；`04` 的逐项定案后再同步本专题与必要的规格变更。

## 1. P0/P1 常规 UI 与事件创建

### R05：SoundFont 状态入口（P1）

用户要求：状态栏 SoundFont 文本采用诊断状态文字同款 hover / Hand / 点击行为，直接打开 Application Preferences 的 SoundFonts 页。

源码确认：

- `src/midora-desktop/Midora.Desktop/MainWindow.xaml:1370` 附近诊断状态具有点击与悬停样式；`:1389` SoundFontState 仍只是 Caption 文本。
- `MainWindow.xaml.cs:367` 的设置入口先做 `PrepareForModalSurface`、前台任务许可检查，保存后可能重建 Worker。
- `ApplicationPreferencesDialog.xaml:21` 是 Audio / SoundFonts / Appearance TabControl，当前没有供该状态入口指定初始页的完整导航路径。

建议：抽统一 `OpenApplicationPreferences(initialTab)` 核心方法；现有菜单仍默认 Audio，新入口指定 SoundFonts。不要构造缺失 RoutedEvent 的事件参数再调用事件处理器。打开前停止由当前编辑器持有的试听，不误停正常项目播放。

播放 / 不可中断前台任务期间，建议遵守现有设置编辑禁用规则并给出可理解的 tooltip，不绕过设置保存门；若希望此时仍可只读浏览，属于 D-UI08.a 的额外选择。

验收：无项目、有项目、零 / 多 SoundFont、预览中、正常播放中、正在任务中；初始页正确；Cancel 无改动；设置保存 / Worker 重建行为不退化。

### R14：SubVoice 事件创建扩模板（P0）

源码确认与候选原因：

- `MainWindow.xaml:955` 将 SubVoice Event Surface 的 `RangeEndTick` 绑定 TemplateLengthTicks。
- 共享 `Midora.Desktop.Presentation/Controls/TimelineSurface.cs:3725/7014` 把该范围传入绘线采样；`Interaction/TimelineValueTraceSampler.cs:52/162` 过滤 `tick >= rangeEnd`。
- 但 Application `ProjectResourceCreationEditCommands.cs:733–747` 创建事件已扩大模板；`ProjectSubVoiceEventLaneEditCommands.cs:98–102` 的移动 / 复制亦有扩展；`ProjectBoundedTemplatePointCommands.cs:122` 分页发布同样支持。

因此优先追查“显示模板范围被 UI 误作创建硬上界”，不重新创建一套扩模板领域规则。Logical / MIDI Segment 的边界规则不同，不能全局取消采样器上界。

预期实现合同：

- SubVoice 的创建允许非负 Tick；成功编辑按既有正式 required boundary 同事务扩模板，事件位于模板末端时必须使它进入半开区间。对点通常需容纳 `tick+1`，必须 checked；`long.MaxValue` 不能通过溢出伪装成合法长度。
- 覆盖单击创建、Shift 锁 Tick、自由线、直线、水平线、上下文创建、Batch Create、粘贴 / Paste Here、Ctrl 复制拖动，以及已支持的曲线点创建。检查移动 / Properties 改 Tick 的既有扩展路径，不任意扩展不支持的操作。
- Add Event 只建空 owner，不凭空创建 tick 0 点；Initial State 不扩模板；已删除的 Mapping owner 不因普通编辑静默复活。
- 若有 Value Curve，仅检查其原有创建路径；不得把曲线展开结果改存离散点，不改变可曲线化目标范围。
- 内容 + 模板长度一次 Undo；取消、revision race、非法目标、溢出零发布；预览和提交共享同一结果。

测试门：`Length-1 / Length / Length+1`、Snap on/off、正反向绘线、跨很多页、大型生成取消、模板包含 Loop/Pre-Roll、Undo/Redo、其他两类 Segment 仍遵守自身范围。延长后立即刷新标尺、音符 / 事件可见范围，不依赖切 Tab。

### R16：Catalog 列表滚轮（P0）

`InstrumentCatalogDialog.xaml:106/137/247` 的 BankList、ProgramList、ScanBankList 采用 `CanContentScroll=True` 与虚拟化；没有专用滚轮处理。通用 `ScrollViewerWheelRouter.cs` 把 `Delta/3` 直接传给 ScrollToVerticalOffset。若该 ScrollViewer 的 offset 是项单位，普通 120 wheel delta 就可能成为 40 项，而非 40px。这是需运行确认的直接候选原因。

建议按逻辑 / 像素滚动模型处理，不对项列表硬套 24px 或假设行高恒定。推荐默认一个标准刻度一项，并累积高分辨率滚轮余量；嵌套列表及边界应消费本次列表滚动，不能同时滚父级。最终量见 D-UI08.b。

验收包含 Banks / Programs / Scan 预览的每个列表、长列表虚拟化、无滚动范围、到顶 / 到底、触控板小 delta、选中项不因滚动改变；确认没有破坏已有事件乐器内层列表的滚动隔离。

### R17：禁止展开下拉框悬停自滚（P0）

当前共享主题 `Midora.Desktop.StyleGallery/Themes/Controls.xaml:748/836` 提供 ComboBox template 和 `ComboBoxWheelSelectionGuard.UseSingleStepDropDownWheel`；后者只拦截滚轮，没有覆盖 hover 自动滚动。尚未通过运行堆栈确定该行为来自 WPF 默认 ComboBox 还是特化模板，实施前先复现记录，不能只改 wheel handler 声称已解决。

用户要求覆盖整个产品的展开下拉内容；建议上下边缘都禁止纯悬停触发连续滚动。保留：滚轮、拖滚动条、滚动条按钮按住、方向键 / PageUp/Down / Home/End、键盘选择的 BringIntoView。禁止滚轮穿透到父级的现有合同不变；不以冻结 ScrollOffset 的方式同时破坏键盘导航。

实施清单必须以源码扫描生成，包含隐式 ComboBox、显式 / keyed Style、自定义下拉 Popup、可编辑 ComboBox及测试宿主；菜单 / 补全列表不是 ComboBox，应标明是否具有同类行为，不能不加区分禁用所有 Popup 的交互。交付时单列未走共享模板的实际位置，由用户验收，不在本次预编造一份完整名单。

### R23 / R25 / R26：裁切、默认宽度与数据列表文案

| 要求 | 源码确认 | 修改合同 |
|---|---|---|
| R23，P0 | `NewProjectDialog.xaml:5/36/42`：宽620、Browse宽76、确认文案 Create Project | 改 `Create`；同时增加 Browse 和窗口可用宽度，保持共享确定按钮高度 / 颜色 / Enter / Esc，不全局扩大所有按钮 |
| R25，P0 | `TimelineObjectListState.cs:10/33`：默认350、约束240..700 | 默认改400；三类主 Piano 的共用列表状态同步。仅默认，不覆盖当前用户拖出的宽度；未来恢复值优先 |
| R26，P1 | `TimelineObjectListSource.cs:373/384` fallback 为 `Number · Value` | 按正式事件类型格式化 value；PitchBend仅数值，不做全局字符串替换。RPN/NRPN、PolyPressure 等身份移至 Type/Target 或保留必要独立列，不能丢失区分能力 |

R23 的具体增宽数值尚未由用户给定；建议依据 Sora 字体、100/125/150/200% DPI、最小窗口尺寸和按钮实测宽度确定，不靠随机改 margin。R26 与 R12 的 raw/display formatter 和 R27 特殊行共用呈现接口，避免再次三处独立拼字符串。

### R24：SubVoice Add Event 后导航（P0）

`MainWindow.xaml.cs:8549–8585` 成功路径只提交创建 Lane；`PresentationModels.cs:3605` 周边刷新优先保留旧 target，能解释“新增后仍停旧 Lane”。

成功后以正式 target 身份激活刚创建 Lane，并恢复焦点到目标 Surface；不能用显示名称或暂时数组下标。Cancel / 失败保留原页 / 选择。已有 target 重复创建入口应按既有规则定位 / 拒绝，不能制造重复 owner。异步刷新迟到不能把用户已切换的页抢回；R28 上线后同一导航命令打开目标 Tab。

## 2. R13：事件批移的 scalar 值与 Clamp（P1）

### 2.1 已发现的具体路径

`MainWindow.xaml.cs:7984–8039` 的 Direct MIDI 编辑路径读取选择指标时 value 写为0，只按 anchor Clamp；Application `ProjectPureMidiTimelineEditCommands.cs:836–856` 将相同 Data1/Data2 byte delta 套给每个事件再严格验证。

这不只可能在 value 上下限出错。Pitch Bend 的 14-bit 值127→128，对 anchor 计算成低字节−127、高字节+1；直接套到另一个原值128的事件，会得到负低字节。应在**正式 scalar 值** 上做变换，再编码字节，不能把两个 MIDI 数据字节当独立坐标轴。

Logical Parameter（`MainWindow.xaml.cs:8174–8217`）与 SubVoice（`:8424–8446`）已有基于整选区 min/max 的共同 delta Clamp。它们不是已确认相同 BUG，但必须一并验证。共享 Surface 的预览用受限 delta，MouseUp 某路径仍提交原 pointer delta（`TimelineSurface.cs:3824/8485`），需核对 preview=commit。

### 2.2 建议的修复边界

- 统一以 target 的 scalar 值域表达编辑 / 预览 / delta 提示；PB遵守各adapter现有14-bit scalar合同：Direct当前使用0..16383，SubVoice使用−8192..8191，二者以明确偏移转换。CC / Pressure 等遵守各自合法值；编号和身份不参与Clamp，不借本修复静默改变UI或持久值域。
- SRS §20.1.7已规定共同delta饱和，先保持该合同并修复Direct编码不一致，无需用户重新批准。D-UI03的逐点饱和仅作为可另行选择的新交互变更，不阻塞本次小修。
- Value 保护不能放宽非法事件 target、CC91/93 在 SubVoice 的禁入或枚举合法性；混合非法类型应按现有显式子集规则处理。
- Small / paged、普通 / Ctrl 复制拖动、公用 Properties / 批处理若复用此转换层均检查；不要同时重写与本项无关的 Batch 数值契约。
- 操作后精确同 Tick 碰撞继续 later-wins；Tick 轴既有边界与 Shift 锁 Tick 保持。

最低测试：PB 127↔128、8191↔8192 raw 边界与−8192/8191端点、多点不同高低字节、CC0/127、同 target 不同值、逻辑整数 / double / enum、SubVoice复合Bank目标、四边出视图、选择页冷热、取消 / Undo / Redo。必须比较最终图形与实际导出值，不能只断言“不抛异常”。

## 3. R18 / R19 / R20 / R21：共享鼠标交互重构

### 3.1 右键行为（R18，P1，待 D-UI01）

用户已决定取消300ms双击切工具。新的内容区状态机候选：

```text
Right Down：冻结 owner / revision / target / selection / 起点
未过阈值 Right Up：打开原上下文菜单，不人为等300ms
超过阈值：框选，取消菜单候选
框选 Right Up：提交选择，不补弹菜单
Escape / 失捕获 / 切Tab / owner失效 / source revision变化：取消手势
```

新手势取代旧手势或Surface卸载也须取消pending exact-hit、菜单和框选；迟到结果不得恢复旧Selection或覆盖当前菜单。这与既有选择修订隔离共用同一token。

命中冷页仍须后台 / 有界；“无延迟”是没有双击识别等待，不是要求 UI 同步解压冷页。菜单目标未就绪时建议立即显示不可执行的定位中框架，再按冻结对象更新；另一候选是仅等待实际后台命中。决策见 D-UI01，禁止沿用上回旧 target 开菜单。

旧右键画直线与 Shift+右键水平线将发生直接冲突，包含 Velocity 和 Conductor Tempo，不能漏掉。D-UI01.a 建议在 Draw 中增加可见 `Free / Line / Horizontal` 绘制形态，左拖执行；普通左键命中已有点仍用于移动，`Alt+左拖` 在密集点上强制执行所选绘制形态。Shift+左仍保留锁 Tick / 单点，不能拿它暗替水平线。产品所有者确认前不得删除旧入口。

| 表面 | 新右拖候选范围 / 必须保留 |
|---|---|
| Arrangement 实际 Segment 内容 | 框选正式 Segment；头部 / brace / 空轨外区域不借此创建或编辑 Segment |
| 三种 Piano | 框选 Note；左键继续当前 Draw / Select / Split / Erase 工具 |
| MIDI / Parameter 数值 lane、Tempo | 框选点；旧绘线迁移后保留完整采样密度与 Shift 行为 |
| Velocity | D-UI01.b 建议纳入，框选的是对应 Note，不画新的 velocity；须验证列表与 Piano 选择同步 |
| 时间尺 | 不当作普通对象区；保留现行定位例外；底部 / SubVoice 禁 Time Range 的规则不恢复 |
| Piano 左侧琴键尺 | 仍无右键菜单，不产生空菜单或穿透框选 |
| 浮动工具 / grip | 有独立命中优先级，不能穿透到背景启动第二手势 |
| All Tracks | 仍只读，不擅自增加对象选择 / 编辑菜单 |

框选修饰键建议沿用当前 Replace / Add / Remove / Toggle 规则；drag threshold 与冷页选择仍复用既有政策。中键 Pan 保留；已有 capture 时另一键不能建立竞争手势。

### 3.2 设置编辑指针而不清选（R19，P1，待 D-UI02）

不能回答“点顶部尺子就行”：`MainWindow.xaml.cs:6267` 标尺更新的是 **Playback Cursor** ，`:7330–7345` Select 单击清选后改的是 **Edit Cursor** 。

推荐保留 plain Select 内容区左单击的清选 + 设置编辑指针，新增 **Ctrl+左单击顶部时间尺** 只设置 Edit Cursor 并保留选择；普通顶部时间尺单击仍设置 Playback Cursor。这里明确更正旧的“Ctrl+左单击内容区”建议：内容区 Ctrl 单击 / 拖动已承担多选和复制拖动，不能被新定位操作占用。新手势仍待 D-UI02 确认，不是已存在能力；底部禁止 Time Range 的规则不因此恢复。

备选是 plain 左键只设指针、Esc 清选，但它改变用户希望保留的单击清选习惯。不擅自选择。增加 tooltip / 帮助说明，避免成为隐藏手势。

### 3.3 浮动工具（R20，P2；R21，P1）

用户要求新增 Copy / Cut / Delete 和多种批量编辑入口；若右拖可在所有左模式框选，则工具也不限 Select。

建议保留 Move、ResizeStart、ResizeEnd、Follow/Pin、grip 和全规模 delta 提示，补三项高频操作，其余批处理用一个明确的工具菜单避免挡住大面积视图。不是永久限制为三个新增功能，具体常驻按钮见 D-UI04。菜单与快捷键使用同一能力判断；不能因选择不支持 Move 就隐藏可用的 Delete。

混合选择只提供共同兼容操作 / 显式类型子菜单，不静默忽略一部分。复制拖动、Selection 后态、Undo前态与有限内存事务保持。

R21 候选原因：`TimelineSurface.cs:10213/3449` 用 viewport `YToLane`，`TimelineRenderModel.cs:1320–1328` 将 y 先夹到当前可见 viewport；使鼠标离开后无法表达连续世界坐标 delta。现有 `AutoScrollEditGesture(:3527)` 只在 MouseMove 驱动，不能据此保证鼠标静止在边缘外也持续滚动。

建议冻结 pointer-down 的世界坐标，capture 期间继续将外部位置映射成 delta；视觉裁切与数据约束分开。D-UI09 建议鼠标静止于边缘外时也持续自动滚屏并更新 delta。自动滚动与普通 Draw 拖动共用，不建立浮动工具专用低性能预览。MIDI key硬边界及普通 Move / Copy 的既有删除或 Clamp 规则保持；不要一律 Clamp所有Note并改变既有行为。

测试：三类Note、事件/Segment适用分支、1/10/40万/百万选择；上下出界、停留、返回、四角、Ctrl变化、失捕获、Escape、切Tab、鼠标静止边缘；预览、delta、最终结果一致，内存 / 首次冷区不退化。

## 4. R15：模板末端手柄（P2）

用户要求在 SubVoice 顶部时间线的 Template Length 位置显示窄手柄，拖动时显示新长度和 delta；Snap只量化delta。

当前 `ProjectEventInstrumentEditCommands.cs:7–22/385–414` 不允许模板短于 SubVoice Note尾、事件、曲线、Loop端点及 Pre-Roll 下界。**模板不是可任意 crop 的 Segment** ；不得默认“缩短后隐藏保留”也可成立。

推荐复用现有合法下界，D-UI05 决定拖到下界时饱和还是 Invalid。冻结原长度、Snap和最小长度；最小长度摘要需 revision-bound，不每 MouseMove 全扫内容。preview显示实际受限长度 / delta，抬起一次正式command，取消零变更；长度改变应刷新同Definition所有SubVoice视图并进入既有编译失效路径。不得移动Loop/Pre-Roll迁就手柄。

测试：内容尾 / 末端事件 / Loop单端与双端 / PreRoll、模板很长、长整型溢出、跨多个打开SubVoice、Snap非整倍原长度、Cancel / Undo / Redo。是否允许未来隐藏模板外内容另开语义需求，不搭便车实现。

## 5. R22 / R32 / R31：标尺、首开 Fit 与进度

### R22：全局锚定跳标（P3）

`TimelineGridPresentation.cs:54` Bar选择从可见 `startTick` 开始计算下一可标位置；`TimelineSurface.cs:9097–9107` 普通Tick ruler则已有 major倍数0锚定。应修有问题的公共Bar抽样，不盲目重写正确分支。

建议用全局小节序号的稳定抽样相位；文字仍为一基Bar。Segment通过ProjectTickOffset映射Project拍号图；不是在每个Segment重新“第1小节”。变拍号 / 截断小节 / 负content偏移 / 极端Tick均检查；Pan只能让标签进入或离开视口，不能让仍可见标签换成另一套序列。仅改绘制密度，不改Snap/音乐时间语义。

### R32：All Tracks 首开垂直缩放（P2）

当前 `AllTracksWorkspaceViewModel.cs:32–33` 默认 FirstLane48、LaneHeight15；`AllTracksView.xaml.cs:62` 手动Fit用全控件高度除128，没有扣ruler。新增需求应在**首次有效布局** 测量内容设备像素，不在每次激活 / resize重置。

建议 `N=max(4,floor(contentDeviceHeight/128))`，以显示key0..127为目标；N为整数，DPI转换只做一次。内容不足512device pixels时，无法同时做到N>3和全128key可见，D-UI07建议优先N≥4、允许滚动。未来恢复保存的有效视图时，恢复值优先于首次默认。

### R31：Compile 百分比（P2）

`DesktopSessionController.cs:217` 只公开 CompilationState 字样；`MainWindow.xaml:1289` Compile 文案固定。`Midora.Playback/ProjectCompilationSession.cs:639–685` 包含snapshot capture/materialize、Full/Incremental与结果投影，当前没有能直接绑定的完整进度计数合同。`Midora.Compiler/Contracts.cs` 的CompilationRequest也不包含该计数。

建议增加旁路运行期进度，按已有实际工作量边处理边计数；UI节流约每100ms一次，任务 / revision token隔离迟到更新。缓存命中、早期失败、取消、增量退回Full、后处理都包含，不能“30%忽然完成”或提前100%。

D-UI06建议仅有可靠total时显示百分比，否则显示 `Compile…` 或阶段名。若产品坚持全程百分比，需要明确它是阶段加权估计而非耗时承诺；不能为了精确total先扫描或展开巨大逻辑编译结果。进度不进入Project/canonical，不改编译正式顺序 / 时间性能。

## 6. 低优先级 / 后置性能和外观专题

### R10：连续滚屏（P3，先实验）

当前 `TimelinePlaybackFollowPolicy.cs:26–35` 是越出10%..90%区域后跳到约20%的窗口跟随；`MainWindow.xaml.cs:153/697/725` 使用33ms UI刷新。固定指针会持续改变世界坐标可视范围，比只画一条cursor成本高。

原范围只包括两类Segment Piano及All Tracks，默认不扩大到SubVoice。锚点、默认模式、手动操作和退让政策已经完整列入 D-VIS02.a～e / D-VIS03，待本次集中问答，不再留到实现中隐式决定。推荐保留跳跃模式为默认、连续模式可选，Follow 总开关仍默认关闭；连续锚点允许 0～100%（默认50%），不显示负Tick，手动Pan明确关闭Follow、手动Zoom保持Follow。上述是待批候选，其中手动操作政策会改变 SRS §20.1.9 当前的“临时中断”，不能当作纯渲染实现细节。

不改音频时钟。建议固定世界坐标tile、热tile平移复用、边缘后台预取、隐藏Tab停工，不随每像素滚动重建音符索引。性能 / OS能力门仍须实测；用户批准行为不等于已经证明可以默认启用新效果。

### R11：播放琴键色（P3，先实验 / D-VIS01）

当前 `TimelineSurface.cs:9155–9240` 琴键底图缓存与单个HighlightedPitch不是播放多音高状态。建议独立最多128key的轻量显示层，按明确来源和范围增量计数，不每帧扫所有Note、不修改音符tile、不让音频callback分配UI对象。

“按下”是视图源Note Gate还是canonical最终pitch，以及同key来源竞争、Onion颜色、Mute/Solo、停止 / Seek / Loop / Buffering，已拆到 D-VIS01.a～d 待回答。Logical mapping、Loop/Pre-Roll以及All Tracks混合Compiled模式下，两种来源不相同；二者也都不等于SoundFont仍在release的真实voice。推荐与当前所见图形一致的Gate活动层、不按Mute/Solo过滤、当前编辑轨道优先、其余取最上层来源色；Buffering冻结、Stop清空、Seek/Loop按新Tick更新。该推荐是视觉活动层，不是音频表头。

R10/11性能实验共同输出：相同镜头轨迹下cursor-only与连续滚屏 / 键色的p95/p99帧耗时、首次冷tile显示时间、后台队列 / 取消、内存 / 分配、underrun；稀疏、密集、长Gate、大量Onion来源全部覆盖。未通过前不承诺它们已适合默认开启。

### R30：Segment Tracks 左栏（P3）

顶部 `Tracks` Toggle 位于List左侧；展开面板位于对象List更左。复用Arrangement的轨头presenter、命令、唯一排序、group brace、runtimeMuteSolo，不能复制业务处理器形成第三套顺序。

默认隐藏是建议，不是原文已给定。“几乎所有操作”、单击/双击、目标Segment选择、删除当前owner和轨道操作目标已列 D-TRACK01.a～d 待集中回答：推荐复用Properties/改名/颜色/绑定/路由/复制剪贴板/排序/共享组/MuteSolo；不加入Segment内容绘制。单击共用Arrangement轨道选择，双击优先编辑指针所在Segment、空隙取最近且等距取前，空轨道不自动创建。删除当前owner沿用关闭相关Tab，轨道操作不无故清除音符选择。折叠即停止不必要订阅 / 查询；持久化只包含 D-STATE 批准白名单。

### R03：原生窗口效果（P3，建议发布前单轮）

源码大量使用 `WindowStyle=None`、WindowChrome与自绘边框（例如NewProject/About/ApplicationPreferences）。此轮未做Windows运行验证，不能声称只改CornerRadius就能获得原生阴影 / 动画。

范围和能力不可用时的回退见 D-WIN01；Win10实机条件也在 `04` 留有待填写项。实施前做独立小探针验证产品现有Chrome下的最小化 / 最大化 / 恢复、Snap布局、Win11圆角阴影与Win10方角阴影、DPI / 多屏 / 最大化工作区 / resize命中。支持能力以运行OS与窗口实际配置为准；不为效果引入透明大窗口造成渲染回退。不在本次写平台API已验证结论。

### R08 / R09 / R02：等待清单

- R08（P3）：用户先提供“菜单位置 / 命令 / Fluent图标 / variant”表；通用命令多入口共用图标，禁用态 / 对齐 / shortcut栏同时检查。本轮不替用户挑选。
- R09（P3）：D-KEY01/02 已列本次键位补充或授权推荐，以及是否扩大到可配置快捷键系统的问题。推荐保留既有键位、只为本批高频新增命令安排无冲突键位、不附带用户可配置系统；未回答前不视为授权。正式键表必须核对输入框、IME、代码补全、模态、Tab和只读视图；R18/19手势仍单独确认。
- R02（P4）：发布稳定后才做。闪烁只是用户举例，不固定为产品方向。动效方向、亚克力默认值、首次tile动效范围及性能退让政策已列 D-FX01.a～d 待回答。推荐短淡入、亚克力独立开关默认关闭、Reduced Motion / 禁动画可用；只动画独立视觉层，不删粗缓存、不使已显示内容再次空白、不改变命中 / 选择；编辑/Undo/Selection/热cache不反复动画，繁忙时跳过装饰而不延迟内容。

## 7. 现有自动测试扩展落点

共享Presentation测试：`TimelineValueTraceSamplerTests`、`TimelineEventContextGestureTests`、`TimelineRenderingTests`、`TimelinePlaybackFollowPolicyTests`。Application测试：`BoundedDirectMidiEventEditTests` 与SubVoice资源 / Lane命令测试。Desktop测试：`TimelineObjectListTests`、Catalog / Properties投影和workspace生命周期测试。

这些只是已存在的测试入口，不代表当前覆盖完整或本轮已运行。每个实现批次应增加能稳定失败于旧代码的回归测试，再对共享三宿主和性能预算验收。
