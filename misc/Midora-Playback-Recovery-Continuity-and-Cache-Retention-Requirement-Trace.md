# Midora 播放恢复连续性与缓存保留 Requirement Trace

状态：已实现
日期：2026-08-11

## 1. 需求依据

- SRS 13.19.4～13.19.11：Render-Ahead、Buffering 自然段恢复、五层 session 音频缓存、完整 tile 发布与局部失效。
- SRS 22：INV-042～INV-044，exact replay 完整命中不得重复语义编译或 BASSMIDI 合成；缓存不进入 `.midora`。
- `misc/Midora-Audio-Backend-Architecture-Decisions.md`：ADR-AUDIO-009。

## 2. Requirement trace

| 项目 | 约束 |
|---|---|
| 输入 | canonical realtime plan、冻结采样率、ring 中尚未消费的帧、恢复区间 `[F,R)`、Unit/playback-span 完整 cache key、Application Preferences 的 cache root/quota |
| 正式输出 | underrun 后连续且不丢帧地回放完整 `[F,R)`；未修改内容的已完成 Unit/Segment PCM 可在同一 Project-open session 内继续命中；完整 playback span 可直接重放 |
| 边界 | recovery replay 与下层异步 cache read 的交界；Project 编辑只使内容 key 改变的条目失配；cache root/quota 实际变化才替换 session store |
| 失败条件 | recovery storage 不可用、cache hit 损坏、已接受 hit 的 I/O 失败、renderer/ring fault；普通 reusable 写失败只禁用后续保留，不改变可听结果 |
| 诊断 | Worker fault 保留原始异常；回归测试固定“replay 前缀已产生而 tail 暂时 Buffering”的分支；cache retention warning 继续由 session snapshot 报告 |
| 持久化归属 | Project 源数据和 `.midora` 不保存 PCM、恢复 spool、playback span、cache key 或运行状态 |
| 运行时归属 | recovery spool、render-ahead ring、Unit PCM、playback span、cache store 和偏好重配置均属于当前 Project-open session/音频任务 |
| 非目标 | 不改变 canonical、tick→sample、自然小节恢复终点、Master/Limiter 顺序、SoundFont 语义或跨会话缓存策略 |

## 3. 已确认缺陷与修复

1. recovery replay 在同一次 pull 中先产生前缀、随后下层返回 `Buffering` 时，旧实现返回了 `Buffering(0)`。replay 游标已经前进，但前缀没有写入 ring，导致生产源永久领先；后续恢复到 EOS 时会在 `R` 前提前结束。
2. 修复后，已产生的 replay 前缀以 `Continue(frameCount)` 发布；只有零前缀时才传播 `Buffering(0)`。该分支不新增托管分配或文件 I/O。
3. 旧的 sample-domain generation reset 会 Dispose 并重建整个 session cache store，错误删除未变化 Segment/Unit 的 reusable entry。修复后只清除 sample-plan 派生索引，内容 key 负责局部失效。
4. 重复应用相同 cache root/quota 现在幂等；桌面服务重配置不再重复创建 cache store。
5. 指定项目中，第二轨 3456 个密集短 fragment 在编辑第一轨后键值全部保持不变，实际缓存目录也只新增第一轨变化条目和新的 playback span；因此 Buffering 不是未修改第二轨重新合成。旧 Unit cache bridge 每遇到一个短 hit fragment 才通知 I/O 线程切换 payload，并让 miss writer 的初始化/切换参与正式 readiness。前者把 3456 个命中退化为数千次线程握手，后者使任意新 miss 的首块必然先返回 `Buffering`。
6. 修复后，同一 canonical Unit 的 hit/miss fragment 各自形成连续虚拟 PCM 流。Preparing 预装首个 16,384-frame 读 hot-set，AboveNormal I/O 线程跨 fragment 主动预读并优先服务读取；miss 写入不再对正式渲染施加背压，无法无等待保留时只失效 capture。纯 cache hit fragment 也不再重复初始化不会被消费的 BASSMIDI stream。

## 4. 验证门

- recovery replay 前缀与下层 `Buffering` 同批发生时，输出帧数、顺序和下一次源位置必须连续。
- recovery/ring、Unit PCM staging、playback span staging 的既有测试全部通过。
- generation reset 后，既有 reusable payload 仍可读取。
- 相同 root/quota 再配置后，既有 reusable payload 仍可读取。
- 多个连续 cache-hit fragment 在只请求首 fragment readiness 后，后续 fragment 必须可直接顺序读取；512 个密集微型 fragment 必须在初始有界预读内无边界 stall。
- 连续 cache-miss fragment 的 writer ring 必须可跨 fragment 无初始化握手入队；writer 无法接受时不得让正式 renderer 返回 `Buffering`。
- 指定 `TestProject.midora` 的当前 Native AOT Worker 首次完整播放不得再以 recovery EOF fault 结束；同 session 重复播放不得弹出播放错误。
- 指定项目完成首次缓存后，仅编辑第一轨 Segment，再次播放不得因第二轨 3456 个未修改 fragment 的 cache I/O 边界进入可观察 Buffering。

## 5. 本轮验证证据

- 对指定项目只在内存中将第一轨唯一 Segment 的 `Project Start Tick` 从 0 改为 48；第二轨 3456 个 Unit PCM key 在编辑前后 `missing=0`、`added=0`、`changed=0`。
- computer-use 使用当前 Debug Desktop 与重新发布的 Native AOT Worker 实测：首次播放后当前 session 有 3461 个 `.mcac` 条目；上述单一编辑后再次播放直接进入 `Playing`，未观察到 `Buffering`；完成后条目为 3467，仅新增受第一轨编辑影响的缓存及新 playback span。测试过程未保存 Project，源 `.midora` 的长度和最后写入时间保持不变。
- `Midora.Audio.Bass.Tests` 的真实 BASS/SF2 集成子集 31/31 通过；四个完整 solution 门禁按测试项目去重后 1130/1130 通过；Desktop `win-x64` Debug 构建 0 warning、0 error。
