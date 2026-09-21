# Midora Avalonia 呈现核心移植指南（Slice C/D 子任务）

配合 `misc/avalonia/Midora-Avalonia-Window-Porting-Guide.md`（窗口移植规范）。
本指南只适用于把 `src/midora-desktop/Midora.Desktop.Presentation` 的呈现核心移植到
`src/midora-avalonia/Midora.Avalonia.Presentation`（Avalonia 12.1.2 / SkiaSharp 3.119.4 / .NET 10）。

## 硬约束

1. 只写分配给你的文件（路径按命名空间映射）；不要改共享文件、`.csproj`、其他 Agent 的文件。
2. **不得引用任何 WPF/`System.Windows*`/`Midora.Desktop*`**，不得新增 NuGet 包（只用 Avalonia 12.1.2 与其传递依赖 SkiaSharp 3.119.4）。
3. 不要构建（并行任务争抢 obj/bin）；写完自检语法与 API，构建由主任务统一执行。
4. 命名空间映射：`Midora.Desktop.Presentation.Rendering` → `Midora.Avalonia.Presentation.Rendering`；
   `...Interaction` → `Midora.Avalonia.Presentation.Interaction`；`...Controls` → `Midora.Avalonia.Presentation.Controls`。
5. 保留原有类型名、成员名、算法与常量，只替换 API；便于后续与 WPF 侧对照。
6. 遇到无法一一对应的 WPF API：优先用 Avalonia 等价物；确实没有时写一个本文件私有的最小替代，
   并在最终报告中列出。

## 已有共享基础设施（直接用，不要重复实现）

- `Midora.Avalonia.Presentation.Rendering.DpiScale`（`DpiScaleX`、`DpiScaleY`、`PixelsPerDip`）。
- `Midora.Avalonia.Presentation.Rendering.PixelBufferBitmap.Create(byte[] pbgra32, int width, int height, double dpiX, double dpiY)`
  → `WriteableBitmap`，替代 `BitmapSource.Create(...) + Freeze()`。

## API 替换表

| WPF | Avalonia |
|---|---|
| `System.Windows.Media.Color` | `Avalonia.Media.Color`（`FromArgb/FromRgb` 同名） |
| `System.Windows.Rect` / `Point` / `Size` / `Thickness` | `Avalonia.Rect` / `Point` / `Size` / `Thickness` |
| `System.Windows.Media.SolidColorBrush` / `Pen` / `Brush` | `Avalonia.Media.*` 同名 |
| `System.Windows.Media.DpiScale` | `Midora.Avalonia.Presentation.Rendering.DpiScale` |
| `System.Windows.Media.Imaging.BitmapSource` | `Avalonia.Media.Imaging.WriteableBitmap`（或 `IImage`） |
| `BitmapSource.Create(...)` + `Freeze()` | `PixelBufferBitmap.Create(...)`，删除 `Freeze()` |
| `System.Windows.Media.Imaging.WriteableBitmap` | `Avalonia.Media.Imaging.WriteableBitmap`（`Lock()` + `ILockedFramebuffer`） |
| `System.Windows.Media.PixelFormats.Pbgra32` | `Avalonia.Platform.PixelFormat.Bgra8888` + `AlphaFormat.Premul` |
| `Int32Rect` | `PixelRect` 或四个 int |
| `System.Windows.Threading.Dispatcher` | `Avalonia.Threading.Dispatcher.UIThread`（`Post`/`InvokeAsync`/`CheckAccess`） |
| `DispatcherPriority.Render` / `Background` | `Avalonia.Threading.DispatcherPriority.Render` / `Background` |
| `System.Windows.Media.FormattedText` | `Avalonia.Media.FormattedText`（构造：`(text, CultureInfo, FlowDirection, Typeface, fontSize, brush)`） |
| `System.Windows.Media.Typeface` | `Avalonia.Media.Typeface` |
| `System.Windows.Media.FontFamily` | `Avalonia.Media.FontFamily` |
| `System.Windows.Media.StreamGeometry` | `Avalonia.Media.StreamGeometry`（`Open()` 后 `BeginFigure/LineTo/EndFigure`） |
| `System.Windows.Media.Matrix` / `Transform` | `Avalonia.Matrix` / `Avalonia.Media.Transform` |
| `RenderOptions.*` / `UseLayoutRounding` / `SnapsToDevicePixels` | 删除 |
| `Freeze()` / `CanFreeze` / `IsFrozen` | 删除 |

## 命名空间遮蔽陷阱

在 `Midora.Avalonia.*` 命名空间内，`Avalonia.X` 会被解析为 `Midora.Avalonia.X`。
需要引用框架时使用 `global::Avalonia.X`（例如 `global::Avalonia.Media.Color` 若报错）。

## 报告格式

1. 每个源文件 → 目标文件、行数、完成度。
2. 替换表之外的 API 处理方式（尤其是位图、Dispatcher、文本）。
3. 无法移植/被删减的功能与原因。
4. 需要主任务补充的共享类型或文件。
