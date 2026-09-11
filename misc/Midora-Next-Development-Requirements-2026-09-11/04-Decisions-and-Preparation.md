# 待决策与用户准备清单

日期2026-09-11。**下面推荐项均未自动定案。** 用户可按编号回复，也可只决定下一批需要的条目；后续专题的决定可以到该专题开始前再做。

本次仅归档，未发起实施。原优先级与完整要求见[总表](00-Overview-and-Delivery-Plan.md)。源码风险与验收在[01](01-Interaction-and-Display.md)、[02](02-Instrument-Changes-and-Lanes.md)、[03](03-Multi-Instance-and-Workspace-State.md)。

## 1. 不需要重复确认的方向

- 保留全部32项与用户原优先级；动画最低且发布稳定后；菜单图标等用户先挑；连续滚屏/播放琴键色先评估性能。
- R18取消右双击；右拖固定框选、右单击抬起无人为双击等待。还需决定的是旧绘线功能的新入口，而不是再问要不要取消双击。
- R06视图和Track Mute/Solo随显式保存，不进入Undo/Redo、不标音乐Modified；还需定义“完整”的字段边界及保存安全。
- R27保持Pure MIDI原始事件兼容；统一选择器使用名称与数值，默认试听Key60/Velocity100；不在Logical Segment放Instrument Changes。
- R28 Velocity固定第一、Instrument Changes若有固定第二、按target独立Tab、可重排、横向滚轮、溢出列表、逐Tab纵轴。
- R14所有事件创建交互要像Note一样扩模板；R23 Create短文案/Browse加宽；R24成功后转新Lane；R25三类List默认400。这些是可直接按需求验证的事项。

## 2. 近期进入相应代码前必须决定

### D-UI01：右拖固定框选后，旧直线 / 水平线入口放哪里？

关联R18；进入A3前阻塞。

- **A（推荐）**：Draw提供明确 `Free / Line / Horizontal` 绘制形态，左拖执行；右拖始终框选。同步Event/Parameter、Velocity和Conductor Tempo。
- B：为直线和水平线分配新的明确修饰键组合；先做全局冲突表，不能用已承担Shift锁Tick的组合。

不推荐Alt+右作为暗例外，因为会破坏“右拖固定选择”的一致性。保留Select左单击/框选、中键Pan、底部ruler禁止Time Range、琴键尺无右菜单。

另一个菜单细节：冷页exact-hit未完成时，推荐立即显示“定位中”的禁用菜单框架，目标就绪后填充；候选是只等待必要的后台命中再显示。不允许UI同步读冷页，也不允许用旧选择误开菜单。“无延迟”定义为无300ms人为等待，不承诺任何冷I/O为零。

### D-UI02：改变蓝色编辑指针但保留选择

关联R19；进入A3前阻塞。

- **A（推荐）**：Select模式Ctrl+左单击且未拖动，只改Edit Cursor、不改Selection；plain左单击仍清选+定位。给可发现的tooltip/帮助。
- B：plain左单击只定位，Esc或其他明确动作清选；改变现有单击清选习惯。

顶部ruler当前改的是Playback Cursor，不是现成替代方案。需要核对Ctrl点击实际对象与空白的路由，避免与Ctrl多选/复制拖动混淆。

### D-UI03：批量事件Clamp已有合同；是否另外改变形状规则？

关联R13；**保留现行规则即可修复，不是A1阻塞项。** SRS §20.1.7已有共同delta饱和规定。

- **A（沿用现有合同）**：整组共用一个受限delta；任何点先触边，整个形状停止继续越界。符合当前Logical Parameter/SubVoice的共同delta实现；修复Direct MIDI不一致与PB进位。
- B：每点单独Clamp；到边界的点停住，其余点可继续移动，形状可能被压平。

两者都可做到不报`data2`越界；原文未要求改变已有相对形状，因此按A实施。只有用户主动选择B时才修改规格。无论哪种，preview、delta文本、MouseUp提交必须一致；在标量值上变换后再编码MIDI字节。

