# Midora Open Project 非 UI 候选工作流 Requirement Trace

状态：已实现并完成自动化验证

日期：2026-08-06

上位规范：《Midora SRS》§3.9.3～3.10、§6.6～6.8、§16.17～16.21、§19.2.2～19.3、INV-001、INV-008、INV-012、INV-013、INV-032。

## 1. 输入与正式输出

- 输入：完全限定的 `.midora`/`.zip` 候选路径、严格 v1 package reader、取消 token，以及可选的粗粒度应用进度观察器。
- 成功输出：不持有源文件句柄的 `ProjectOpenCandidate`；包含可信内存 Project、current path/file information、打开诊断、恢复导致的 Requires Save、Damaged Placeholder/保存能力、Embedded 运行时资源所有权和未执行后端验证的初始 SoundFont 状态。
- 候选完整构建前不清空、不关闭也不修改当前 Project；调用方只在通用 Project Switch Guard 的成功分支替换当前会话。
- 打开包本身不执行 Mapping/全 Project 编译，不建立 `ProjectCompilationSession`，不开始工程总耗时，不加载播放 stream，也不恢复 Workspace/Mute/Solo/播放状态。

## 2. 提交边界与会话工厂

- 候选提交为当前 Project 后，调用方才构造 `ProjectCompilationSession`；`CreateDocumentSession` 绑定 Persisted origin，并把 package recovery 的 Modified 状态转为独立 `RecoveredProjectSourceData` 外部 dirty reason。
- `CreatePersistenceCoordinator` 冻结 current path/file information，并把候选拥有的 Embedded 资源提供给普通 Save/Save Copy；损坏对象或不可用 Embedded 资源使保存入口为不可用。
- `CreateSoundFontRuntimeSession` 只构造验证状态机，不自动验证或加载；调用方提交候选后再以 current path/Embedded lease 执行异步 refresh。无引用初始为 No Reference；External 或完整 Embedded 初始为 Verification Required；损坏 Embedded 初始为 Embedded Resource Unavailable。
- 候选释放时只释放其拥有的 Embedded 提取目录；源 `.midora` 在 package read 返回前已关闭，External SF2 不由候选拥有。

## 3. 失败、诊断与恢复

- 相对路径、取消、非 Zip、缺失/非法 manifest、结构/hash/schema/版本不兼容和当前格式强制文件损坏均不返回候选，原 Project 保持 Stopped 且不变；异常继续保留 `MidoraPackageStageV1`、目标与包内路径。
- Metadata/普通 settings/Conductor 可恢复问题由 package reader回退、生成 Error 并设置 Requires Save；提交后的 Document 保持 Modified，直到普通 Save 成功清除 recovery dirty reason。
- 缺失 Metadata 的必填恢复时间戳按小决定 Q-NUI-016 使用打开 package 服务的 UTC 当前值同时初始化 created/modified；它是恢复默认值生成时刻而非已证实的原工程创建时间，且保证恢复后首次 Save 不会因跨时钟域倒序失败。
- 单对象读取失败形成保留 ID/名称/顺序/来源的 Damaged Placeholder；它本身不标记 Modified，但在删除前禁用 Save/Save Copy。
- 未知/孤立文件只生成 Information，不标记 Modified，不参与 Project 语义，下一次保存不保留。
- External SF2 缺失、歧义、不可读、hash 变化或后端不可加载不阻止源 Project 打开；异步 runtime refresh 产生明确状态并持续禁止发声消费者。

## 4. 明确非目标与暂停分支

- 不实现 Open Progress WPF、文件选择器、Global Notice、Workspace 导航、播放恢复、自动编译或自动 SF2 后端加载。
- Q-NUI-002 所需的旧格式来源样本、版本矩阵和成功迁移器仍是重大决定暂停分支；本工作流只透传当前已实现的版本预检/不兼容失败，不把默认回退伪装成迁移。
- 不监控打开后的原 `.midora` 文件变化，不实现 crash recovery、扫描重建、后台多 Project 或自动保存。

## 5. 自动化验证门

- `.midora`/`.zip` 成功候选、路径/file information、无长期文件占用、进度阶段和无恢复 Modified。
- 候选提交后才开始单调工程时间；Document/Persistence/SoundFont Runtime 工厂必须绑定同一 Project。
- Metadata 缺失回退、Error/Requires Save/外部 dirty reason、普通 Save 清除和严格重开。
- 未知 entry Information 且不 Modified；损坏对象 Placeholder 且保存不可用。
- Embedded 提取资源所有权、后端验证和释放；External 缺失不阻止打开且验证状态为 Missing。
- 相对路径、无效容器、预取消、跨 Project 工厂误用和已释放候选全部确定失败。
