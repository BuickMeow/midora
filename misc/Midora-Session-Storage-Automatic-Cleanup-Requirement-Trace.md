# Midora 会话存储自动清理 Requirement Trace

状态：已确认并实施
日期：2026-08-25

## 输入与所有权

- Application Preferences 中规范化的本机 Audio Cache Root。
- 固定 `%LOCALAPPDATA%\Midora\SessionContent` Pure MIDI backing root。
- 当前版本 session 目录名、版本 manifest、独占活动锁。
- 旧开发版本裸 GUID backing 目录及严格 `mt_<positive id>.mpk` 内容形态。

## 输出与触发

- 主应用取得单实例所有权后，自动删除已确认不活动的遗留 session 目录。
- 新 Audio Cache / SessionContent session 激活前再次执行同一清理。
- 正常释放继续删除本 session；异常退出残留由下一次启动或 session 激活回收。

## 安全边界

- 只处理正式 root 的直接子目录；绝不删除 root。
- 当前格式必须 manifest 与目录名精确匹配，且活动锁可独占取得。
- reparse point、仍活动、未知、manifest 错误和内容结构不匹配项全部保留。
- 旧 SessionContent 兼容项只允许裸 32 hex GUID，且为空或仅含 `mt_<positive id>.mpk` 普通文件。
- 每个目录独立 best-effort 删除；I/O、权限和竞争失败不阻止启动、打开、导入、播放或新 session。

## 持久化与非目标

- manifest/lock/目录只属于本机运行时，不进入 Project、`.midora`、canonical、Undo/Redo 或 Modified。
- SessionContent 不是 Audio Cache，不受 reusable quota 管理。
- 不按目录年龄猜测，不递归清空未知内容，不跨 Project session 复用，不静默删除无法证明属于 Midora 的路径。

## 验证门

- Audio Cache：自动删除有效且不活动的 session；保留另一个活动 store、未知目录和无关文件。
- SessionContent：删除当前格式 stale 与严格旧 GUID 项；保留活动 lease、未知内容和无关文件。
- Project Dispose 删除自己的 backing 目录，且不影响 sibling/父目录文件。
- Application/Persistence/Desktop 构建通过；相关定向测试通过。