### D-IN01：乐器变化点用什么身份，是否重开后仍包装？

关联R27；A2a前阻塞。

- **A（推荐候选）**：raw音乐事件仍是唯一事实，显式包装关联稳定成员ID，跨保存保留。将影响选择/删除单位的关联视为严格版本化编辑组织数据，评估对应source格式；它不得改变raw输出。
- B：关联只存presentation，音乐/旧版本兼容简单；但presentation损坏或被旧版重写时回到独立raw，包装操作体验可丢失。
- C：新增正式InstrumentChange源对象，由Compiler展开。整体身份清楚，但Domain、Mapping、wire和编译回归范围最大。

三项都不能采用可变数组开始下标+长度作持久身份。不推荐只存会话关联，否则重开后新核心功能会退回裸事件且用户可能误以为丢数据。

### D-IN02：包装成员被raw编辑，以及与原有同Tick事件碰撞

关联R27；A2b前阻塞，A2a先冻结规则。

推荐组合：

1. raw成员被单独改值/移动/删除后解除该包装，剩余raw数据保持；不擅自同步其他成员或重建被删消息。
2. 从新入口创建完整包装时，CC0/CC32/PC等实际命中target按现有later-wins归并，在一个事务里全部完成；失效旧包装同步解除。没有被命中的导入重复不清洗。
3. 导入后不自动将同Tick的独立事件认定一组；以后可另做显式组合命令。

替代选择是raw修改始终同步整组，或碰撞时整组拒绝；它们分别改变独立raw编辑含义或既有later-wins习惯，需显式批准。

### D-IN03：旧SubVoice和Initial State的部分字段

关联R27；A2a前阻塞。

推荐：新建优先用完整选择器，但旧Bank/PC和缺MSB/LSB仍可通过高级展开编辑；精确Mapping目标不删。Initial State统一行保留每字段“继承 / override”，选完整preset时才明确覆盖三字段，打开/浏览不自动补0。

用户原文只要求Event Instrument全局和SubVoice Initial State。推荐本轮**不扩大到Project Global Initial State**；若希望全局统一可以一并批准，但要保留相同继承语义。

### D-IN04：Program显示编号

关联R27；选择器前确认。

- **A（推荐）**：新数值输入统一0–127，明确 `Program (0–127)`，名称同时显示；保留raw数值，不制造偏移。
- B：UI统一1–128，底层0–127，所有入口用一个中央转换器；需同步旧列表/Properties文案，避免混用。

SRS旧§8.55.1曾写UI1–128，但当前数字入口与Catalog键值需全面核对；不要不说明就把+1差异留给用户猜。

### D-IN05：自动试听的时长与安全行为

关联R27；音频接入前确认。

推荐自动选择试听采用短单音（先试300ms），快速连续换选仅保留最新请求；键盘手按走held预览。默认Key60/Velocity100已确定。无Enabled SoundFont允许编辑、禁试听；播放中不抢正常Project播放，只按现有模态/音频任务规则处理。

Key/Velocity修改后的记忆范围建议先弹窗会话内，若需程序级记忆再加入Preferences白名单。还需明确试听上下文：

- **A（推荐首切片）**：独立preset audition，使用所选三元组、目标初始Melodic/Percussion模式、干净控制器状态；明确不是当前时间线听感重现。
- B：当前时间线上下文audition，使用正式Root活动状态查询，包含范围前事件和后续privileged SysEx造成的模式改变；实现和性能验证更重。

无论哪种，不凭Channel10默认鼓声，也不把Root初始Mode等同于整个时间线始终有效的Mode。

Master/Limiter、配置冻结、预载、独立ownership、编辑/关闭优先是既有正确性要求，不是可以省略的选项；需要先核查当前裸音高试听链，不能只复制UI。

### D-LANE01：Tab能否关闭，关闭是什么意思？

