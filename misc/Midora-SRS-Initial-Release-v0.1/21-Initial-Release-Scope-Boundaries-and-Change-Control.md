# 第 21 章 初版范围边界、实现自由度与变更控制

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章汇总产品级范围，区分正式需求与实现设计自由度，并规定实现验证导致需求变化时的处理方式。

## 21.1 初版正式包含的能力
初版必须形成以下完整闭环：
```text
创建和打开 Project
设计 Event Instrument 与 SubVoice MIDI 事件
在 Logical Track / Segment 中编写 Logical Notes 与 Logical Parameters
执行确定性编译与资源分配
使用 Project 级 SF2 进行播放和预览
导出标准 MIDI 1.0 SMF Type 1
渲染普通 RIFF/WAVE、Stereo、IEEE 32-bit Float，并允许选择 8,000–192,000 Hz 整数采样率
保存、Save Copy 并重新打开 .midora Project
通过统一 UI、诊断和任务状态完成上述工作
```
## 21.2 产品级明确排除项
初版不支持：
```text
macOS、Linux、Web 或移动端
MIDI 2.0
DAW、音频录制、音频轨、VST/VSTi 宿主
MPE 或 MIDI 2.0 per-note controllers
自由 SysEx 编辑
程序级全局 Event Instrument Library
跨 Project Event Instrument 导入、导出或实时引用
多 Project 同时打开
多个可独立启动、显示 UI 或打开 Project 的 Midora 应用实例；不包括由唯一主实例管理的内部音频后端子进程
传统 Save As、自动保存或崩溃恢复
Pause、Scrubbing、Recording、Count-in、Legato、Tempo Ramp
Voice Steal
多 SF2、每 Port/Track/Instrument 独立 SF2
SFZ、DLS、Kontakt、LV2 等声音资源
纯键盘完整工作流、屏幕阅读器和 Access Keys
多主题、高对比度主题、额外交互音效
非 100% DPI 的专项验收承诺
```
各专项章节列出的更细禁止项同样属于正式范围，不因未在本章重复而失效。
## 21.3 实现设计自由度
本规格有意不固定以下实现选择：
```text
最终 C# namespace、class、record、struct 与字段布局
稳定 ID 的内存承载类型和内部转换实现，但必须服从 16.5.3 的分配语义和 16.13.2 的持久化布局
集合、索引和缓存的具体数据结构
Channel Group 分配的具体扫描算法
曲线离散化的内部优化算法，但结果必须与第 12.8.6 节逐整数 tick 参考语义完全一致
Tempo Map 的查找、索引和缓存结构，但 tick→sample 结果必须与第 4.1.4 节 decimal 积分和单次 Away From Zero 取整语义完全一致
Canonical Compiled Result 的最终内存布局
BASSMIDI / BASSWASAPI 的具体封装与安全调用顺序和音频线程模型，但 WASAPI 模式、格式与 period 请求必须符合第 13.14.7 节，正式实时工作 block 与 ring 容量必须符合第 13.19.2、13.19.9 节
内部音频子进程的类型拆分、轮询细节和共享内存字段打包，但不得改变第 13.30 节固定的完整子进程所有权、Native AOT、二进制 ABI 与零分配约束
Limiter 的循环展开、SIMD 和状态存储实现，但算法与参数必须符合第 13.17.6 节版本 1 语义
Roslyn 编译、缓存和 AssemblyLoadContext 方案
JSON Schema、protobuf .proto 的其他最终字段名、字段号与代码生成方式；16.5.3 已固定的 `nextStableId` 和 16.13.2 已固定的稳定 ID 表示除外
具体 WPF 控件、Visual Tree、MVVM 类型和 Timeline 虚拟化实现
错误码编号和自动化测试框架
```
这些内容可以通过技术原型、ADR、性能基准和实现测试确定，但不得改变本规格已经明确的用户语义、数据所有权、失败原子性、输出一致性和兼容边界。

实现选择同时遵守以下性能优先级：
```text
正确性、确定性、失败原子性和资源上限是硬约束
满足硬约束的候选中优先时间性能
时间与空间冲突时允许使用更多但有明确上限的内存换取速度
音频活动线程在 Playing / Buffering / Preview Playing / Rendering 中不得产生托管堆分配
约 200 ms 端到端实时延迟是性能测试基准，不是播放成败逻辑
```
## 21.4 范围变更规则
实现中发现技术不可行、成本异常或第三方能力不符合预期时，不得静默弱化需求。必须：
1. 记录验证事实和失败条件；
2. 明确受影响的 SRS 条款；
3. 提出替代方案和用户可见影响；
4. 修订 SRS 后再将变化视为正式范围；
5. 保持历史版本可追踪。
## 21.5 初版完成判定
产品达到初版范围至少要求：
```text
所有强制 Project 对象和编辑流程可用
核心对象可保存并无损重新打开
全量编译在相同输入下确定一致
播放、预览、MIDI 导出、音频渲染共享同一编译语义
资源不足、断裂引用、无效值、损坏资源和文件事务失败均有明确诊断
正式输出不受临时 Mute/Solo 或 UI 会话状态污染
长任务支持规定的锁定、取消和结果状态
启用的音频输出设备可以完整列出和选择，实时采样率跟随设备实际值
音频 buffer 设置可调且实际值可观察
音频性能验收覆盖线程零分配、underrun、IPC 延迟和约 200 ms 端到端基准
所有“初版不支持”能力不会以不完整正式功能暴露
```
