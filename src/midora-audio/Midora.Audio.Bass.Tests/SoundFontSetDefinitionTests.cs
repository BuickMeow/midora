using Midora.Audio;

namespace Midora.Audio.Bass.Tests;

public sealed class SoundFontSetDefinitionTests
{
    [Fact]
    public void ConfigurationPreservesPriorityOrderWithoutRequiringFiles()
    {
        string first = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sf2");
        string second = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sf2");

        SoundFontSetDefinition ordered = SoundFontSetDefinition.CreateConfiguration(
            [first, second]);
        SoundFontSetDefinition reversed = SoundFontSetDefinition.CreateConfiguration(
            [second, first]);

        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], ordered.Paths);
        Assert.NotEqual(ordered.CacheIdentity, reversed.CacheIdentity);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void DescriptorRoundTripPreservesEveryOriginalPathAndOrder()
    {
        string descriptor = Path.Combine(
            Path.GetTempPath(),
            $"midora-soundfonts-{Guid.NewGuid():N}.masf");
        string first = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sf2");
        string second = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sf2");
        try
        {
            SoundFontSetFile.Write(descriptor, [first, second]);

            Assert.Equal(
                [Path.GetFullPath(first), Path.GetFullPath(second)],
                SoundFontSetFile.Read(descriptor));
        }
        finally
        {
            File.Delete(descriptor);
        }
    }
}