关联R28；A2b前阻塞。

- **A（推荐，新增候选）**：Close仅隐藏Tab，不删数据或Mapping owner；从Tab列表/显式选择目标重新打开，刷新不自动抢回隐藏Tab。
- B：只有空Tab能关闭；用target计数/曲线摘要判断，不能全表扫描；仍不隐式删除Mapping。
- C：不提供关闭；实现最直接，但大量导入target会长期占Tab头。

不论选择，Delete Data / Delete Mapping owner是独立命令，不是“关窗口”的副作用。

## 3. 后续各项实现时再决定

| ID | 关联 | 推荐 / 主要分歧 |
|---|---|---|
| D-UI04 | R20 | 常驻Copy/Cut/Delete +原Move/左右Resize/Follow/Pin；其他批处理用菜单。若要求更多常驻，先列最常用命令 |
| D-UI05 | R15 | 手柄到正式模板最小长度时饱和并显示有效delta；备选Invalid拒绝。绝不附带“模板外内容隐藏”新语义 |
| D-UI06 | R31 | 可靠total才显示百分比；无法可靠计量时显示阶段名。若要求始终百分比，需接受明确的阶段权重估计，不能增加全工程预扫描 |
| D-UI07 | R32 | N≥4px/key优先；不足512device-pixel内容高度时允许纵向滚动，不能同时承诺完整128键全显。备选小窗允许3px/key |
| D-UI08 | R05/R16 | SoundFonts入口遵守播放中设置禁用；列表标准刻度先一项。只读播放中设置浏览/不同滚动量可另选 |
| D-VAL01 | R12/R29 | 首批target明确到CC号；Pan(CC10)、Cutoff候选(CC74)用raw−64。Properties/List/图形用display，已有表达式/预设仍raw并标注；阶梯仅适用正式状态型点，不改Value Curve |
| D-VIS01 | R11 | 按当前所见来源Note Gate做琴键色，而非声卡实时voice；同key多来源建议顶层来源色优先，与Onion顺序一致。是否过滤Mute/Solo需定：建议图形源规则不混音频过滤，若要仅发声轨明确切换语义 |
| D-VIS02 | R10 | 先测后决定默认跟随模式与可配置锚点；至少评估最左/居中，是否允许任意百分比待定。手动Pan暂停跟随、再次显式Follow恢复是候选，不改变音频时钟 |
| D-TRACK01 | R30 | 默认隐藏；复用轨头业务。双击Track如何定位其Segment、删除当前owner、哪些“几乎所有”操作排除，实施该轮再固定 |
| D-OPEN01 | R04 | 每请求一个文件，`.mid/.midi`均走现有MIDI导入；多个文件明确提示不静默只取第一个；Open With先支持手选exe，不擅自注册默认关联 |

这些不阻塞不相关的P0/P1。R03的Windows效果先用实际系统探针验证；R02动画无需现在逐参数定案。

## 4. 工作区恢复专题的决策

### D-STATE01：字段白名单和组Mute/Solo

推荐采用[03 §3.2候选表](03-Multi-Instance-and-Workspace-State.md)中的轻量布局/导航字段，Track与独立Usage/Root组Mute/Solo一起保存；仍不保存大Selection、Undo、模态草稿、正在运行的音频/任务或缓存。

需决定Edit/Playback静止指针、Diagnostics筛选、当前工具模式是否也纳入。若“完整”还指全量选区，必须单独评估百万级保存成本，不能默默包含。B1须实测并数值冻结presentation字节/条目、恢复并发/缓存预算与超限行为，不以“轻量状态”代替可执行上限。

### D-STATE02：恢复优先级与损坏

推荐已验证section分别恢复，坏section默认+Warning，音乐加载不失败；Stopped启动，绝不自动继续播放。普通Save即使没有音乐Modified也保存视图，关闭时不因视图变化单独提示。

