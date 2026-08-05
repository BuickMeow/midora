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
---
## 1.2 软件定位
### 1.2.1 Midora 是什么
Midora 是一个 Windows 桌面端 MIDI 1.0 事件乐器编曲、编译、播放与导出工具。
它的核心目标是：
> 允许用户通过纯 MIDI 事件设计“事件乐器”，并在更高层的逻辑轨道中复用这些事件乐器进行编曲。软件负责将这些高级逻辑编译为标准 MIDI 1.0 数据。
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
```

在满足音乐语义、确定性、文件兼容和资源安全边界的前提下，Midora 的实现优先追求时间性能。时间性能与空间占用冲突时，允许使用更多但有明确上限的内存换取更快的编译、播放准备和音频渲染；不得以性能为由改变正式结果或省略错误检查。
### 1.2.2 Midora 不是什么
Midora 不是：
* DAW
* 普通 MIDI 编辑器
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
Midora 不让用户直接在底层 MIDI Track + Channel 上完成全部工作，而是提供更高层抽象：
```text
事件乐器 / Event Instrument
逻辑轨道 / Logical Track
片段 / Segment
逻辑参数 / Logical Parameter
逻辑参数映射 / Logical Parameter Mapping
事件乐器实例 / Event Instrument Instance
编译器 / Compiler
```
用户主要操作：
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
此外，Event Instrument 可以向 Logical Track / Segment 暴露一组 Logical Parameters。用户在 Segment 中编辑这些参数的时间变化，用于完成 Mod、Expression、Pitch Bend、滤波控制等编曲层动态控制。Logical Parameter 不是裸 MIDI CC / Pitch Bend / RPN / NRPN 编辑，而是 Event Instrument 暴露的有限外部控制接口。
---
## 1.4 平台与技术边界
### 1.4.1 目标平台
Midora 只面向 Windows 桌面平台。
技术栈：
```text
.NET 10
WPF
Windows Desktop
CPU / 发布架构：x64（win-x64）
```
初版不发布 x86 或 Arm64 构建，不提供 AnyCPU 包，也不在运行时跨架构回退。主应用、内部音频子进程以及随产品分发的 BASS / BASSMIDI / BASSWASAPI 原生库必须全部为 x64。

暂不考虑：
* macOS
* Linux
* Web
* 移动端
* 跨平台 UI 框架
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
* 自由 SysEx 编辑
* 每 Port 独立 SF2
* 程序级全局事件乐器库
* 反向同步 MIDI 到 Midora 抽象层
* Voice Steal
* Channel 10 drum mode
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
Segment
事件乐器实例
SubVoice
音符实例隔离
生命周期策略
重叠策略
Reset 策略
Port / Channel 自动分配
预渲染播放
多模式 MIDI 导出
```
让用户能够用纯 MIDI 事件设计可复用的“事件乐器”，并在逻辑层进行编曲。
Midora 不试图替代 DAW，也不试图成为通用 MIDI 播放器。
它的核心价值是：
> 把难以维护、难以复用、容易冲突的 MIDI 事件堆，提升为可设计、可复用、可编译、可预渲染、可导出的事件乐器系统。
---
## 1.7 一句话定义
> Midora 是一个使用 MIDI 1.0 事件构建“事件乐器”的 Windows 桌面编曲与编译工具，它让用户在逻辑轨道中调用这些事件乐器进行创作，并由软件自动完成 Port / Channel 分配、事件展开、生命周期处理、Reset、预渲染播放和 MIDI 导出。
