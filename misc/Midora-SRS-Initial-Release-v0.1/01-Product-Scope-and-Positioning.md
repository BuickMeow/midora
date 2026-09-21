# 第 1 章 产品范围、定位与总体目标

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章定义 Midora 的产品身份、核心问题、目标用户工作方式、平台和协议边界，以及初版的总体范围。本文中的“初版”始终指本 SRS 所约束的 **Initial Release Scope**，而不是文档草稿版本。

## 1.1 项目基本信息
### 1.1.1 软件名称
软件正式名称：
```text
Midora
```
项目文件后缀名：
```text
.midora
```
名称说明：
* `Midora` 带有 `Mid` 前缀，与 MIDI 相关。
* `-ra` 后缀使名称更像一个独立产品名。
* 虽然 Midora 可能像人名，但在本项目中它是软件名。

### 1.1.2 发布定位

Midora 初版的产品定位固定为：

```text
免费软件
开放源代码软件
非商业软件
```

该定位描述 Midora 自身，不把 BASS、BASSMIDI、BASSWASAPI、用户提供的 SoundFont 或其他第三方材料重新许可为 Midora 的开源组成部分。Midora 自有源代码固定采用 OSI 批准的 MIT License，不增加“禁止商业使用”等自定义限制；根目录 `LICENSE` 使用标准 MIT 全文，版权署名固定为 `Copyright (c) 2026 Midora contributors`。

免费、开源、非商业定位也不自动构成对第三方许可证条件的法律认定。发布包含或依赖第三方二进制的正式产物时，仍须执行第 21.6 节的逐项发布门。
---
## 1.2 软件定位
### 1.2.1 Midora 是什么
Midora 是一个 Windows 桌面端 MIDI 1.0 事件乐器与 Pure MIDI Track 编曲、编译、播放、SMF 导入与导出工具。
它的核心目标是：
> 允许用户通过纯 MIDI 事件设计“事件乐器”，在更高层的 Logical Track 中复用这些事件乐器，也允许在 MIDI Channel Root 下直接编辑 Pure MIDI Track；软件把两条主线统一编译为标准 MIDI 1.0 数据并支持打开 SMF Format 0 / 1。
Midora 的核心不是传统意义上的音色设计，而是：
```text
纯 MIDI 事件
+ 时间维度
+ 可复用模板
+ 逻辑轨道调用
+ 自动 Port / Channel 分配
+ 自动事件展开
+ 自动 Reset
+ 预渲染式播放
+ 标准 MIDI 导出
+ 标准 MIDI Format 0 / 1 导入
+ Pure MIDI Track 直接编曲
```

在满足音乐语义、确定性、文件兼容和资源安全边界的前提下，Midora 的实现优先追求时间性能。时间性能与空间占用冲突时，允许使用更多但有明确上限的内存换取更快的编译、播放准备和音频渲染；不得以性能为由改变正式结果或省略错误检查。
### 1.2.2 Midora 不是什么
Midora 不是：
* DAW
* 以任意设备协议、音频轨、插件或完整制谱能力为目标的通用 DAW / 全功能 MIDI 工作站
* MIDI 2.0 工具
* GM 编曲工具
* 鼓机
* 传统音色库编辑器
* 传统合成器 preset 编辑器
* VST 宿主
* 面向任意第三方播放器完全一致复现的 MIDI 播放标准化工具
Midora 的目标不是让 MIDI 1.0 变成现代协议，而是在 MIDI 1.0 的限制内，通过编译器和抽象层，提供一种可控、可复用、可预期的事件乐器工作流。
---
## 1.3 核心理念
### 1.3.1 背景问题
在普通 MIDI 编辑器中，如果用户想用纯 MIDI 事件设计类似“音色”的结构，需要手动复制大量事件。
例如一个 Sine Kick 可能包含：
* Program Change 切换到某个 Sine 类音色
* Pitch Bend Range 设置
* Pitch Bend 曲线
* Expression CC 曲线
* Modulation CC 曲线
* Resonance CC 曲线
* Note On / Note Off
* 多个 CC / RPN / NRPN 状态设置
* 一定长度的尾巴和 Reset
如果这个事件结构持续 192 ticks，那么每出现一次 Kick，就要复制这一坨事件。
这种做法会带来大量问题：
* 复制粘贴繁琐。
* 容易漏事件。
* 容易把事件复制到错误 Channel。
* 多次触发时，Pitch Bend / CC 会互相污染。
* 快速重复触发时，事件尾巴可能与下一次事件开头交错。
* 多 Channel 事件结构难以手动管理。
* Channel / Port 资源难以人工分配。
* 想在逻辑层复用和修改这些事件结构非常困难。
Midora 的目标就是把这种工作从“手动复制 MIDI 事件”提升为：
```text
设计事件乐器
→ 保存到项目级事件乐器库
→ 在逻辑轨道中调用
→ 编译为标准 MIDI 1.0 数据
```
### 1.3.2 核心抽象
Midora 同时提供高层 Event Instrument 抽象与受 Root/Segment 边界约束的直接 MIDI 编曲主线：
```text
事件乐器 / Event Instrument
逻辑轨道 / Logical Track
MIDI 通道根 / MIDI Channel Root
纯 MIDI 轨道 / Pure MIDI Track
逻辑片段 / Logical Segment
MIDI 片段 / Midi Segment
逻辑参数 / Logical Parameter
逻辑参数映射 / Logical Parameter Mapping
事件乐器实例 / Event Instrument Instance
编译器 / Compiler
```
Logical 主线的主要操作：
```text
创建事件乐器
在事件乐器中设计 MIDI 事件
在逻辑轨道中画音符或触发器
在 Segment 中编辑 Logical Parameter Lane
软件编译为真实 MIDI 数据
```
用户不需要手动思考：
* 当前事件应该用哪个 MIDI Channel
* 当前事件应该用哪个 MIDI Port
* 是否需要新建 synth 实例
* Pitch Bend 是否污染其他音符
* CC 是否与其他实例冲突
* 事件尾巴何时被裁剪
* Reset 应该在哪里插入
这些由 Midora 编译器负责。

