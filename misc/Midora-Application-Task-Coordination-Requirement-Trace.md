# Midora 非 UI 应用任务协调与偏好 Requirement Trace

状态：已实现并完成自动化验证

日期：2026-08-20

上位规范：SRS 第 3.12、13.5、13.14、14.18、15.14、17.2.2、19.2、19.10、20.14 节及 INV-004、INV-005、INV-018、INV-024、INV-037。

## 1. 输入与正式输出

- 输入：当前 `ProjectCompilationSession`、`PlaybackController`、一个待执行的全局任务、任务取消请求、Project 切换时的 Function Draft/未保存状态决策、New Project 的创建参数/SF2/首次目标、Open MIDI as New Project 候选、Open Project 候选路径、Save/Save Copy 的 package/路径/覆盖授权、成功激活的持久化 Project 路径，以及当前 Windows 用户的 Application Preferences/Recent Projects 本机状态。
- 正式输出：唯一活动任务的种类、阶段与锁级别；结构化完成/取消/失败/忙碌/播放清理风险结果；一次性清理风险 continuation；Project 切换保护结果；仅在完整验证/首次发布成功后返回的 New Project 候选；仅在严格读取完成后返回且拥有诊断/Embedded 资源的 Open Project 候选；Save/Save Copy 后与磁盘一致的 current path/file information/Document 保存基线；已验证并自动保存的本机偏好快照；成功激活后显式记录的有界 Recent Projects MRU。
- MIDI Export 与 Audio Render 在取得应用任务锁和 Project 编辑锁后才调用 request factory，因此 canonical、SF2、参数与最终路径快照在正式任务开始点冻结。
- 本层不产生音乐语义；播放、MIDI 导出和音频渲染继续只消费各自现有的 canonical 派生入口。

## 2. 任务、锁与顺序边界

| 操作 | 自动 Stop 播放/预览 | 锁级别 | 可取消 |
|---|---|---|---|
| 主播放、Segment/Event Instrument/SubVoice Preview | 不抢占其他任务 | Project Edit | Stop |
| Explicit Compile | 否 | Project Edit | 是 |
| New/Open/Close/Exit | 是 | Main Window Modal + Project Edit | 仅 Open |
| Save/Save Copy | 是 | Main Window Modal + Project Edit | 保存事务开始后否 |
| MIDI Export | 是 | Main Window Modal + Project Edit | Finalizing 前是 |
| Audio Render | 是 | Full Application + Project Edit | Finalizing 前是 |

- 同时只允许一个全局任务；忙碌时直接返回 `RejectedBusy`，不排队。
- 只有 SRS 指定的 Save、Save Copy、New/Open/Close/Exit、MIDI Export、Audio Render 可以自动 Stop；Stop 后不恢复播放。
- `Stopping` 和已预留 admission 期间不接受第二个命令。
- Project 编辑锁使用计数 lease；播放层与应用任务层可以安全嵌套，任一所有者只能释放自己的 lease。
- Save/Save Copy 通过 `ProjectPersistenceCoordinator` 直接使用该打开会话持有的单调工程时间源；首存成功才提交 path/file information/保存基线，Save Copy 不改变当前 Document 状态，且不能选择当前 Project 自身路径。
- New Project 候选由 `ProjectCreationCoordinator` 在旧 Project 之外构建；Create Unsaved 不具有路径，Create and Save 在 package 原子发布后才返回。External SF2 必须已有首次目标并采用受限相对引用；Embedded SF2 运行时快照随候选返回并由会话所有者释放。
- Open Project 候选由 `ProjectOpenCoordinator` 在旧 Project 之外严格读取；成功前不编译、不启动工程时间且不占用源文件，成功后才允许同一候选构造 Document/Persistence/SoundFont Runtime 会话。package recovery 转换为独立 dirty reason，Damaged Placeholder 或不可用 Embedded 资源持续禁用保存。
- Project 切换固定顺序：Stop/cleanup → Function Draft Apply/Discard/Cancel → 取得 Project 编辑锁 → 未保存 Save/Close Without Saving/Cancel → `BeginProjectClosing` → 实际切换。Draft Apply 必须在编辑锁外完成；嵌套 Save 一旦开始使用不可取消 token。Guard 与 Save 仍计入当前 Project 工程时长；实际切换失败时 `CancelProjectClosing` 从失败完成后恢复累计，成功后保持暂停直到旧会话释放。

