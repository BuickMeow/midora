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
            SoundFontConfiguration[] expected =
            [
                new(first, null),
                new(second, new SoundFontTarget(12, 34, 56))
            ];
            SoundFontSetFile.Write(descriptor, expected);

            Assert.Equal(
                expected.Select(value => value.Normalize()),
                SoundFontSetFile.Read(descriptor));
        }
        finally
        {
            File.Delete(descriptor);
        }
    }

    [Fact]
    public void CacheIdentityIncludesTargetMapping()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sf2");

        SoundFontSetDefinition original = SoundFontSetDefinition.CreateConfiguration(
            [new SoundFontConfiguration(path, null)]);
        SoundFontSetDefinition mapped = SoundFontSetDefinition.CreateConfiguration(
            [new SoundFontConfiguration(path, new SoundFontTarget(1, 2, 3))]);

        Assert.NotEqual(original.CacheIdentity, mapped.CacheIdentity);
    }

    [Fact]
    public void SfzRequiresTargetAndMapsNominalSourceZeroToDestination()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sfz");
        Assert.Throws<ArgumentException>(() =>
            new SoundFontConfiguration(path, null).Validate());

        SoundFontConfiguration configuration = new(path, new(11, 22, 33));
        var native = Midora.Audio.Bass.Internals.BassMidiSoundFontMapping.Create(
            7,
            configuration);

        Assert.Equal((uint)7, native.font);
        Assert.Equal(0, native.spreset);
        Assert.Equal(0, native.sbank);
        Assert.Equal(33, native.dpreset);
        Assert.Equal(11, native.dbank);
        Assert.Equal(22, native.dbanklsb);
    }

    [Fact]
    public void TargetComponentsMustStayWithinMidiSevenBitRange()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sf2");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SoundFontConfiguration(path, new SoundFontTarget(128, 0, 0)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SoundFontConfiguration(path, new SoundFontTarget(0, 128, 0)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SoundFontConfiguration(path, new SoundFontTarget(0, 0, 128)).Validate());
    }

    [Fact]
    public void MappedSf2UsesMatchingSourceAndUnmappedSf2KeepsOriginalLayout()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sf2");
        var mapped = Midora.Audio.Bass.Internals.BassMidiSoundFontMapping.Create(
            8,
            new(path, new(9, 10, 11)));
        var original = Midora.Audio.Bass.Internals.BassMidiSoundFontMapping.Create(
            9,
            new(path, null));

        Assert.Equal(11, mapped.spreset);
        Assert.Equal(9, mapped.sbank);
        Assert.Equal(11, mapped.dpreset);
        Assert.Equal(9, mapped.dbank);
        Assert.Equal(10, mapped.dbanklsb);
        Assert.Equal(-1, original.spreset);
        Assert.Equal(-1, original.sbank);
        Assert.Equal(-1, original.dpreset);
        Assert.Equal(0, original.dbank);
        Assert.Equal(0, original.dbanklsb);
    }
}