旧软件不认识新presentation后可能保存为旧版本并丢失新增视图字段；这是需说明的兼容限制，不是音乐数据损坏。是否要求额外保留未知presentation字节需独立决定，不能偷改strict-schema规则。

### D-STATE03：同Track即时共享的边界

推荐按Track ID拥有通用profile；Grid/Snap/默认音长力度从目前“全部Track共享”收窄为“同Track共享”。同Track同步zoom，不同步各Segment滚动起点；Lane真实存在性不共享创建。

显式Arrangement打开到Edit Cursor仍可一次覆盖位置，普通切Tab /重开项目优先恢复原viewport；Track Duplicate复制轻量profile后独立，Segment迁入使用目标Track通用profile。逐字段最终表在B1冻结。

## 5. 多实例专题的决策

### D-MI01：同一文件多窗口写入

推荐同一Project文件只一个可写会话，第二次定位已有实例或明确提示另存副本。备选允许多可写但保存冲突必须显式解决；不推荐最后保存者静默覆盖。独立ownership lease可以不长期锁原.midora；仅长期锁源文件才改变§16.17.2的“不长锁”。普通Save是否检测外部identity变化须另作明确决定；无论选哪种，都需ADR补足多实例写入协调。

### D-MI02：程序配置和资源预算

推荐ProgramRoot共享Preferences/Catalog/Presets/Recent，短写锁+revision冲突处理；其他实例音频任务继续冻结配置，Idle后接新值。Cache quota按ProgramRoot合计，不按实例数乘倍。若希望每实例独立偏好，则另设可见profile概念，成本不同。

### D-MI03：跨项目对象支持范围

推荐先同TPQN三类Note和可表达的数值MIDI Event，优先Note Art；Parameter/Segment/Track/Definition及Mapping闭包分后续子阶段。也可要求首个正式多实例版全部覆盖，但验收/格式/冲突成本显著更高。

跨Project不能按名称自动绑定参数/Definition，也不能保留原整数ID碰巧匹配到另一对象。Cut在源端一条command，Paste在目标端另一条，不提供跨进程共同Undo。

### D-MI04：不同TPQN

推荐第一切片只允许相同TPQN，差异明确拒绝；后续基础Note/Event可批准按节拍比例换算。备选保留Tick数（会改变音乐时值），或完整对象逐项转换（包含Loop/PreRoll/Curve/表达式常量问题，不能承诺无损）。这不是纯实现细节。

### D-MI05：源关闭后Clipboard有效性

推荐源快照完整发布后，用有预算与lease的独立backing支持源Project关闭 /实例退出后的Paste；Clipboard被替换但已有接收继续持lease。备选仅源实例存活可新发起Paste，成本低但便捷性差，必须明确提示。

同时决定跨portable ProgramRoot/软件版本的支持范围；推荐同用户、明确兼容协议版本，不承诺任意旧版 / 新版互通。

## 6. 用户准备与下一步

| 项目 | 等待内容 | 当前是否阻塞近期小修 |
|---|---|---|
| R08菜单图标 | 每条菜单命令的Fluent图标名及Regular/Filled，允许复用关系 | 否 |
| R09快捷键 | 要新增/调整的命令与手感目标；冻结当前冲突清单后决定键位 | 否 |
| R10/R11 | 性能实验报告后确认可接受效果/默认值，不要求用户先手动全量跑基准 | 否 |
| R03窗口效果 | 实施该轮确认可测Win10/Win11环境；没有某OS不冒充验收通过 | 否 |
| R27与R18/19 | 对应D-IN、D-UI决定 | 只阻塞对应切片 |
| R06/07/R01 | 对应D-STATE、D-MI决定 | 不阻塞前面的P0/P1 |

建议用户下一次若只想先开发可独立的小修，可直接批准A1；R13沿用既有共同delta规则，无需再决定。若希望先定核心音色体验，可先回复D-IN01–05。无需一次回复所有未来决策，也无需重新逐项确认已明确的32个目标。