## 3. 失败、诊断与原子性

- 普通任务异常返回 `Failed` 并保留原始异常；取消返回 `Cancelled`；任务总在 `Finalizing` 后释放 Project 锁和全局任务槽。
- 播放清理失败时，Save/Save Copy 按 SRS 继续并在结果中携带 cleanup error；New/Open/Close/Exit/MIDI/Audio 返回 `RequiresPlaybackCleanupConfirmation`，不执行主体。
- 清理风险 continuation 与创建它的 coordinator 和原命令绑定，只能消费一次；继续后仍把原 cleanup error 带入最终结果，不能把风险静默丢失。
- Playback 进入 Error 后不再占有活动任务或 Project 编辑锁。
- Application Preferences 的非法值拒绝且不 Clamp；读取、解析或写入失败使用 SRS 安全默认值并返回非 Project notice。偏好文件限制为 1 MiB，避免无界读取。
- 偏好写入使用同目录临时文件、落盘 flush 和原子 move/replace；发布前失败不破坏原文件，运行时切回安全默认值。
- Save/Save Copy 的参数、取消、序列化、自校验、发布或清理失败不提交 current path/file information/保存基线，并总是释放应用层持久化操作槽；Damaged Placeholder 在 staging 前返回结构化不可保存原因。
- New Project 输入、SF2 hash/loadability/内容复核或首次 package 事务失败时不返回候选并释放 Embedded 临时资源；调用方只替换成功返回的候选，因此原 Project 保持不变。候选阶段不启动工程时间会话，提交为当前 Project 后才开始累计。
- Open Project 的路径、取消、容器/manifest/hash/schema/结构/版本失败不返回候选；成功读取后的 Embedded 提取资源由候选唯一拥有并在候选放弃或 Project 关闭时释放。外部 SF2 不可用不阻止源 Project 打开，只在提交后的 runtime refresh 中产生不可用状态。

## 4. 偏好归属与失效规则

- 当前保存：System Default/输出设备 ID、Render-Ahead 20–2000 ms（默认 100）、Device Buffer Request 5–200 ms（默认 50）、Realtime Maximum Sample Voices per Stream 1–16,777,216（默认 750）、可选的 Default Embedded SoundFont 本机绝对文件路径，以及 Open、Save/Save Copy、SoundFont、MIDI Export、Audio Render 五类 picker 最近目录。
- 音频偏好只允许在 Playback `Stopped` 且无其他应用任务时提交；非法值不提交、不静默 Clamp。
- 音频偏好实际变化后立即清除 sample-domain 计划并发出 `RealtimeAudioPreferencesChanged`，由未来应用 composition 重建实时 Worker/设备连接；当前任务的冻结值不被中途改写。
- 最近目录只作为对应 picker 起点；必须是完全限定目录，可被清空，不进入 Project，也不转换为默认导出路径。
- Application Preferences 与 `.midora` 版本独立；不标记 Project Modified、不进入 Undo/Redo，不持久化覆盖授权、关闭不保存、丢弃草稿、播放状态、设备枚举结果、实际采样率/buffer、IPC 状态或任务历史。
- Recent Projects 与 Preferences v1 分文件保存，最多 10 个 Windows OrdinalIgnoreCase 唯一绝对路径；只在持久化 Project 成功激活后记录。离线路径保留并只投影当前可用状态，读取损坏回到空列表、写入失败保持旧列表。
- UI 布局、窗口、Grid/Snap 等纯 UI 偏好明确不属于本次非 UI 实现范围。

## 5. 小决定与明确非目标

- SRS 只规定“当前 Windows 用户本机”和独立版本，未规定具体本机路径或编码。实现采用 `%LOCALAPPDATA%\Midora\preferences-v1.json`、source-generated UTF-8 JSON v1 和原子替换；该小决定登记为 Q-NUI-004，待产品所有者确认。
- 明确非目标：命令队列、后台多 Project、多任务并发、Pause/Scrub、自动恢复播放、Preference import/export/sync/profile、WPF 对话框/Notice 展示、窗口布局和 UI 状态。

