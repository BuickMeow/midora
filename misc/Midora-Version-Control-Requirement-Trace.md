# Midora 版本控制 Requirement Trace

状态：已实施 `1.0.0-dev` 基线；Format 1 自 2026-08-25 起冻结

上位规范：《Midora SRS》§16.3、§16.4.3、§16.24、§16.31、§21.4、§21.7、INV-088

## 输入与正式输出

- 输入：`eng/Version.props` 中的产品 SemVer、Git commit/tag、Format 1 schema/descriptor/wire assets、Mapping ABI 与其他独立协议版本。
- 正式输出：统一的 Assembly/File/Informational Version；Project manifest 与 MIDI Export Readme 的准确软件版本；单调 Project Format/schema/ABI 兼容判断；版本化发布 tag 和变更记录。

## 边界与失败条件

- 源码不得重新引入产品版本字符串；产品版本、AssemblyVersion、FileVersion 或 release tag 不一致时发布门失败。
- Format 1 的 JSON schema hash、protobuf descriptor/golden bytes、版本常量或旧文件重开失败时兼容门失败；不得通过更新旧基线掩盖未评审的格式变化。
- 新持久化能力不能由 Format 1 表示时必须建立新格式和显式迁移；旧软件读取未来格式不属于保证范围。
- 用户自由文本 `Project Version`、缓存 generation、Worker IPC、Mapping ABI 与 Project Format 分别管理，不能互相替代。

## 持久化与运行时归属

- `createdWithSoftwareVersion` / `lastSavedWithSoftwareVersion` 属于 manifest 诊断信息，不决定格式兼容。
- `fileFormatVersion`、component schema 与 Project source wire 属于 `.midora` 持久化契约。
- Product version、Application Preferences schema 与发布 tag 属于程序/发布契约。
- 缓存 generation 和 build commit metadata 属于派生/诊断身份，不进入 Project 语义。

## 明确非目标

- 不把所有版本压缩成同一个数字。
- 不支持旧软件前向读取未知新格式，不提供保存回旧格式。
- 本轮不创建 `v1.0.0` tag、不发布 RC/正式产物，也不改变现有 Project 数据结构。
