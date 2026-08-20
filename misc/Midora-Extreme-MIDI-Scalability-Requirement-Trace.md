# Midora 极端规模 MIDI 可伸缩性 Requirement Trace

状态：已实施；规模门通过，等待产品侧实机验收  
日期：2026-08-19  
适用范围：Pure MIDI source、SMF 导入、编译、Canonical、播放事件计划、IPC、持久化与 Timeline 展示

## 1. 目标与已确认问题

目标是在不削弱 Midora 既有确定性、Segment/Root 生命周期、精确事件顺序和音频质量的前提下，使百万到一亿级 Note 的 Project 可以用有界常驻内存打开、浏览、编译和播放。容量不能再由某个整文件、整数组或单次 IPC payload 的固定上限决定；远处事件数量不能线性增加从当前光标开始播放的准备时间。

优化前实现中已确认的放大链为：

```text
SMF whole-file byte[]
→ parsed event object graph
→ imported DirectMidiNote/Event classes
→ deep compilation snapshot
→ pending/final canonical arrays
→ audio projection copies
→ whole MDAP byte[]/worker arrays
→ Timeline full-item snapshots and indexes
```

7,335,699 NoteOn 的实测样本包含约 14,671,398 个 Channel Event；现有音频计划约需 29.34M records，仅 record body 已约 447.7 MiB，超过 MDAP 的 256 MiB/16M aggregate 限制。提高限制不能消除对象、数组和复制倍增，也不能解决远处事件阻塞 Startup。

## 2. Requirement Trace

| 项目 | 正式定义 |
|---|---|
| 输入 | SMF 1.0 Format 0/1 + TPQN；`.midora` Pure MIDI Track metadata/content pack；当前 Project revision 的 Logical/Pure MIDI source；播放光标与 consumer range。 |
| Project source 输出 | `MIDI Channel Root → Pure MIDI Track → Midi Segment → Direct MIDI records` 的既有逻辑模型；大量 records 由 immutable source pages + copy-on-write overlay 承载。 |
| 编译输出 | 与小型对象路径语义和正式顺序一致的延迟 Pure MIDI canonical range source、最多 16,384 records 的 consumer pages、aggregate metadata、source-aware fingerprint、Root 音频 fragment descriptor 与 preset summary。 |
| 播放输出 | 从当前光标开始按 250 ms tick/sample 窗口投影；主进程写一个 session 私有、append-only、24-byte fixed-record event stream，Worker 通过 seqlock 发布的 committed-prefix snapshot 和 262,144-record ring 有界消费。 |
| UI 输出 | 对可视 tick/pitch/target 范围的 value cursor 与 tile-local摘要；不建立全 Segment item arrays/indexes。 |
| 边界 | Source page：最多 65,536 records 或 4 MiB decoded；consumer canonical/endpoint page：最多 16,384 records；排序 run：4,096 fixed records、最多 64-way merge；rolling event producer batch：16,384 records；Worker ring：262,144 records。 |
| 缓存边界 | 每 Project 共享 decoded source-page LRU 默认 64 MiB；render/SMF sort working set 由固定 run buffer、最多 64 个 reader batch 和单个输出页限定；Worker event ring 约 6 MiB。没有第二份全 Project canonical decoded cache。 |
| 播放水位 | Startup 2 s、Low 0.75 s、Resume 2 s、Target High 6 s；水位同时约束 event preparation、IPC publication 与 PCM preparation。 |
| 失败条件 | malformed SMF/source pack、pack checksum/version/extent 错误、临时存储不足、无法原子发布、单个 opaque payload 超限、取消、rolling stream 截断/倒序/非法 committed snapshot、Worker 协议错误；均须失败原子。 |
| 诊断 | 报告导入/pack/sort/rolling stream 阶段及可获得的 Track/Segment/page、record/tick、expected/actual checksum 或容量；不得只报整 Project “too large”。 |
| 持久化归属 | `.midora` 保存 Track metadata protobuf 与每 Track 一个 `midi-content/mt_<id>.mpk`；不保存 canonical、IPC、UI tile、Undo/Redo 或 session page cache。 |
| 运行时归属 | session backing pack、Project-wide decoded page LRU、overlay generation、bounded sort runs、rolling event stream/control map/ring、Root PCM descriptor、preset summary、UI range cursor。 |
| 明确非目标 | 字节级 SMF round-trip；把整个 Project 常驻内存；每 page/record 一个文件；只提高旧上限；修改可听事件语义；让 bitmap 成为命中测试或领域数据来源。 |

