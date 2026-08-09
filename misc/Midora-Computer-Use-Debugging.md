# Midora Computer Use 调试基线

状态：本机可用
记录日期：2026-08-09
范围：Codex Desktop 的 Windows Computer Use 对 Midora WPF Debug 构建的启动、观察和输入控制

本文不是 SRS，不定义任何 Project、Compiler、Canonical Result、播放、预览或持久化语义。

## 已确认的问题与处理

1. 旧可执行文件名 `Midora.Desktop.exe` 会在当前 Computer Use Windows 运行时中产生不一致的进程键：外层授权使用完整文件名，内部窗口助手则把第一个句点之前的尾段识别为 `desktop.exe`。即使配置了完整路径，两者仍无法匹配。
2. 生产 WPF 项目的程序集名固定为 `Midora`，因此调试入口为 `Midora.exe`；命名空间仍为 `Midora.Desktop`。这只改变桌面 apphost/程序集文件名，不改变代码命名空间或业务语义。
3. 当前 Computer Use 的 `launch_app` 子进程环境可能缺少 `windir`。WPF `PresentationCore` 的字体缓存会在应用 `OnStartup` 之前用该变量构造 Windows Fonts 的绝对 URI，变量缺失会触发 `UriFormatException`。`WindowsLaunchEnvironment` 以 module initializer 在 WPF 初始化前检查该变量，仅当它缺失或不是绝对路径时，才从 `Environment.SystemDirectory` 恢复 Windows 目录。
4. 本机开始菜单注册 `Midora Debug`，目标为 Debug 输出目录中的 `Midora.exe`。Codex 用户配置对 Debug 和 Release 的完整 `Midora.exe` 路径建立 Computer Use allowlist。若 app catalog 仍保留旧身份，应在没有 Midora 进程运行时使该任务对应的 `shell-apps.json` 失效，让运行时重新生成；旧文件应保留为可恢复备份，不直接删除。

## 标准调试入口

```text
D:\Programing\midora\src\midora-desktop\Midora.Desktop\bin\Debug\net10.0-windows\win-x64\Midora.exe
```

先构建：

```powershell
dotnet build src\midora-desktop\Midora.Desktop\Midora.Desktop.csproj -c Debug --no-restore
```

Computer Use 必须使用其技能规定的 `node_repl` + `@oai/sky` 接口。选择目标时使用 `list_apps()` 或 `list_windows()` 实际返回的完整窗口对象；不要根据标题、进程名或句柄自行拼装对象。每次输入前重新观察，执行一个动作后立即刷新状态。

## 2026-08-09 验收证据

- `sky.launch_app` 使用上述 Debug 完整路径后，返回的目标窗口进程路径为同一 `Midora.exe`，窗口标题为 `Midora`。
- `get_window_state(include_screenshot: true, include_text: true)` 成功返回主窗口截图和 80 行可访问性树。
- 已实际验证：菜单元素点击、`Escape`、文本输入、`Ctrl+A`、`Backspace`、状态刷新和 `Alt+F4` 自然退出。
- Debug 构建：0 warning / 0 error。
- Release 构建：0 warning / 0 error。
- `Midora.Desktop.Tests` Debug：18/18 通过。

## 边界

- 本基线证明 Computer Use 可用于 Midora Debug 构建的 GUI 复现、截图、可访问性检查及鼠标/键盘调试，不证明某个具体业务缺陷已经修复。
- Computer Use 可用性验收本身不能关闭业务缺陷；`WPF-AUDIO-HELD-001` 后续已通过独立根因对照、自动回归和重新发布 Worker 后的实际 SubVoice / Segment WPF 路径验证关闭，完整证据见 `Midora-WPF-Timeline-Editing-Playback-and-Render-Diagnostics-Requirement-Trace.md`。
- Debug 与 Release 的可执行文件基础名相同。当前开始菜单注册指向 Debug，因此从 app catalog 启动用于日常调试；若要检查已手动启动的 Release 实例，应按 `list_windows()` 返回的完整 Release 进程路径选择窗口。
