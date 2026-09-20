# Midora Avalonia 窗口移植指南（供并行移植任务使用）

状态：工作指南，配合 `misc/Midora-macOS-Port-and-Rendering-Stack-Requirement-Trace.md`
适用范围：把 `src/midora-desktop/Midora.Desktop/*.xaml(.cs)` 的 WPF 窗口移植到
`src/midora-avalonia/Midora.Avalonia/Windows/`（Avalonia 11.3.22 / .NET 10）。

## 硬约束

1. 只写分配给你的文件：`src/midora-avalonia/Midora.Avalonia/Windows/<Name>.axaml` 与 `<Name>.axaml.cs`。
   不要修改 `App.axaml`、`MainWindow.*`、`Themes/*`、`Controls/*`、`.csproj`、任何 `Midora.Desktop`/WPF 文件。
2. 不要新增 NuGet 包，不要引用任何 `Midora.Desktop*`、`System.Windows*`、WPF、AvalonEdit。
3. 不要构建（并行任务会争抢 obj/bin）。写完后自检语法与 API，构建由主任务统一执行。
4. 新窗口命名空间统一 `Midora.Avalonia.Windows`；`x:Class="Midora.Avalonia.Windows.<Name>"`。
5. 每个 Window 根节点加 `x:CompileBindings="False"`（避免编译绑定要求 `x:DataType`）。
6. 视觉尽量忠实 WPF 源：同结构、同尺寸、同间距、同颜色 token；行为可以简化。
7. 代码隐藏只保留能编译、能演示的最小逻辑：构造、OK/Cancel/Close、简单选中/输入回显。
   需要数据的地方用私有字段或简单 record 提供占位数据，不要访问 WPF ViewModel。

## 可用主题资源（App 级，均已存在）

- 刷子：`Brush.Black`、`Brush.Surface.0..4`、`Brush.Border`、`Brush.Border.Strong`、
  `Brush.Red`、`Brush.Red.Hover`、`Brush.Red.Pressed`、`Brush.Red.Dark`、`Brush.Red.Subtle`、
  `Brush.Red.Subtle.Hover`、`Brush.Text.Primary/Secondary/Tertiary/Disabled`、
  `Brush.Success(.Subtle)`、`Brush.Warning(.Subtle)`、`Brush.Info(.Subtle)`、`Brush.Damaged(.Subtle)` 等。
  一律用 `{DynamicResource Brush.X}`。
- 圆角/厚度：`Radius.Small/Medium/Large`、`Thickness.ControlPadding/CardPadding`（`{DynamicResource ...}`）。
- 字体：`Font.UI`（Sora）、`Font.Mono`（JetBrains Mono）、`FontSize.Caption/Body/Title`。
- 图标：`{StaticResource Fluent.Xxx}`（FluentIcons.axaml 中的全部 key）与
  `{StaticResource WindowControl.Close/Minimize/Maximize/Restore}`。
  图标控件：`<controls:FluentIcon Data="{StaticResource Fluent.Xxx}" .../>`
  （xmlns:controls="using:Midora.Avalonia.Controls"），类 `commandbar`（14px）、`stepper`（14px）、`captionicon`（10px）。
- 控件样式类（`Classes`）：
  - `Button`：默认；`primary`、`ghost`、`icon ghost`、`caption`、`caption close`、`commandbartext`。
  - `TextBlock`：`caption`、`muted`、`mono`、`section`、`overline`。
  - `Menu > MenuItem`：一级菜单已按 WPF 指标（29 高、9,0 内边距等）。

WPF `Style="{StaticResource Text.Caption}"` → Avalonia `Classes="caption"`；
`Text.Muted` → `muted`；`Text.Mono` → `mono`；`Text.Section` → `section`；
`Text.Overline` → `overline`；`Button.Primary` → `Classes="primary"`；`Button.Ghost` → `Classes="ghost"`；
`Button.Icon` → `Classes="icon ghost"`；`Button.Caption` → `Classes="caption"`；
`Button.Caption.Close` → `Classes="caption close"`；`Button.CommandBarText` → `Classes="commandbartext"`；
`Button.Base`/默认 Button → 不加类。
若 WPF 引用了本列表以外的 key（如 `PanelHeader`、`Badge.*`、`Text.Title`、`Button.TimelineIcon`），
不要引用该 key；把对应属性内联写在元素上（值从 WPF 样式抄），或省略该装饰，并在报告中列出。