## 3. 实施切片

1. 已完成可观测性：记录两遍导入、pack、编译、计划、启动窗口、managed heap 与 process peak working set。
2. 已完成 SMF 两遍流式导入：第一遍冻结结构计划，第二遍 FIFO Note pairing并直接写 transactional source page builders；文件路径入口不再 `File.ReadAllBytes` 或建立完整 parsed graph。
3. 已完成 Source pack 与 COW：版本化 `.mpk`、页目录/checksum、value cursor、range query、Stable ID page bounds、session backing、Project-wide LRU、编辑 overlay 与 snapshot sharing。
4. 已完成 Persistence：Track protobuf只保存 metadata；record body使用每 Track 一个 stored Zip entry `.mpk`，打开时顺序提取到 session backing，保存仍进入临时包—自校验—原子替换事务。
5. 已完成延迟 Canonical：Pure MIDI source在编译时保留为 range source；播放、SMF 导出和兼容性诊断使用固定 run buffer与最多 64-way 的磁盘外部归并，不建立全项目 pending/final arrays。
6. 已完成 Rolling audio plan：整项目播放不再把 paged Pure MIDI 写入 whole MDAP；sample-domain 事件按 250 ms 查询并写 committed append stream；Worker通过固定 ring背压消费。Preset preload使用有界 source summary，Pure MIDI Root PCM fragment恢复可复用缓存。
7. 已完成 UI range provider：Arrangement preview、piano roll、Velocity 与 event lane从 source range查询流式生成可视 tile，不再为 paged Segment建立全量 render-item arrays/indexes。
8. 已完成样本验收：按 1M、3M、7M、18M、32M 顺序通过后，运行并通过 164M 最终样本。
9. 已完成滚动事件边界回归：renderer 先消费 current-frame batch，再计算下一事件边界并受 `SafeThroughFrame` 截断；避免当前记录遮住同一 256-frame native block 内的后续记录。
10. 已完成 source-backed UI 回归：Arrangement preview 不能以空的兼容数组判断分页 source 无内容；Pure MIDI Segment editor 的存在性同时接受 Logical Segment 与 Midi Segment，普通编辑不得关闭仍存在的 editor。
11. 已完成超ring committed窗口回归：当已发布窗口超过262,144-record ring时，reader公布最后已装载record frame这一排他的partial safe frontier；renderer可推进并分批排空该frame来释放ring，且在完整同frame后缀安全前不生成后续PCM，消除reader等待ring空间与renderer等待完整窗口形成的稳定Buffering互锁。

## 4. 等价性与验证门

- 小型 golden：旧对象路径与 paged 路径在 Direct Note/Event、opaque、同 tick顺序、FIFO NoteOff、Root/Segment边界上的正式输出完全一致。
- 相同输入重复编译、无序 collection、Full/Incremental、分页方式变化必须产生相同 logical sequence 与 fingerprint。
- page boundary 覆盖 NoteOn/Off跨页、同 tick跨页、Tempo/Port/Channel state restore、Segment/Root硬边界。
- import/open/save fault 或 cancel 不得发布半个 Project/pack；未知/损坏 page必须隔离并拒绝正式消费。
- seek/loop/mute/solo 必须在稳定 producer frontier使用当前 committed event suffix；生产未覆盖 render frontier 时只能进入受控 Buffering。
- UI查询和命中测试随相交页数增长，而非 Project总 record 数；极端可视内容仍使用 tile/LOD。
- 每次规模测试记录输入文件 bytes、NoteOn/channel-event 数、source/canonical pack bytes、import/compile/startup耗时、peak managed heap、peak working set、page hit/miss 与 IPC in-flight peak。

