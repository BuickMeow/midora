# Changelog

Midora 的重要用户可见变更记录在本文件中。版本采用 Semantic Versioning；Project 文件格式版本独立管理。

## [Unreleased]

### Added

- 建立 `1.0.0-dev` 产品版本基线、集中式构建版本源和正式发布检查门。
- 冻结 `.midora` Project Format 1 的 JSON schema、protobuf descriptor、代表性 wire bytes 与读取兼容性要求。

### Changed

- 本地 self-contained 发布包不再暴露内部 schema/release manifest，并随包携带 Fluent System Icons、Roslyn、Google.Protobuf 与精确 .NET runtime-pack 的许可材料。
- About 对话框改为读取集中式产品版本，欢迎页文案采用内嵌 Sora Light 字重并调整垂直位置。

正式 `1.0.0` 发布时，本节内容将整理到 `[1.0.0] - YYYY-MM-DD`，并由不可移动的 `v1.0.0` Git tag 冻结。
