# Midora Recent Projects 非 UI Requirement Trace

状态：已按推荐方案实现并进入回归验证；Q-NUI-018 待确认。  
日期：2026-08-06

上位规范：SRS 第 19.2.1、20.14 节及 INV-005、INV-024；SRS 只规定 File 菜单存在 Recent Projects，没有规定列表策略。

## 1. 输入与正式输出

- 输入：成功激活为当前 Project 的持久化绝对路径、现有当前用户本机 MRU、显式移除/清空请求及当前文件可用状态。
- 正式输出：最多 10 项、最新优先、Windows 路径大小写不敏感唯一的绝对路径列表；每次读取时附带当前 `File.Exists` 可用投影。
- 候选读取成功本身不记录；必须等待 New/Open/首次 Save 后的持久化 Project 已经成功提交为当前打开 Project，再由应用 composition 显式调用记录入口。

## 2. 排序、身份与边界

- 最新成功激活路径移动到首项；重复激活当前首项为 no-op，不产生无意义写入。
- 路径先要求 fully-qualified 并经 `Path.GetFullPath` 词法规范化，再用 `OrdinalIgnoreCase` 去重；不解析 symlink、不改写磁盘实际大小写。
- 超过 10 项时稳定移除最旧尾项。
- 离线、临时断开的移动磁盘/网络位置保留；只投影当前不可用，不自动删除。用户可显式移除单项或清空全部。

## 3. 本机表示与失败条件

- 默认文件：`%LOCALAPPDATA%\Midora\recent-projects-v1.json`，与严格的 `preferences-v1.json` 分离。
- source-generated UTF-8 JSON v1，固定属性顺序；未知属性、重复属性、重复路径、相对路径、超过 10 项、未知 schema、损坏 JSON 与大于 1 MiB 均拒绝。
- 读取失败返回空列表和 `RecentProjectsReadFailed` 非 Project notice；文件不存在返回无 notice 空列表。
- 保存使用同目录唯一临时文件、flush-to-disk、move/replace；失败返回 `RecentProjectsWriteFailed`，保留内存和磁盘旧列表并尽最大努力清理临时文件。

## 4. 持久化与运行时归属

- Recent Projects 是当前 Windows 用户本机应用状态，不属于 `.midora`、Project Source Data、Application Preferences v1 或 canonical。
- 路径列表持久化；`IsCurrentlyAvailable`、notice、临时文件和更新结果只属于运行时。
- 更新不设置 Project Modified，不进入 Undo/Redo，不触发编译、缓存失效、播放或任何输出任务。

## 5. 明确非目标

- WPF File 菜单展示、图标、快捷键和失效项交互。
- 固定项目、分组、时间戳、缩略图、云同步、跨用户/跨机器 roaming、Project 内容预扫描或自动修复。
- 把 Save Copy、失败/取消候选或未提交的新 Project 计入 MRU。

## 6. 自动化验证门

- 10 项 MRU 顺序、稳定淘汰、Windows 大小写去重与首项 no-op。
- 确定性往返、未知/重复字段、未知 schema、null、损坏和 1 MiB 上限。
- 相对路径、重复路径和超过容量在发布前拒绝。
- 存在/离线路径投影变化不删除持久条目。
- 目标锁定导致替换失败时，磁盘/内存旧列表和临时文件清理保持正确。
- 显式移除、清空和无操作分支。