## 5. 2026-08-19 实物样本结果

统一条件：Debug test host、48 kHz realtime plan、从 tick 0 查询开头 2 秒；`retained managed` 是强制完整 GC 后相对基线，`peak working set` 是独立 test host 的进程峰值。时间是本机一次实测，不是跨硬件性能承诺。

| 样本 Note 数 | 文件 | 导入 | 编译 | 计划 | 开头 2 s 查询 | retained managed | peak working set |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 1,000,000 | 8,010,662 B | 2.254 s | 1.17 ms | 37.0 ms | 126.6 ms / 884 events | 15.4 MB | 121.9 MB |
| 3,170,668 | 25,369,280 B | 4.889 s | 1.24 ms | 36.8 ms | 41.1 ms / 695 events | 4.6 MB | 91.6 MB |
| 7,335,699 | 58,686,394 B | 10.380 s | 1.13 ms | 37.5 ms | 29.3 ms / 27 events | 2.0 MB | 91.5 MB |
| 17,999,999 | 144,175,201 B | 29.458 s | 30.5 ms | 110.5 ms | 310.5 ms / 9,900 events | 38.1 MB | 166.4 MB |
| 32,548,613 | 260,546,663 B | 46.772 s | 13.1 ms | 36.9 ms | 131.9 ms / 1,949 events | 17.8 MB | 136.9 MB |
| 164,206,462 | 1,314,713,086 B | 231.908 s | 134.1 ms | 43.8 ms | 23.4 ms / 1,968 events | 22.3 MB | 320.2 MB |

最终样本生成 3,076 source pages、1,196,231,233 B content packs。上述结果确认总 Note 数不再决定编译常驻内存或开头窗口准备时间；导入时间和 backing-pack字节仍按源文件规模线性增长，这是 out-of-core设计的预期成本。

同日故障样本回归使用 `Platinum Grand III.sf2` 与真实固定版本 BASS/BASSMIDI：`I'm So Happy`、`ATAAI4` 均连续渲染到 100,000 frame，`Pi` 连续渲染到 1,400,000 frame，三者均为 `AudioRenderFaultCode.None`，已跨过原报告的 8,875、55,113 与 1,252,321 frame。另有最小流式计划固定覆盖 frame 0 后紧邻 frame 1 事件位于同一 256-frame block 的情况。三个样本的分页 Note source、后台 tile raster 与实际 WPF TimelineSurface 合成路径均产生可见像素。

同日后续Buffering停滞回归使用同一SF2，将真实`I'm So Happy`按正式导入→编译→rolling event IPC→BASSMIDI renderer链从tick 0连续拉取到tick 635,000，约57秒完成且`AudioRenderFaultCode.None`，实际跨过报告的tick 614,400与629,760～633,600区间。独立协议门另以未来同一frame的300,000条事件稳定复现修复前ring写满且safe frontier不前进的问题，并验证修复后全部record无丢失消费、最终推进到published through frame。

## 6. 需求依据

- SRS §12.25、§13.19.13、§13.30、§16.30、§18.2、§23.19、§24.8。
- `INV-065`～`INV-072`。
- `ADR-PMIDI-010`、`ADR-PMIDI-011`、`ADR-AUDIO-013`、`ADR-AUDIO-014`。

## 7. 2026-08-19 播放吞吐与 reusable 后续优化

