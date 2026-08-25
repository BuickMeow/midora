using System.Reflection;

namespace Midora.Desktop;

internal static class MidoraSoftwareVersion
{
    private static readonly Assembly ProductAssembly = typeof(MidoraSoftwareVersion).Assembly;

    public static string InformationalVersion { get; } =
        ProductAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException(
            "The Midora product assembly does not contain an informational version.");

    public static string ProductVersion { get; } =
        InformationalVersion.Split('+', 2, StringSplitOptions.None)[0];
}
