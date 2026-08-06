# Midora 单应用实例与启动请求转发 Requirement Trace

状态：已实现并完成自动化验证

日期：2026-08-06

上位规范：《Midora SRS》§3.3、§3.18.1、§19.2～19.3、INV-019。

## 1. 输入与正式输出

- 输入：稳定的主应用 ID、第二次启动进程的完全限定工作目录、原始命令行参数和本次启动的取消/连接超时。
- 正式输出二选一：当前进程取得主应用实例 lease；或启动请求被已有实例完整接收并确认，当前进程据此退出。
- 转发只搬运启动意图，不打开、解析或持有 Project；已有实例后续仍必须通过 Project Switch Guard 和正式 Open 流程处理候选文件。
- 内部 Native AOT 音频 Worker 不调用本协调器，因此不被误认为第二个用户可启动应用实例。

## 2. ADR-APP-001：当前交互登录会话内的命名内核对象与本地管道

- 单实例所有权使用 `Local\\` 命名 Mutex 对象的存在期，而不取得线程关联的 Mutex ownership。这样 lease 可在任意托管线程安全释放，进程异常结束时内核 handle 自动消失。
- 对象名由版本、稳定应用 ID 和 Windows Session ID 的 SHA-256 派生；不把任意用户输入直接拼入内核对象名。单实例范围固定为当前 Windows 交互登录会话，因为另一会话的桌面不能可靠接收前台激活或文件打开请求。该实现选择登记为小决定 Q-NUI-014。
- 第二次启动通过 byte-mode Named Pipe 转发。Server 使用 `PipeOptions.CurrentUserOnly`，避免不同 Windows 用户向现有实例注入启动参数。
- 启动协议固定为内部二进制 v1：magic、版本、reserved、总 payload 长度、严格 UTF-8 工作目录、参数数量和逐参数长度。总 payload 上限 1 MiB、参数最多 256 个；负长度、截断、非法 UTF-8、未知版本、非零 reserved 和尾随字节全部拒绝。
- 主实例在发布成功前同步创建第一个 pipe server，再启动单一顺序监听循环。已验证请求进入容量 64 的有界单读队列后才返回 accepted；队列满时显式拒绝，不静默丢弃或无界分配。
- 第二次启动连接失败时先释放自身临时 Mutex handle，再重试一次所有权判定：原实例若已崩溃，可原子成为新主实例；原实例仍存在但 IPC 不可用时失败，不允许绕过互斥启动第二个独立应用实例。

## 3. 边界、失败与诊断

- 主应用 ID 必须非空且有界；工作目录必须完全限定；参数保留原 Unicode、大小写、顺序和空字符串，不做路径猜测或 normalization。
- 连接超时、取消、pipe I/O、协议拒绝和队列满通过结构化 forwarding failure/异常返回；不得在无法确认送达时谎报成功。
- 单个畸形或中途断开的客户端只终止该连接，监听器继续服务。监听器自身不可恢复故障进入可观察 `Completion` task 并终止队列。
- 主 lease 的异步释放先停止监听并完成请求队列，再释放命名 Mutex handle；在旧 listener 完全退出前不得允许新主实例取得所有权。
- 本协议是同一版本主应用的内部启动 ABI，不进入 `.midora`、Project、Undo/Redo、Modified、canonical fingerprint 或 Application Preferences。

## 4. 明确非目标

- 本层不决定转发请求是激活窗口、打开 Project 还是显示错误；这些属于后续 WPF composition 和既有 Project Switch Guard。
- 不提供多实例切换、跨 Windows Session 的 UI 转发、管理员/服务账户控制、命令排队持久化、远程网络 IPC 或音频 Worker 控制。
- 不把文件扩展名当作 Project 有效性判断；`.midora`/`.zip` 仍由 package magic、manifest 和 schema 正式校验。

## 5. 自动化验证门

- 同一应用 ID 并发竞争只有一个 Primary，其余全部收到 Forwarded 确认。
- Unicode、空参数、顺序和工作目录完整往返；多个请求按 listener 接收顺序进入队列。
- 取消/超时/畸形/截断客户端不转移所有权、不杀死 listener、不产生部分请求。
- queue full 显式拒绝；payload、参数数量、字符串长度和协议头边界全部覆盖。
- 主 lease 释放完成等待者、释放内核对象；释放后同一应用 ID 可重新取得 Primary。
