# Segment Preview 损坏 Event Instrument 绑定需求追踪

状态：已实现  
日期：2026-08-06

## 1. 需求依据

- SRS 13.1.1～13.1.3：Segment Preview 使用专用 CompileContext，但只允许改变范围和选择，不得改变正式编译语义；失败结果不得播放。
- SRS 16.19.1、16.19.3：损坏 Event Instrument 占位不是正常定义；引用它的 Logical Track 不参与展开并产生 Error。
- SRS 12.19.2、12.20：Error 阻止结果消费，失败结果可以保留可定位诊断和 partial 信息。

## 2. 输入与正式输出

- 输入：用户预览一个正常加载的 Segment，其 Logical Track 仍绑定到打开时形成的 Damaged Event Instrument placeholder。
- 正式输出：临时 Segment Preview Project shell 同时保留 Track 的绑定 ID和对应损坏占位身份；Canonical Compiler 产生 `MIDORA1305` Error，结果不可消费。
- 正常 Event Instrument、未绑定 Track 和普通断裂引用的既有预览语义不变。

## 3. 边界、失败与诊断

- 只复制与被预览 Track 绑定 ID 相同的损坏占位，不把未选择对象引入 Preview 诊断作用域。
- 不复制或解析损坏 `.pb` 内容；placeholder 只保留身份与错误分类。
- 诊断保留 Track ID 与 Event Instrument ID；不得降级为普通断裂引用 `MIDORA1303` Info。
- Playback Controller 已拒绝不可消费的预览结果，因此不会进入 Backend Preparing/Playing 的正式音频消费阶段。

## 4. 持久化与运行时归属

- 占位的创建、删除、Undo 与保存禁止仍归 `.midora` 打开会话及 Project History。
- 本次只修正临时 Preview CompileContext；不修改 Project、不分配源 Project ID、不改变持久化格式。

## 5. 自动验证

- 构造正常 Segment + 损坏 Instrument 占位绑定，断言 Preview 不可消费、`IsPartial=true`、精确 `MIDORA1305` 来源且不存在 `MIDORA1303`。
- 既有 Preview 测试继续覆盖临时上下文、Cursor Tempo、SubVoice 过滤与只选 Segment 范围。
