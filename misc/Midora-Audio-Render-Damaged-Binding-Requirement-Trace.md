# 音频渲染损坏 Event Instrument 绑定需求追踪

状态：已实现  
日期：2026-08-06

## 1. 需求依据

- SRS 2.3、3.15、12.1.1：正式消费者只消费 Canonical Compiled Result，不得绕过编译器重解释 Project 语义。
- SRS 16.19.1、16.19.3：损坏 Event Instrument 占位不作为正常定义；引用它的 Logical Track 不参与展开并产生 Error。
- SRS 15.2.2、15.13：按 Logical Track 渲染中，某条 Track 编译失败应记录失败并继续其他独立 Track。

## 2. 输入与正式输出

- 输入：音频渲染任务选择中包含绑定正常 Event Instrument、损坏 Event Instrument 占位或未绑定/普通断裂引用的 Logical Track。
- 正式输出：所有绑定正常对象或损坏占位的已选 Track 都进入各自规定的 Canonical CompileContext；损坏绑定由编译器产生 `MIDORA1305` Error，不生成可消费 canonical 输出。
- 普通未绑定或普通断裂引用仍不是音频文件输出目标，继续以 Info 报告，不升级为损坏错误。

## 3. 边界与失败条件

- Whole Mix：任一被选 Track 绑定损坏占位时，共享 canonical 编译失败，整曲输出不可消费。
- Per Logical Track：损坏绑定对应的独立编译项失败；其他成功 Track 仍可继续形成输出，符合多文件独立成功/失败规则。
- 仅有未绑定 Track：仍在 Preparing 前以“无有效目标”阻止任务。
- Mute/Solo 不参与上述判断。

## 4. 诊断、持久化与运行时归属

- 损坏绑定诊断由编译器产生，保留 Track ID 与 Event Instrument ID 来源；Audio Render 协调器不得降级或替换。
- 打开时形成的损坏占位、保存禁止及删除/Undo 规则仍归持久化与 Project History；本次不修改 `.midora` 格式。
- 音频文件路径冻结、SF2 快照和 Worker 生命周期不受影响。

## 5. 明确非目标

- 不尝试修复或猜测损坏 `.pb` 内容。
- 不把普通未绑定/断裂引用升级为 Error。
- 不允许失败或 partial canonical 进入音频 Worker。

## 6. 自动验证

- Whole Mix：健康 Track 与损坏绑定 Track 同选，断言整曲不可消费并保留 `MIDORA1305` 精确来源。
- Per Logical Track：断言损坏项失败、健康项仍成功。
- 既有无绑定 Track 与空 SubVoice 测试继续覆盖相邻边界。
