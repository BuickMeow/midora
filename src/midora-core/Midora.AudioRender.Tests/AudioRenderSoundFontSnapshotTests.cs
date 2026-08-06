using System.Security.Cryptography;
using Midora.Domain;
using Midora.Persistence;

namespace Midora.AudioRender.Tests;

public sealed class AudioRenderSoundFontSnapshotTests
{
    [Fact]
    public async Task ExternalSnapshotFreezesBytesAndNeverMutatesStoredIdentity()
    {
        string directory = AudioRenderTestProject.CreateOwnedDirectory("midora-sf2-snapshot-test");
        try
        {
            MidoraProject project = new(192);
            string projectPath = Path.Combine(directory, "Project.midora");
            string soundFontPath = Path.Combine(directory, "Piano.sf2");
            byte[] original = Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray();
            await File.WriteAllBytesAsync(soundFontPath, original);
            ExternalSoundFontBindingV1 binding = await SoundFontBindingV1.BindExternalAsync(
                projectPath,
                soundFontPath);
            project.SoundFont.SetExternal(
                binding.Reference.RelativePath,
                binding.Reference.OriginalFileName,
                binding.Reference.Sha256,
                binding.Reference.FileSizeBytes);

            string frozenPath;
            await using (AudioRenderSoundFontSnapshot snapshot = await AudioRenderSoundFontSnapshot.CreateAsync(
                project,
                projectPath,
                embeddedResource: null,
                acceptExternalHashChange: false))
            {
                frozenPath = snapshot.FrozenPath;
                await File.WriteAllBytesAsync(soundFontPath, [9, 8, 7, 6]);
                Assert.Equal(original, await File.ReadAllBytesAsync(snapshot.FrozenPath));
                Assert.Equal(binding.Reference.Sha256, snapshot.Sha256);
                Assert.Equal(binding.Reference.Sha256, project.SoundFont.Reference!.Sha256);
            }
            Assert.False(File.Exists(frozenPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExternalHashChangeRequiresExplicitAcceptanceAndProducesOnlyRuntimeWarning()
    {
        string directory = AudioRenderTestProject.CreateOwnedDirectory("midora-sf2-change-test");
        try
        {
            MidoraProject project = new(192);
            string projectPath = Path.Combine(directory, "Project.midora");
            string soundFontPath = Path.Combine(directory, "Piano.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [1, 2, 3]);
            ExternalSoundFontBindingV1 binding = await SoundFontBindingV1.BindExternalAsync(
                projectPath,
                soundFontPath);
            project.SoundFont.SetExternal(
                binding.Reference.RelativePath,
                binding.Reference.OriginalFileName,
                binding.Reference.Sha256,
                binding.Reference.FileSizeBytes);
            await File.WriteAllBytesAsync(soundFontPath, [4, 5, 6, 7]);

            AudioRenderSoundFontException blocked = await Assert.ThrowsAsync<AudioRenderSoundFontException>(() =>
                AudioRenderSoundFontSnapshot.CreateAsync(
                    project,
                    projectPath,
                    embeddedResource: null,
                    acceptExternalHashChange: false));
            Assert.Equal(
                AudioRenderSoundFontFailure.ExternalHashChangeRequiresConfirmation,
                blocked.Failure);

            await using AudioRenderSoundFontSnapshot accepted = await AudioRenderSoundFontSnapshot.CreateAsync(
                project,
                projectPath,
                embeddedResource: null,
                acceptExternalHashChange: true);
            Assert.Contains(accepted.Diagnostics, value =>
                value.Code == "MIDORA-AUDIO-RENDER-SF2-HASH-CHANGED"
                && value.Severity == AudioRenderDiagnosticSeverity.Warning);
            Assert.NotEqual(binding.Reference.Sha256, accepted.Sha256);
            Assert.Equal(binding.Reference.Sha256, project.SoundFont.Reference!.Sha256);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EmbeddedSnapshotRequiresMatchingAvailableRuntimeResource()
    {
        string directory = AudioRenderTestProject.CreateOwnedDirectory("midora-embedded-sf2-test");
        try
        {
            MidoraProject project = new(192);
            string soundFontPath = Path.Combine(directory, "Piano.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [11, 12, 13, 14]);
            await using EmbeddedSoundFontResourceV1 resource = await SoundFontBindingV1.BindEmbeddedAsync(
                project,
                soundFontPath);

            await using AudioRenderSoundFontSnapshot snapshot = await AudioRenderSoundFontSnapshot.CreateAsync(
                project,
                currentProjectFilePath: null,
                resource,
                acceptExternalHashChange: false);

            Assert.Equal(await File.ReadAllBytesAsync(soundFontPath), await File.ReadAllBytesAsync(snapshot.FrozenPath));
            Assert.Equal(project.SoundFont.Reference!.Sha256, snapshot.Sha256);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingReferenceAndExternalProjectPathAreStructuredFailures()
    {
        MidoraProject missing = new(192);
        AudioRenderSoundFontException noReference = await Assert.ThrowsAsync<AudioRenderSoundFontException>(() =>
            AudioRenderSoundFontSnapshot.CreateAsync(
                missing,
                currentProjectFilePath: null,
                embeddedResource: null,
                acceptExternalHashChange: false));
        Assert.Equal(AudioRenderSoundFontFailure.NoReference, noReference.Failure);

        MidoraProject external = new(192);
        external.SoundFont.SetExternal(
            "Piano.sf2",
            "Piano.sf2",
            Convert.ToHexStringLower(SHA256.HashData([1, 2, 3])),
            3);
        AudioRenderSoundFontException noPath = await Assert.ThrowsAsync<AudioRenderSoundFontException>(() =>
            AudioRenderSoundFontSnapshot.CreateAsync(
                external,
                currentProjectFilePath: null,
                embeddedResource: null,
                acceptExternalHashChange: false));
        Assert.Equal(AudioRenderSoundFontFailure.ProjectPathRequired, noPath.Failure);
    }
}