## WPF → Avalonia 速查

| WPF | Avalonia |
|---|---|
| `Visibility="..."` / `BooleanToVisibility` | `IsVisible="..."`（bool 直接绑定） |
| `Style.Triggers` / `DataTrigger` / `MultiDataTrigger` | 伪类选择器（`:pointerover`、`:pressed`、`:disabled`、`:checked`、`:selected`、`:focus`）或代码隐藏；复杂触发器可省略并在报告说明 |
| `Background="Transparent"` 用于命中 | Avalonia 中 `Background="{x:Null}"` 不参与命中，透明要写 `Background="Transparent"`（同 WPF） |
| `TextBox.Text` / `Watermark` | 同；占位提示用 `Watermark` |
| `PasswordBox` | `TextBox PasswordChar="●"` |
| `ListBox`/`ComboBox`/`TabControl`/`TreeView`/`Expander`/`Slider`/`ProgressBar`/`CheckBox`/`RadioButton`/`ToggleButton` | 同名可用 |
| `DataGrid` | 不存在；用 `ListBox` + 标题行或省略，报告中说明 |
| `GridSplitter` | 可用但属性不同；能省则省 |
| `MenuItem.InputGestureText` | `MenuItem InputGesture="Ctrl+N"`（仅显示） |
| `x:Shared="False"` | 不支持；去掉（一次性资源可直接内联） |
| `StaticResource` 刷子 | 改 `{DynamicResource ...}` |
| `{Binding}` | 保留；窗口根加 `x:CompileBindings="False"` |
| `RelativeSource AncestorType=Window` | 可用但尽量少；必要时 `ElementName` |
| `ToolTipService.ShowOnDisabled` | 去掉 |
| `AutomationProperties.Name` | 保留（Avalonia.Automation 支持） |
| `IsCancel` / `IsDefault` | 支持 |
| `Window.ShowDialog()` | `await ShowDialog(owner)` 或直接 `ShowDialog(this)`；关闭用 `Close()` |
| `DialogResult = true` | Avalonia 无该属性；用 `Close()`，结果语义在代码隐藏里注释 |
| `FontFamily` from key | `{DynamicResource Font.UI}` / `Font.Mono` |
| `TextTrimming` | 同名，值 `CharacterEllipsis` 可用 |
| `Padding`/`Margin`/`Height`/`Width`/`MinWidth`/`MaxWidth` | 同名 |
| `ContentPresenter` | 同名（`Content`、`ContentTemplate`） |
| `Border.Background/BorderBrush/BorderThickness/CornerRadius` | 同名 |
| `Popup` | 同名（用 `PlacementTarget`/`Placement`） |
| `Geometry`/`StreamGeometry` | 用 `StreamGeometry`（Path Data 直接兼容） |
| `x:Static` | 避免；用字面量 |

## 数量参考

- WPF 窗口基本都已设置 `WindowStyle=None` + `WindowChrome`：Avalonia 移植统一用
  `ExtendClientAreaToDecorationsHint="True"`、`ExtendClientAreaChromeHints="NoChrome"`、
  `WindowStartupLocation="CenterOwner"`、`ShowInTaskbar="False"`（对话框）、`CanResize` 按源；
  标题栏用 Grid 自绘（可参考 `MainWindow.axaml` 的 36px 标题栏结构），
  关闭按钮走 `Close()`。无 WindowChrome 的简单对话框可直接用系统边框（`CanResize="False"`）。
- 迁移时保留原 `Title`、`Width`/`Height`/`MinWidth`/`MinHeight`、`ResizeMode` 对应物。

## 报告格式（最终消息）

1. 写出的文件清单（每个：WPF 源 → Avalonia 目标，行数）。
2. 每个窗口移植完成度（XAML% / 交互%）。
3. 省略或近似的 WPF 构造（触发器、DataGrid、AvalonEdit、缺失样式 key 等）。
4. 需要主任务提供的共享控件/样式（如有）。