Pure MIDI 主线中，用户直接编辑 Note 与完整 MIDI 1.0 Channel Voice Event；Root 明确承担共享 Port.Channel、Melodic/Percussion、Channel 状态与硬边界。该主线不经过 Event Instrument、Mapping 或 Logical Parameter，但仍必须经过统一 semantic validation、canonical compilation、播放/渲染和导出管线。
此外，Event Instrument 可以向 Logical Track / Segment 暴露一组 Logical Parameters。用户在 Segment 中编辑这些参数的时间变化，用于完成 Mod、Expression、Pitch Bend、滤波控制等编曲层动态控制。Logical Parameter 不是裸 MIDI CC / Pitch Bend / RPN / NRPN 编辑，而是 Event Instrument 暴露的有限外部控制接口。
---
## 1.4 平台与技术边界
### 1.4.1 目标平台
Midora 面向 macOS 与 Windows 桌面平台，UI 使用 Avalonia。初版发布 macOS（Apple Silicon，`osx-arm64`）；Windows（`win-x64`）是计划中的后续平台，同样使用 Avalonia，不恢复已弃用的 WPF 应用。
技术栈：
```text
.NET 10
Avalonia
CPU / 发布架构：osx-arm64（初版）；win-x64（后续）
```
每个发布平台的应用、内部音频子进程以及随产品分发的原生库必须与目标平台同架构，不提供 AnyCPU 包，也不在运行时跨架构回退。Windows 平台沿用 x64 的 BASS / BASSMIDI / BASSWASAPI 基线；macOS 平台不使用 BASSWASAPI，改用 BASS 原生 CoreAudio 输出并保持等价语义（见第 13 章的平台后端作用域）。

暂不考虑：
* Linux
* Web
* 移动端
* WPF 应用（已弃用）

术语解释：本 SRS 其余章节中出现的 `WPF`、Windows 控件/API/系统术语（如 WPF Controls、WPF ClickCount、WPF UI thread、Windows 工作区、InputMethod、Windows 音量、Windows 注销/关机）按以下规则解释：实现平台使用 Avalonia 时，取该平台的等价能力；仅在 Windows 平台成立的条目保持 Windows 作用域；具体映射与差异记录在实现 ADR 中。
### 1.4.2 MIDI 标准
Midora 仅支持：
```text
MIDI 1.0
```
明确不支持：
```text
MIDI 2.0
```
因此所有设计必须遵守 MIDI 1.0 的限制：
* 每个 MIDI Port 最多 16 个 Channel。
* Pitch Bend 是 Channel-Wide。
* Program Change 是 Channel-Wide。
* CC 通常是 Channel-Wide。
* RPN / NRPN 是 Channel-Wide。
* 单个 Channel 上的状态会影响该 Channel 上所有 Note。
* 不使用 MIDI 2.0 的 per-note controllers。
* 不使用 MIDI 2.0 profiles / property exchange 等机制。
---
## 1.5 初版明确不做的内容
初版不做：
* MIDI 2.0
* DAW 插件
* VSTi 托管
* FluidSynth 后端
* XSynth 后端
* 传统 MIDI OUT 实时发送
* 自由 SysEx / 任意 Meta payload 创建与字节编辑；SMF 导入的 opaque SysEx / Meta 只读保留和重新导出不属于该排除项
* 每 Port 独立 SF2
* 程序级全局事件乐器库
* 反向同步 MIDI 到 Midora 抽象层
* Voice Steal
* Logical Track / Event Instrument 分配路径的 Channel 10 drum mode；Pure MIDI Root 的显式 Percussion 模式按第 23 章支持
* GM 假设
* 冻结事件乐器实例
* 传统逻辑轨道 Automation 曲线
* 完整 DAW 混音功能
---
## 1.6 总体方向总结
Midora 的最终大方向可以概括为：
```text
Midora 是一个面向 MIDI 1.0 的事件乐器编译环境。
```
它通过：
```text
项目级事件乐器库
逻辑轨道
MIDI Channel Root 与 Pure MIDI Track
Logical Segment 与 Midi Segment
事件乐器实例
SubVoice
音符实例隔离
生命周期策略
重叠策略
Reset 策略
Port / Channel 自动分配
预渲染播放
多模式 MIDI 导出
SMF Format 0 / 1 导入
```
让用户能够用纯 MIDI 事件设计可复用的“事件乐器”，并在逻辑层进行编曲。
Midora 不试图替代 DAW，也不试图成为通用 MIDI 播放器。
它的核心价值是：
> 把难以维护、难以复用、容易冲突的 MIDI 事件堆，提升为可设计、可复用、可编译、可预渲染、可导出的事件乐器系统。
---
## 1.7 一句话定义
> Midora 是一个面向 MIDI 1.0 的 Windows 桌面编曲与编译工具：它既允许在 Logical Track 中调用 Event Instrument，也允许在 MIDI Channel Root 下直接编写 Pure MIDI Track，并统一完成确定性 Port/Channel 分配、生命周期、Reset、SMF 导入导出和预渲染播放。
