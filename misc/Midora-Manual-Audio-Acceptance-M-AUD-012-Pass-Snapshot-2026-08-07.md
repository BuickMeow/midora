# Midora M-AUD-012 通过快照（2026-08-07）

状态：不可改写的原始结果快照
范围：Q-NUI-028 修复后的活动输出设备移除或禁用复测

> 本文件保存用户返回的原始人工结果和完整终端输出。后续文档整理不得覆盖或改写本快照。

## 人工结果

```text
M-AUD-012：通过；
```

## 完整终端输出

```text
dotnet run --project $ConsoleProject -c Release --no-restore -- logic-realtime-child $Sf2 tempo-loop
编译：consumable=True；events=108；instances=2；peak units=2
逻辑模型子进程实时播放：tempo-loop；actual=48000 Hz
播放结束：callback allocations=0 B；child allocations=0 B；IPC underruns=0；child fault=False
活动输出设备已不可用，音频 Worker 已断开输出并受控退出。请手动重新指定输出设备；Midora 不会自动切换设备。

$LASTEXITCODE
2
```

## 结论

- 活动设备丢失后没有未处理异常、Worker Fault 堆栈、卡死或自动设备切换。
- callback 和子进程渲染线程托管分配均为 0 B，IPC underrun 为 0，`child fault=False`。
- 控制台给出显式人工重选提示，并使用约定的 action-required 返回码 2。
- M-AUD-012 通过；M-AUD-001～012 人工音频验收全部关闭。