## 6. 自动化验证门

- 单任务排他、非允许命令不抢占、允许命令自动 Stop 且不恢复。
- Save 清理失败继续；风险命令产生一次性 continuation；嵌套 Project 锁正确释放。
- 任务取消、Finalizing、Full Application 锁、Save 不可取消。
- Project 切换顺序、Draft Apply 锁外执行、未保存处理锁内执行、Save 不可取消、Cancel 和 Save unavailable 分支；Guard 期间计时、实际切换边界暂停、成功保持暂停及失败恢复不补计。
- 偏好默认值、边界值、确定性 JSON 往返、未知/损坏/超大/版本错误回退、写失败回退、绝对目录、用途隔离、Stopped-only、任务忙碌拒绝及 sample-domain 缓存失效。
- 首存、已有目标覆盖、当前路径 Save、Save Copy 状态保持、工程时间快照、Damaged/Embedded 资源门、取消和持久化并发拒绝。
- New Project 默认图、Unsaved/Persisted 语义、Metadata/TPQ、External/Embedded SF2、覆盖、竞态、资源清理、严格重开和提交前不累计工程时间。
- Open Project 的 `.midora`/`.zip`、无长期文件占用、恢复 Modified、未知 entry、Damaged Placeholder、Embedded/External SF2、候选资源释放、取消/阻断失败和同 Project 工厂约束。
- Recent Projects 的 MRU/大小写去重/10 项淘汰、严格/有界 JSON、确定性往返、离线路径保留、原子写失败保持、移除和清空。
- Default Embedded SoundFont 的绝对本机路径验证、显式清空、确定性 JSON 往返、启动时缺失自动清空、任务开始前二次缺失降级，以及 New Project / Open MIDI as New Project 的 Embedded snapshot/验证/失败资源释放。

## 7. Task overlay 展示增量（2026-08-20）

- Task overlay 不再展示内部 `LockLevel`（例如 `MainWindow`）。没有可靠数值进度的任务使用真实 indeterminate 动画；可靠数值进度继续使用 determinate bar。
- Cancel 行只在任务声明 `CanCancel` 时存在。Save / Save Copy 的事务阶段按既定原子性保持不可取消，因此 UI 隐藏 Cancel，而不是显示一个无响应按钮。
- Save / Save Copy 的序列化、自校验、压缩与文件事务在后台工作线程运行；WPF Dispatcher 只负责 overlay、命令锁和完成后的视图刷新，使 indeterminate 动画和窗口消息泵在大型 Project 保存期间继续响应。该线程迁移不放宽 Project 编辑锁，也不改变保存事务的不可取消阶段。
- 本增量只改变运行时任务展示，不改变任务 admission、锁、取消 token、持久化事务或失败原子性；Task 状态仍不进入 Project、Undo/Redo 或 `.midora`。

## 8. 新建 Project 默认 Embedded SoundFont（2026-08-20）

- Application Preferences 可保存一个可清空的本机 SF2 绝对文件路径；不接受相对路径或 UNC 路径。该路径只属于当前 Windows 用户的本机偏好，不进入 Project、Undo/Redo、canonical 或 `.midora`。
- 该偏好只作用于未显式选择其他 SoundFont 的 `New Project` 和 `Open MIDI as New Project`。创建流程复用正式 Embedded 导入：流式复制到 Project 会话拥有的临时资源，同时计算完整 SHA-256，再执行 BASSMIDI loadability validation；成功候选持有独立 snapshot，源文件后续变化不改写当前 Project。
- 应用启动时若路径不存在则自动清空并原子保存偏好；每次创建任务开始前再次检查，竞态缺失视为未配置并保持新 Project 无 SoundFont。其他读取、权限、格式或 loadability 错误仍显式失败，不把损坏文件静默降级为空。
- MIDI 导入候选先 detached 完成；默认 SF2 绑定和验证也在替换当前 Project 前完成。失败时释放候选 Project 与 Embedded 临时资源，保持原 Project 不变。
