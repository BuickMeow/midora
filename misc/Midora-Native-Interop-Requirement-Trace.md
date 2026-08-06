# Midora Native Interop win-x64 ABI Requirement Trace

状态：已实现并通过正式非 UI 发布门。
日期：2026-08-06

上位规范：SRS 第 3.18.1、13.15、15.16、21.3 节，INV-019、INV-025、INV-026、INV-028，以及固定 `bass-native-baseline.win-x64.json`。

## 1. 输入与正式输出

- 输入：固定 BASS 2.4.18.3、BASSMIDI 2.4.16.0、BASSWASAPI 2.4.4.1 的 win-x64 C ABI，以及仓库 `LibraryImport`/unsafe struct 声明。
- 正式输出：与固定 header ABI 一致的关键结构大小/字段偏移、primitive/pointer/function-pointer/handle 宽度、精确 DLL/entry point 与正式函数返回宽度。
- 本门验证声明的静态 ABI；真实固定 DLL 的版本/hash、初始化、错误码、stream/font/device 生命周期仍由既有正式集成测试验证。

## 2. 架构与调用约定边界

- 正式测试进程必须是 Windows x64；`IntPtr`、data pointer 与 function pointer 为 8 bytes，BASS `DWORD/HSTREAM/HSOUNDFONT` 等 handle 为 unsigned 32-bit，`QWORD` 为 unsigned 64-bit，BASS BOOL 返回为 signed 32-bit。
- `LibraryImport` 不添加 cdecl 等替代 convention；Windows x64 使用统一平台默认 ABI。该结论不外推到 x86/Arm64，初版正式产物也禁止这些 RID。
- callback 使用 `delegate* unmanaged`，其字段在正式结构内按 8-byte pointer 对齐。

## 3. 固定结构快照

- BASS：`BASS_DEVICEINFO`、`BASS_INFO`、`BASS_CHANNELINFO`、`BASS_FILEPROCS`、packed `WAVEFORMATEX`。
- BASSMIDI：`BASS_MIDI_FONT`、`FONTEX`、`FONTEX2`、`FONTINFO`、`MARK`、`EVENT`、`DEVICEINFO`。
- BASSWASAPI：`BASS_WASAPI_DEVICEINFO`、`BASS_WASAPI_INFO`。
- 每项同时断言总大小和影响 pointer/尾部 padding/pack 的关键字段 offset，防止只检查 managed field type 却遗漏对齐错误。

## 4. 入口点与失败边界

- 固定检查三项版本入口，以及正式运行链使用的 BASS Init/Free/Error/StreamFree、BASSMIDI Stream/Font、BASSWASAPI Notify/Device/Init/Free 入口。
- 精确断言 library name、vendor entry point 与 `uint` handle/version 或 `int` BOOL/error return type。
- 任何布局、entry point、return width、架构或 convention 快照变化都使自动门失败；升级必须随固定 native baseline 一并显式评审。

## 5. 持久化与明确非目标

- ABI 声明、native handle、callback pointer、error code 和 layout snapshot 都是实现/运行时边界，不进入 Project、`.midora`、canonical、Undo/Redo 或 Application Preferences。
- 不验证未被正式链调用的全部 vendor API 行为，不把测试变成 vendor SDK 的完整镜像；不支持 x86/Arm64/AnyCPU 正式运行。
- 不以成功加载 DLL 代替结构布局测试，也不以布局测试代替真实版本/hash和生命周期集成测试。

## 6. 自动化验证门

- Windows x64 primitive/pointer/function pointer width。
- 三库关键结构 `Marshal.SizeOf` 与 `Marshal.OffsetOf` golden。
- 关键 `LibraryImportAttribute` 的 library/entry point/return type 和无替代 calling-convention attribute。
- 固定 DLL 发布门继续执行真实版本/hash、初始化、渲染、文件 Worker 与 WASAPI 策略测试，且零 Skip。
- 2026-08-06 正式门结果：BASS 测试 119/119、全仓 829/829、零 Skip；六个 Release solution 0 warning / 0 error；固定 BASS baseline 与 win-x64 Native AOT Worker 发布均通过。证据目录为 `artifacts/non-ui-release-gate-ea01febeb87b4c3b919a3b29e06be16a/`。
