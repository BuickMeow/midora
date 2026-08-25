# Pure MIDI Channel Mode SysEx 音频特权 Requirement Trace

## 输入

- Pure MIDI Segment 中保存的 opaque F0 SysEx；
- SMF source MTrk、effective Port 与 payload 指定的 target Channel；
- MIDI Channel Root membership、Channel Mode、活动连通区间和 Unit allocation；
- Compilation range、Track 级 Mute/Solo monitoring demand；
- canonical 同 tick order、SMF Track order 与 event order。

## 正式输出

- 原 opaque source 与 canonical SMF projection 保持不变，可原样 MIDI 导出；
- 仅对合法 Roland GS DT1 Part Mode / Yamaha XG Part Mode 派生有类型的 canonical audio event；
- range 从活动 Root 区间中途开始时，在起点恢复最近一条先前 Channel Mode；
- Full Compile 结果派生默认 Playback View 时，必须原样保留全部有类型 Channel Mode SysEx；不得因复用完整项目 canonical 而只复制普通 MIDI event/page source；
- 非分页/分页 render plan、滚动 IPC 和 Worker 保留 payload、来源及顺序；
- Worker 先发送完整规范化厂商 SysEx，再在同一事件顺序点显式同步 BASSMIDI Unit 的 Melodic/Percussion mode，不能依赖 SoundFont 相关的隐式 preset remap；
- BASSMIDI 1-channel Unit 以 channel 0 规范化完整 F0…F7 原始消息接收事件。
- 引入该可听语义、以及修复默认 Playback View 遗漏该事件时，均必须提升 canonical Unit PCM、sample-domain Unit PCM、当前实时路径使用的 Segment PCM 与 playback-span renderer cache generation；旧版忽略或丢失 SysEx 生成的 PCM 不得继续命中。

## 边界与失败条件

- GS 只接受 `41 10..1F 42 12 40 10..1F 15 00..02 checksum F7` 且 checksum 正确；
- XG 只接受 `43 10..1F 4C 08 00..0F 07 00..02 F7`；
- payload target Channel 决定多 Channel MTrk 导入归属；普通 opaque first-owner 规则不适用；
- Root 空闲边界禁止状态继承；range restore 不补发 NoteOn；
- 任意其他 SysEx/Meta、Reset、F7 continuation、坏 checksum、未知 mode 继续保留/导出，但音频静默忽略且不产生诊断；
- IPC 非法枚举、设备编号、mode、reserved bytes 或 carrier Channel 不一致必须显式拒绝。

## 持久化与运行时归属

- `.midora` 继续只保存既有 opaque source；不增加新的 Project/persistence 字段；
- typed canonical event、render plan、IPC record、BASS raw bytes 与 sample-domain cache identity 都是派生/运行时数据；
- privileged SysEx 首次成为可听输入属于 renderer 语义变化，不依赖 source fingerprint 自然失效旧 PCM；
- Full/Incremental Compile 对同一输入必须产生完全相同的 privileged event 序列。

## 明确非目标

- 任意 SysEx 音频回放；
- GM/GS/XG Reset 或其他厂商参数；
- F7 continuation 拼接；
- 外部 MIDI hardware out；
- 在 Event Instrument/SubVoice 中新增自由 SysEx 编辑能力。
