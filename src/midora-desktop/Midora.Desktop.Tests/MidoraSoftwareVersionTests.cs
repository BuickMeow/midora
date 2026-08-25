using System.Diagnostics;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class MidoraSoftwareVersionTests
{
    [Fact]
    public void ProductAndWindowsVersionsComeFromTheCentralVersionContract()
    {
        Assert.Equal("1.0.0-dev", MidoraSoftwareVersion.ProductVersion);
        Assert.Matches(
            "^1\\.0\\.0-dev(?:\\+[0-9a-f]{40})?$",
            MidoraSoftwareVersion.InformationalVersion);

        System.Reflection.Assembly assembly = typeof(MidoraSoftwareVersion).Assembly;
        Assert.Equal(new Version(1, 0, 0, 0), assembly.GetName().Version);
        Assert.Equal(
            "1.0.0.0",
            FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion);
    }
}
