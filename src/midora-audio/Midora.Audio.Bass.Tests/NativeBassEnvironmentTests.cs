namespace Midora.Audio.Bass.Tests;

public class NativeBassEnvironmentTests
{
    [Fact]
    public void ShouldLoadInWindows()
    {
        NativeBassEnvironment.EnsureNativeLibrariesLoaded();
    }

    [Fact]
    public void ShouldProduceSounds()
    {
        NativeBassEnvironment.EnsureNativeLibrariesLoaded();

        
    }
}