状态：已实施并通过全量自动化、真实BASS合成及1M/18M/164M实物样本门；等待产品侧长时间实时播放验收  
输入：现有分页 Pure MIDI source、Root/Segment PCM generation、当前播放范围、运行期 Mute/Solo source 状态与播放期 UI tick。  
正式输出：Canonical 事件语义、同 tick 顺序、Root/Segment 生命周期、PCM stem 内容及缓存 key 均保持不变；只替换事件的索引、按需生产、缓存物理写入与 UI 展示路径。  
边界：播放事件仍以最多 16,384 records/page、16,384 records/IPC batch 和 262,144 records/Worker ring 为界；Source endpoint page 不超过 16,384 records；reusable generation 仍须完整后才可正式命中。  
失败条件：endpoint pack/index 损坏、乱序或 checksum 不匹配；event demand epoch 无法在安全 producer frontier 生效；journal block 缺失/重复/校验失败；generation 未完成；均不得发布部分 canonical 或可命中 PCM generation。  
诊断与指标：记录 Root PCM hit/miss、跳过的事件 source/window、event producer queried/emitted records、producer lag/SafeThrough、PCM read wait、journal backlog/committed generation。  
持久化归属：endpoint index 随 Pure MIDI content pack 保存；reusable journal、IPC、指标和 UI cursor overlay 只属运行时，不写入 `.midora` source model。  
明确非目标：禁用 SoundFont MMAP、把全部 `.mpk` 解码后常驻 RAM、让未完成 PCM block 独立命中、修改可听 MIDI 或 Mute/Solo 语义、只提高 ring/水位/排序上限。

实施顺序：

1. 由缓存命中计划建立 source→cache owner demand schedule；完整 PCM hit 的窗口不查询、不排序、不写 IPC 事件。Mute/Solo 首次使 owner cache bypass 后，从实际可听 frame 追加并原子发布新的 event generation，Worker 显式 Seek 后切换，随后永久启用该 owner 的事件生产。
2. Pure MIDI pack 增加局部有序的 NoteOn、NoteOff、Channel endpoint pages，以及中途启动所需的有界状态/活动 Note 查询入口；播放用有界 k-way merge，不再按 250 ms 窗口重复扫描相交 Note page或预分配 131,072-record sorter。
3. 缓存 miss 的 16,384-frame PCM blocks 直接顺序追加到 reusable generation journal；完整 generation 经校验后原子发布索引，未完成 journal 只形成可重整的孤儿，不再复制完整 sparse spool。
4. Tempo 使用不可变有序索引和二分/递增游标，仅在实际 Tempo 变化时通知；每个 UI timer tick 冻结一次 current tick；播放指针由独立轻量 overlay 绘制，不触发 Timeline 瓦片层完整 `OnRender`。
5. 保持 `BASS_MIDI_FONT_MMAP` 与“只预载计划引用 Preset”的既有 SoundFont 策略。

实施结果：

- Event demand使用source→cache-owner interval schedule。全命中窗口的demand-aware provider在触碰Pure MIDI pack之前返回；Monitoring首次影响cached owner后，从实际可听frame以append-only event generation重建suffix，旧代在Worker显式Seek前保持可读，并对本次playback generation单调启用synthesis。
- Source pack开发格式升级为`MIDMPK3` v3：原始source pages保持65,536-record/4 MiB上限，新增最多16,384-record的NoteOn/NoteOff/Channel endpoint pages；NoteOn目录额外携带页内最大end tick，active-note恢复只查询候选endpoint页。Track内使用page-directory裁剪与有界k-way merge；Channel状态每16,384 events建立checkpoint。
- Reusable miss由cache I/O bridge直接顺序写16,384-frame Pack journal blocks。完整key集合经结构校验后同卷采用Pack并发布索引；没有完整sparse payload复制阶段。
- UI timer只冻结一次tick；Tempo lookup为有序数组+递增/二分游标，Tempo未变化时不格式化/通知；红色播放指针由独立overlay绘制并复用Pen。
- 保持SoundFont MMAP；未加入全SF2私有内存复制或全`.mpk`解码常驻。

后续自动化证据：

