using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Midora.Domain;

namespace Midora.Persistence.Tests;

public sealed class SoundFontPersistenceV1Tests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void DomainStoresPortableSoundFontReferenceWithoutAbsoluteRuntimePath()
    {
        MidoraProject project = new(480);

        project.SoundFont.SetExternal("soundfonts/Piano.SF2", "Piano.SF2", HashA, 123);

        ExternalProjectSoundFontReference external = Assert.IsType<ExternalProjectSoundFontReference>(
            project.SoundFont.Reference);
        Assert.Equal("soundfonts/Piano.SF2", external.RelativePath);
        Assert.Equal(HashA, external.Sha256);
        Assert.Null(typeof(MidoraProject).GetProperty("SoundFontPath"));
        Assert.Throws<ArgumentException>(() =>
            project.SoundFont.SetExternal("assets/Piano.sf2", "Piano.sf2", HashA, 1));
        Assert.Throws<ArgumentException>(() =>
            project.SoundFont.SetExternal("Piano.sf2", "Other.sf2", HashA, 1));
        Assert.Throws<ArgumentException>(() =>
            project.SoundFont.SetExternal("Piano.sf2", "Piano.sf2", HashA.ToUpperInvariant(), 1));
        Assert.Throws<ArgumentException>(() =>
            project.SoundFont.SetExternal(".sf2", ".sf2", HashA, 1));

        UInt128 beforeInvalidEmbedded = project.NextStableId;
        Assert.Throws<ArgumentException>(() =>
            project.SoundFont.SetEmbedded(project, "Piano.sf2", "invalid", 1));
        Assert.Equal(beforeInvalidEmbedded, project.NextStableId);

        MidoraId resourceId = project.SoundFont.SetEmbedded(project, "Piano.sf2", HashA, 456);
        EmbeddedProjectSoundFontReference embedded = Assert.IsType<EmbeddedProjectSoundFontReference>(
            project.SoundFont.Reference);
        Assert.Equal(resourceId, embedded.ResourceId);
        project.SoundFont.Clear();
        Assert.Null(project.SoundFont.Reference);
    }

    [Fact]
    public void SoundFontSettingsJsonRoundTripsAllModesAndHasGoldenExternalBytes()
    {
        MidoraProject project = new(480);
        byte[] none = SoundFontSettingsCodecV1.Serialize(project.SoundFont);
        Assert.Null(SoundFontSettingsCodecV1.Parse(none));

        project.SoundFont.SetExternal("soundfonts/Piano.SF2", "Piano.SF2", HashA, 123);
        byte[] externalBytes = SoundFontSettingsCodecV1.Serialize(project.SoundFont);
        string expected = $$"""
            {
              "schemaVersion": 1,
              "mode": "external",
              "relativePath": "soundfonts/Piano.SF2",
              "originalFileName": "Piano.SF2",
              "sha256": "{{HashA}}",
              "fileSizeBytes": 123
            }

            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), Encoding.UTF8.GetString(externalBytes));
        Assert.Equal(
            project.SoundFont.Reference,
            SoundFontSettingsCodecV1.Parse(externalBytes));

        _ = project.SoundFont.SetEmbedded(project, "Orchestra.sf2", HashA, 456);
        byte[] embeddedBytes = SoundFontSettingsCodecV1.Serialize(project.SoundFont);
        Assert.Equal(
            project.SoundFont.Reference,
            SoundFontSettingsCodecV1.Parse(embeddedBytes));
    }

    [Fact]
    public void SoundFontSettingsJsonRejectsUnknownDuplicateAndInconsistentFields()
    {
        string unknown = """
            {"schemaVersion":1,"mode":"none","unknown":true}
            """;
        string duplicate = """
            {"schemaVersion":1,"mode":"none","mode":"external"}
            """;
        string inconsistent = $$"""
            {"schemaVersion":1,"mode":"none","sha256":"{{HashA}}"}
            """;

        Assert.Throws<JsonException>(() =>
            SoundFontSettingsCodecV1.Parse(Encoding.UTF8.GetBytes(unknown)));
        Assert.Throws<InvalidDataException>(() =>
            SoundFontSettingsCodecV1.Parse(Encoding.UTF8.GetBytes(duplicate)));
        Assert.Throws<InvalidDataException>(() =>
            SoundFontSettingsCodecV1.Parse(Encoding.UTF8.GetBytes(inconsistent)));
    }

    [Fact]
    public async Task ExternalBindingHashesRawBytesAndPassiveChangesDoNotRewriteReference()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Song.midora");
            string soundFontPath = Path.Combine(directory, "Piano.SF2");
            byte[] originalBytes = [0x01, 0x02, 0x03, 0x04];
            await File.WriteAllBytesAsync(soundFontPath, originalBytes);

            ExternalSoundFontBindingV1 binding = await SoundFontBindingV1.BindExternalAsync(
                projectPath, soundFontPath);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(originalBytes));
            Assert.Equal("Piano.SF2", binding.Reference.RelativePath);
            Assert.Equal(expectedHash, binding.Reference.Sha256);
            Assert.Equal(originalBytes.Length, binding.Reference.FileSizeBytes);
            Assert.False(binding.UsedCaseInsensitiveFallback);

            ExternalSoundFontVerificationV1 first = await SoundFontBindingV1.VerifyExternalAsync(
                projectPath, binding.Reference);
            Assert.Equal(ExternalSoundFontResolutionKind.Exact, first.Resolution);
            Assert.True(first.HashMatches);

            await File.WriteAllBytesAsync(soundFontPath, [0x05, 0x06, 0x07, 0x08]);
            ExternalSoundFontVerificationV1 changed = await SoundFontBindingV1.VerifyExternalAsync(
                projectPath, binding.Reference);

            Assert.False(changed.HashMatches);
            Assert.True(changed.IsReadable);
            Assert.True(changed.RequiresWarning);
            Assert.Equal(expectedHash, binding.Reference.Sha256);

            ExternalSoundFontBindingV1 accepted = await SoundFontBindingV1.BindExternalAsync(
                projectPath, soundFontPath);
            Assert.NotEqual(binding.Reference.Sha256, accepted.Reference.Sha256);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExternalBindingAllowsOnlyProjectRootOrDirectSoundfontsDirectory()
    {
        string directory = CreateTemporaryDirectory();
        string outsideDirectory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Song.midora");
            string soundFontsDirectory = Directory.CreateDirectory(
                Path.Combine(directory, "SoundFonts")).FullName;
            string allowed = Path.Combine(soundFontsDirectory, "Piano.sf2");
            await File.WriteAllBytesAsync(allowed, [1]);
            ExternalSoundFontBindingV1 binding = await SoundFontBindingV1.BindExternalAsync(
                projectPath, allowed);
            Assert.Equal("SoundFonts/Piano.sf2", binding.Reference.RelativePath);

            string nestedDirectory = Directory.CreateDirectory(
                Path.Combine(soundFontsDirectory, "Nested")).FullName;
            string nested = Path.Combine(nestedDirectory, "Piano.sf2");
            await File.WriteAllBytesAsync(nested, [1]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SoundFontBindingV1.BindExternalAsync(projectPath, nested));

            string outside = Path.Combine(outsideDirectory, "Piano.sf2");
            await File.WriteAllBytesAsync(outside, [1]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SoundFontBindingV1.BindExternalAsync(projectPath, outside));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VerificationUsesUniqueCaseInsensitiveFallbackWithoutMutatingStoredPath()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "Song.midora");
            string actualPath = Path.Combine(directory, "Piano.SF2");
            await File.WriteAllBytesAsync(actualPath, [1, 2, 3]);
            SoundFontContentIdentityV1 identity = await SoundFontBindingV1.ReadContentIdentityAsync(actualPath);
            ExternalProjectSoundFontReference stored = new(
                "piano.sf2", "piano.sf2", identity.Sha256, identity.FileSizeBytes);

            ExternalSoundFontVerificationV1 result = await SoundFontBindingV1.VerifyExternalAsync(
                projectPath, stored);

            Assert.Equal(ExternalSoundFontResolutionKind.CaseInsensitiveFallback, result.Resolution);
            Assert.Equal(actualPath, result.ResolvedAbsolutePath);
            Assert.True(result.HashMatches);
            Assert.True(result.RequiresWarning);
            Assert.Equal("piano.sf2", stored.RelativePath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CandidateSelectionPrefersExactThenUniqueFallbackAndRejectsAmbiguity()
    {
        Assert.Equal(
            ("Piano.sf2", false, false),
            SoundFontBindingV1.SelectCandidateName(["Piano.sf2", "piano.sf2"], "Piano.sf2"));
        Assert.Equal(
            ("Piano.sf2", true, false),
            SoundFontBindingV1.SelectCandidateName(["Piano.sf2"], "piano.sf2"));
        Assert.Equal(
            ((string?)null, false, true),
            SoundFontBindingV1.SelectCandidateName(["Piano.sf2", "PIANO.SF2"], "piano.sf2"));
    }

    [Fact]
    public void CheckedInSoundFontSchemaIsStrictDraft202012()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "Schemas", "Json", "soundfont-settings-v1.schema.json");
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(path));

        Assert.Equal(PersistenceContractV1.JsonSchemaDialect,
            schema.RootElement.GetProperty("$schema").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"midora-sf2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
