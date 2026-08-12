# Midora Desktop 后台增量编译 Requirement Trace 与并发决策

状态：2026-08-12，已批准实施。本文件记录运行时并发模型，不修改 SRS。

需求依据：SRS 12.21、17.7.3、18.10.4、20.11.6、20.11.12，以及跨系统不变量 INV-009、INV-010、INV-015。

## 1. 输入、正式输出与所有权

- 输入仍是完整 Project、合并后的 `ProjectChangeSet` 与明确的 `CompilationRequest`。
- 正式输出仍是与某一确定 Project source revision 对应的 `CanonicalCompiledResult`；播放、预览、MIDI 导出和音频渲染不得消费过期结果。
- Project 编辑、Undo/Redo 和 History 仍只在调用线程原子提交。后台任务只在短暂的会话访问门内把合并变更同步到独立编译镜像；随后只读取该镜像并生成会话内派生结果，不能修改实时 Project 或 History。
- source revision、compiled revision、待编译 change set、取消令牌和后台 worker 只属于打开的 Project 会话，不持久化到 `.midora`。

## 2. ADR-BGC-001：Desktop 编辑提交与编译解耦

- `ProjectCompilationSession` 保留默认同步模式，维持核心调用方和非 Desktop 工作流的既有契约；正式 Desktop 会话显式启用后台模式。
- 后台模式下，编辑提交只完成 Project/History 修改、source revision 推进、变更集合合并和派生缓存失效，然后立即返回。它不在 WPF Dispatcher 上执行 canonical 编译。
- 只有会影响 canonical 结果的 Everything、Conductor、Track 或 Event Instrument 变更使编译结果 Outdated。仅影响音频 PCM cache generation 或纯展示/元数据的变更不制造伪编译任务。
- 后台 worker 单实例串行运行，并使用短 debounce 合并连续手势提交。不存在同一 Project 的并行正式编译。

## 3. ADR-BGC-002：latest-wins、协作取消与事务缓存

- 新编辑到达时，先请求取消正在运行的旧 revision 编译，再在 Project 访问门内提交编辑；旧编译不持有该访问门，因此编辑不等待编译退出。worker 在镜像同步、语义验证、Track 展开、Overlap、分配、事件物化和范围处理的有界位置检查取消。
- 被取消或完成后已过期的结果不得发布为当前 diagnostics/canonical result。待编译 change set 继续保留从最后已发布 revision 到最新 source revision 的并集，因此不会漏掉增量失效。
- compiler 的 Track/Segment checkpoint cache 以工作副本执行；只有一次编译正常返回时才提交。取消或异常丢弃工作副本，防止半更新 cache 污染下一轮增量结果。
- 若旧编译在取消生效前自然完成但 source revision 已推进，结果仍被丢弃；worker 立即对最新 revision 继续编译。

## 4. ADR-BGC-003：实时 Project 与后台编译镜像

- 后台模式为当前会话维护一个不持久化的 Project 编译镜像。镜像保留全部正式编译输入及 Stable ID，但不成为第二套业务模型，也不允许 UI 或消费者直接编辑。
- 普通 Track/Event Instrument/Conductor 变更只复制对应变更对象；未变对象在镜像内保持身份，以继续复用 compiler checkpoint。Everything 变更建立完整新镜像并按 Full 失效语义处理。
- 镜像同步只在后台 worker 上执行，且具有细粒度取消检查。新编辑会在取得 Project 门前先取消同步，因此即使变更对象含极大量音符，编辑等待也只受一个有限复制批次约束，而不受整次编译时长约束。
- 一次编译只能读取同步完成后的镜像。同步中取消不会发布半同步镜像；旧 revision 的编译可以在镜像上完成清理，但其结果仍受 revision/generation 门拒绝。

## 5. 消费门、失败和诊断

- 会话公开 Not Compiled、Outdated、Compiling、Compile Succeeded、Compile Failed 五种状态，以及 source/compiled revision。
- Play、显式 Compile、MIDI Export 和 Audio Render 在后台等待当前 revision 的编译尝试完成；等待在异步路径发生，不阻塞 WPF Dispatcher。当前结果失败时，消费者按既有失败契约拒绝执行。
- 旧 diagnostics 在 Outdated/Compiling 状态不得冒充当前结论；UI 状态明确显示结果过期或正在编译。后台完成只刷新 diagnostics/状态，不改变焦点、Selection、Workspace、zoom 或 scroll。
- 后台 worker 的非取消异常作为当前 Compile Failed 发布，并唤醒等待者；不会静默吞掉，也不会回滚已经成功提交的用户编辑。

## 6. 边界与明确非目标

- Project edit lock 会先取消正在运行的后台编译或镜像同步，再取得实时 Project 访问门。持锁期间不启动新的后台镜像同步，也不允许编辑。
- 本轮不改变 Full/Incremental 的 canonical 语义、确定性、映射 ABI、持久化格式、音频缓存格式或可听结果。
- 本轮不实现 compiler 全局阶段的细粒度增量化；Overlap、资源分配和 canonical materialization 仍可全量执行，但已移出 UI 线程。后续若优化这些阶段，仍须通过 Full/Incremental 等价门。

## 7. 验证门

- 后台编辑返回时 source revision 已推进、状态为 Outdated/Compiling，且未等待完整编译。
- 连续编辑会合并并最终只发布最新 revision；取消的工作缓存不影响随后结果。
- `EnsureCurrentCompilationAsync` 在成功、canonical 失败、取消、Dispose 下均能终止，不死等。
- 最新后台增量结果与同一 Project 的独立 Full Compile 逐字段等价。
- worker 已进入编译后到达的编辑、Undo/Redo 不等待旧编译结束；取消中的镜像同步也不能留下可发布的部分状态。
- 默认同步模式的现有 Application/Playback 测试继续通过；Desktop 构建和相关 UI/controller 测试通过。