- `MidiRenderEventStreamProtocolTests`：9/9，通过全命中provider在触碰source前抑制且零event输出、mixed hit/miss、Monitoring后的单调cache bypass、在已抑制prefix之后从实际可听frame发布并读取新event generation，以及Monitoring可中断尚未发布的极密250 ms旧代窗口。
- `AudioCacheSessionStoreTests`：16/16；direct journal跨多个block采用后逐字节一致，journal entry/live-byte指标正确。
- 原生BASS exact-cache回归：第二次播放native synthesis frames为0，输出PCM与首次miss逐字节一致。
- Audio BASS（含Native AOT Worker）：227/227；Compiler：287/287；Application：386/386；Desktop Presentation：125/125；Desktop：66/66；其余Common/MIDI/Persistence/Playback/MIDI Export/Audio Render/Audio Device门均全通过。

开发期v2基线实物规模结果（升级v3前的Release test host，48 kHz，tick 0开头2秒；最终v3结果见本节后续验收记录）：

| 样本 | NoteOn | source+endpoint pack | 导入 | 编译 | 开头窗口 | retained managed | peak working set |
|---|---:|---:|---:|---:|---:|---:|---:|
| I'm So Happy | 1,000,000 | 22,801,952 B | 2.750 s | 2.24 ms | 110.8 ms / 884 events | 14,462,032 B | 114,720,768 B |
| 9KX2 | 17,999,999 | 395,816,659 B | 28.424 s | 16.5 ms | 174.3 ms / 9,900 events | 23,617,224 B | 135,467,008 B |
| StarT 164M | 164,206,462 | 3,586,416,620 B | 239.117 s | 118.4 ms | 32.1 ms / 1,968 events | 15,695,352 B | 318,648,320 B |

相较v1，Pack增长来自确定性的播放端点索引，不是第二份托管对象图；18M的开头窗口准备仍为亚200 ms。最终164M门也已通过：虽然3.59 GB Pack构建保持与输入规模线性，编译只需118 ms、开头窗口查询32.1 ms，retained managed约15.7 MB、峰值工作集约304 MiB，确认常驻内存和当前光标准备没有重新随总音符数线性放大。

v2基线还在18M项目中点tick 104,449执行`includeStateAtStart`冷恢复：1.256 s内产生89,553条实际active/state/window events。该成本与恢复点的活动Note和输出事件量相关，没有回扫并物化中点前约900万个Note。

现行`MIDMPK3` v3最终实物门结果（同一Release test host，48 kHz，tick 0开头2秒）：

| 样本 | NoteOn | source+endpoint pack | 导入 | 编译 | 开头窗口 | retained managed | peak working set |
|---|---:|---:|---:|---:|---:|---:|---:|
| I'm So Happy | 1,000,000 | 22,803,680 B | 2.612 s | 3.17 ms | 115.3 ms / 884 events | 14,463,064 B | 112,623,616 B |
| 9KX2 | 17,999,999 | 395,837,179 B | 28.176 s | 20.1 ms | 184.5 ms / 9,900 events | 23,630,584 B | 158,003,200 B |
| StarT 164M | 164,206,462 | 3,586,609,116 B | 240.996 s | 118.0 ms | 34.1 ms / 1,968 events | 15,888,112 B | 316,354,560 B |

v3新增的active-note页级interval bound只增加目录元数据，没有复制第四份Note records。最终164M门的Pack约3.59 GB、retained managed约15.9 MB、peak working set约302 MiB。18M中点tick 104,449的`includeStateAtStart`冷恢复降至566.8 ms并产生同样89,553条实际事件；该次主动查询会填充64 MiB共享解码LRU，因此其独立测试的retained managed约76.3 MB，不应与上表只查询开头窗口的常驻值混比。

真实BASS smoke gate另以9KX2和`Platinum Grand III.sf2`合成开头96,000 frames：完整完成、`AudioRenderFault=None`。Exact PCM cache原生回归确认第二次命中时native synthesis frames为0且PCM逐字节一致。
