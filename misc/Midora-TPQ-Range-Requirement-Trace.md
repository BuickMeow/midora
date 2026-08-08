# Midora 初版 TPQ 范围 Requirement Trace

日期：2026-08-07
状态：已实施并通过 Core 全解自动验证
决定依据：Q-NUI-023、ADR-CORE-036
规格依据：《Midora SRS》4.1.2、14.2.2、16.7.2、19.1.2

## 输入与正式输出

- 输入：New Project 的 TPQ，以及 `.midora` v1 `settings/project-settings.json` 的 `ticksPerQuarterNote`。
- 正式输出：固定且不可编辑的 Project TPQ，供 Semantic Validation、Compilation、Playback、Preview、MIDI Export 与 Audio Render 共用。
- 合法范围：整数 `1..32767`，默认 192；SMF Type 1 直接使用同一原值作为 15-bit TPQ division。

## 边界与失败

- 1 与 32767 接受；0、负值、32768 与 `Int32.MaxValue` 拒绝。
- 新建输入在 SoundFont 验证、目标写入和 Project 激活之前失败。
- v1 settings 越界按严格 settings 损坏处理；不创建 v2，不兼容读取，不迁移或缩放已有 tick。
- MIDI Export 不重采样、不替换 TPQ；合法 Project 不会因 division 上限而永久失去 MIDI Export 能力。

## 归属与非目标

- TPQ 属于 Project 源数据并持久化；tick→秒/sample 缓存属于运行时。
- 不实现创建后修改 TPQ、全工程 tick 重映射、SMPTE division 或高 TPQ 开发格式迁移。

## 验证矩阵

- Domain 构造：1、32767、0、32768、`Int32.MaxValue`。
- New Project admission：越界在任何外部验证与文件写入前失败。
- JSON codec/schema：32767 round-trip，0/32768 严格拒绝。
- 既有 SMF writer 继续覆盖 1/32767 接受与 32768 拒绝。

验证结果：Compiler 219/219、Persistence 86/86、Application 217/217 在本增量完成时通过，0 failure / 0 skip；完整发布门随后按累计基线统一执行。
