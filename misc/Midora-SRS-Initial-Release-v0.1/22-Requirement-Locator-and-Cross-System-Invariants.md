# 第 22 章 主题索引与跨系统不变量

> 文档：**Midora Software Requirements Specification — Initial Release Scope**  
> 规格版本：**v0.1**  
> 适用产品范围：**Midora 初版**

本章提供快速定位表与跨章节不变量，便于开发、讨论、测试和评审时稳定引用。

## 22.1 跨系统不变量
| 编号 | 不变量 |
|---|---|
| INV-001 | Project 是编译、播放、预览、MIDI 导出、音频渲染与持久化的完整上下文。 |
| INV-002 | Conductor Track 固定存在且不参与 Channel Unit 分配。 |
| INV-003 | Event Instrument 与 Logical Track 通过稳定 ID 引用；名称不构成身份。 |
| INV-004 | Logical Track 不直接代表 MIDI Track、Port 或 Channel。 |
| INV-005 | SubVoice 在单个 Event Instrument Instance 中原则上需要独立 Channel Unit。 |
| INV-006 | 初版最多 16 Ports × 16 Channels = 256 Channel Units。 |
| INV-007 | 所有 Port 的 Channel 10 均按 melodic Channel 使用。 |
| INV-008 | SoundFont 不改变编译和 MIDI 导出语义，只影响实际发声。 |
| INV-009 | Canonical Compiled Result 是所有正式输出消费者的唯一音乐语义来源。 |
| INV-010 | 增量编译结果必须等价于同一上下文的确定性全量编译。 |
| INV-011 | Mute/Solo 是运行期监听状态，不属于 Project，也不影响成品输出。 |
| INV-012 | `.midora` 保存 Project 源数据，不保存编译结果、播放缓存、输出产物或 Undo/Redo 历史。 |
| INV-013 | 保存从当前内存 Project 重建完整 package，不保留未知或孤立文件。 |
| INV-014 | UI 只呈现和操作系统语义，不得重新解释编译、生命周期、资源或输出规则。 |
| INV-015 | 同一输入、上下文与有效资源状态必须产生确定一致的正式结果。 |
| INV-016 | 初版不支持 Reverb / Chorus；正式 BASSMIDI Stream 启用 `BASS_MIDI_NOFX`，Project、编译结果和 MIDI 导出不得包含 CC91 / CC93。 |
| INV-017 | 实时播放按所选输出设备的实际采样率生成音频；文件渲染按本次选择的 8,000–192,000 Hz 整数采样率生成音频。 |
| INV-018 | 音频活动线程在 Playing、Buffering、Preview Playing 和文件 Rendering 阶段不得产生托管堆分配；Preparing / Finalizing 不受此限制。 |
| INV-019 | 初版只允许一个用户可启动并打开 Project 的应用实例；正式音频后端固定为由该实例管理的无 UI、不能独立打开或解释 Project 的 Native AOT 内部音频子进程。 |
| INV-020 | 满足正确性、确定性和资源上限的候选实现中，时间性能优先于最小空间占用。 |
| INV-021 | 实时播放、预览和音频渲染的 tick→sample 映射使用完整 Tempo Map 的 decimal 区间积分，乘采样率后只执行一次 `AwayFromZero`，不得逐 Tempo 段取整。 |
| INV-022 | 初版 Limiter 版本 1 固定为 stereo-linked、sample-peak、瞬时 attack、zero-look-ahead、线性 ceiling 1.0、50 ms 单极指数 release；实时与离线使用同一算法。 |
| INV-023 | 初版正式 WASAPI 输出固定为 Shared Mode、event-driven、stereo interleaved float32；采样率、实际 buffer 与 callback period 由端点初始化结果决定，不得静默回退到其他模式或格式。 |
| INV-024 | 初版正式实时音频工作 block 固定为最多 256 frames；子进程内 Render-Ahead PCM ring 容量按实际采样率和用户毫秒设置向上取整为 frame，不固定 ring block 数；实时 PCM 不跨进程，运行时控制 IPC 使用固定版本二进制共享内存且热路径零分配。 |
## 22.2 常用主题定位
| 需要查找的主题 | 主要章节 |
|---|---|
| 软件定位、技术边界 | 第 1 章 |
| 术语、编号、身份、确定性 | 第 2 章 |
| Project、保存入口、修改状态 | 第 3 章 |
| tick、TPQ、Tempo、拍号、Marker | 第 4 章 |
| Port、Channel Unit、资源不足 | 第 5 章 |
| SF2、无 SF2、资源引用 | 第 6 章 |
| Event Instrument 库和定义 | 第 7 章 |
| SubVoice、Note/CC/RPN 等事件 | 第 8 章 |
| Logical Parameter、映射和 C# 函数 | 第 9 章 |
| Release、Loop、Envelope、Overlap | 第 10 章 |
| Track、Segment、裁剪与 Logical Note | 第 11 章 |
| CompileContext、资源分配、Compiled Result | 第 12 章 |
| 播放、预览、BASSMIDI、输出设备、采样率、buffer、Limiter | 第 13 章 |
| MIDI 文件结构与导出 | 第 14 章 |
| 普通 RIFF/WAVE、自定义采样率与离线渲染 | 第 15 章 |
| `.midora` package、schema、损坏与事务 | 第 16 章 |
| 主窗口、导航和全局面板 | 第 17 章 |
| 各编辑器工作区 | 第 18 章 |
| New/Open/Save/Export/Render 工作流 | 第 19 章 |
| 选择、拖放、验证、快捷键和 UI 验收 | 第 20 章 |
| 初版排除项、实现自由度和变更控制 | 第 21 章 |
## 22.3 推荐引用方式
在讨论、设计记录、Issue 和代码评审中，应使用：
```text
《Midora SRS》第 12 章“编译系统与 Canonical Compiled Result”
《Midora SRS》§16.21“打开流程”
《Midora SRS》INV-009
```
章节号和小节标题共同构成引用。仅引用标题而不引用章节号时，应避免使用容易重复的泛化名称。
