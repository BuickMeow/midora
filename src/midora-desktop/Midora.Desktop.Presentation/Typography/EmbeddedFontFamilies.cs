using System.Windows.Media;

namespace Midora.Desktop.Presentation.Typography;

public static class EmbeddedFontFamilies
{
    private static readonly Uri ApplicationBaseUri = new("pack://application:,,,/", UriKind.Absolute);

    public static FontFamily Ui { get; } = new(
        ApplicationBaseUri,
        "/Midora.Desktop.Presentation;Component/Assets/Fonts/Sora/#Sora");

    public static FontFamily Monospace { get; } = new(
        ApplicationBaseUri,
        "/Midora.Desktop.Presentation;Component/Assets/Fonts/JetBrainsMono/#JetBrains Mono");
}
