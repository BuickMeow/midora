using Xunit;

namespace Midora.Desktop.Tests;

public sealed class BatchEditPresetStoreTests
{
    [Fact]
    public void NoteAndEventPresetsUseIndependentFlatDirectoriesAndRejectNameCollisions()
    {
        using TemporaryDirectory temporary = new();
        BatchEditPresetStore store = new(temporary.Path);
        BatchEditPreset note = new(
            1,
            "Humanize",
            BatchEditPresetKind.Note,
            "+4",
            string.Empty,
            "=Clamp(k0 + 1, 0, 127)",
            "*1.1",
            string.Empty);
        BatchEditPreset point = new(
            1,
            "Humanize",
            BatchEditPresetKind.Event,
            string.Empty,
            "95%",
            string.Empty,
            string.Empty,
            "+2");

        BatchEditPresetInfo noteInfo = store.Save(note);
        BatchEditPresetInfo pointInfo = store.Save(point);

        Assert.Equal("NoteBatchPresets", Directory.GetParent(noteInfo.Path)!.Name);
        Assert.Equal("EventBatchPresets", Directory.GetParent(pointInfo.Path)!.Name);
        Assert.Equal(note, Assert.Single(store.Load(BatchEditPresetKind.Note)).Preset);
        Assert.Equal(point, Assert.Single(store.Load(BatchEditPresetKind.Event)).Preset);
        Assert.Throws<IOException>(() => store.Save(note));

        store.Delete(noteInfo);
        Assert.Empty(store.Load(BatchEditPresetKind.Note));
        Assert.Single(store.Load(BatchEditPresetKind.Event));
    }

    [Fact]
    public void DamagedPresetDoesNotPreventOtherPresetsFromLoading()
    {
        using TemporaryDirectory temporary = new();
        BatchEditPresetStore store = new(temporary.Path);
        BatchEditPresetInfo valid = store.Save(new(
            1,
            "Valid",
            BatchEditPresetKind.Event,
            string.Empty,
            "+1",
            string.Empty,
            string.Empty,
            string.Empty));
        File.WriteAllText(
            System.IO.Path.Combine(Directory.GetParent(valid.Path)!.FullName, "Damaged.json"),
            "{not json");

        Assert.Equal("Valid", Assert.Single(store.Load(BatchEditPresetKind.Event)).Name);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "midora-batch-presets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
