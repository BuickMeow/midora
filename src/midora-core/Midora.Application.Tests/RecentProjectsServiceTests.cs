using System.Text;

namespace Midora.Application.Tests;

public sealed class RecentProjectsServiceTests
{
    [Fact]
    public void MissingStoreStartsEmptyWithoutNotice()
    {
        using TemporaryDirectory directory = new();
        RecentProjectsService service = new(new RecentProjectsStore(
            directory.PathFor("recent-projects.json")));

        Assert.Null(service.StartupNotice);
        Assert.Empty(service.Current);
    }

    [Fact]
    public void SuccessfulActivationMaintainsBoundedCaseInsensitiveMruOrder()
    {
        using TemporaryDirectory directory = new();
        string storePath = directory.PathFor("recent-projects.json");
        RecentProjectsStore store = new(storePath);
        RecentProjectsService service = new(store);
        string[] paths = Enumerable.Range(0, 11)
            .Select(index => directory.PathFor($"Project-{index}.midora"))
            .ToArray();

        foreach (string path in paths)
        {
            Assert.Equal(
                RecentProjectsUpdateStatus.Applied,
                service.RecordSuccessfulProjectActivation(path).Status);
        }
        Assert.Equal(
            RecentProjectsUpdateStatus.NoChange,
            service.RecordSuccessfulProjectActivation(paths[^1].ToUpperInvariant()).Status);

        string[] expected = paths.Reverse().Take(RecentProjectsStore.MaximumEntries).ToArray();
        Assert.Equal(expected, service.Current.Select(entry => entry.Path));
        Assert.Equal(expected, store.Load().Paths);
        Assert.DoesNotContain(paths[0], store.Load().Paths);
    }

    [Fact]
    public void StoreRoundTripIsDeterministicAndPreservesUnavailableEntries()
    {
        using TemporaryDirectory directory = new();
        string storePath = directory.PathFor("recent-projects.json");
        RecentProjectsStore store = new(storePath);
        string existing = directory.PathFor("Existing.midora");
        string unavailable = directory.PathFor("Unavailable.midora");
        File.WriteAllText(existing, "project");

        Assert.True(store.Save([existing, unavailable]).Succeeded);
        byte[] first = File.ReadAllBytes(storePath);
        Assert.True(store.Save([existing, unavailable]).Succeeded);
        byte[] second = File.ReadAllBytes(storePath);
        RecentProjectsService service = new(store);

        Assert.Equal(first, second);
        Assert.Equal(
            [true, false],
            service.Current.Select(entry => entry.IsCurrentlyAvailable));
        File.Delete(existing);
        Assert.Equal(
            [false, false],
            service.Current.Select(entry => entry.IsCurrentlyAvailable));
        Assert.Equal([existing, unavailable], store.Load().Paths);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"paths\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"paths\":[],\"unknown\":true}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"paths\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"paths\":null}")]
    [InlineData("not-json")]
    public void UnsupportedCorruptDuplicateOrUnknownJsonUsesEmptyList(string json)
    {
        using TemporaryDirectory directory = new();
        string storePath = directory.PathFor("recent-projects.json");
        File.WriteAllText(
            storePath,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        RecentProjectsLoadResult result = new RecentProjectsStore(storePath).Load();

        Assert.Empty(result.Paths);
        Assert.Equal("RecentProjectsReadFailed", result.Notice?.Code);
        Assert.NotNull(result.Notice?.Error);
    }

    [Fact]
    public void InvalidPathsDuplicatesAndOversizedListsAreRejectedBeforeWrite()
    {
        using TemporaryDirectory directory = new();
        RecentProjectsStore store = new(directory.PathFor("recent-projects.json"));
        string path = directory.PathFor("Project.midora");

        Assert.Throws<ArgumentException>(() => store.Save(["relative.midora"]));
        Assert.Throws<ArgumentException>(() => store.Save([path, path.ToUpperInvariant()]));
        Assert.Throws<ArgumentException>(() => store.Save(
            Enumerable.Range(0, RecentProjectsStore.MaximumEntries + 1)
                .Select(index => directory.PathFor($"{index}.midora"))));
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void OversizedFileUsesEmptyListWithoutUnboundedAllocation()
    {
        using TemporaryDirectory directory = new();
        string storePath = directory.PathFor("recent-projects.json");
        using (FileStream stream = File.Create(storePath))
        {
            stream.SetLength((1024 * 1024) + 1);
        }

        RecentProjectsLoadResult result = new RecentProjectsStore(storePath).Load();

        Assert.Empty(result.Paths);
        Assert.Equal("RecentProjectsReadFailed", result.Notice?.Code);
    }

    [Fact]
    public void FailedReplacementPreservesPublishedAndInMemoryLists()
    {
        using TemporaryDirectory directory = new();
        string storePath = directory.PathFor("recent-projects.json");
        RecentProjectsStore store = new(storePath);
        string original = directory.PathFor("Original.midora");
        string replacement = directory.PathFor("Replacement.midora");
        Assert.True(store.Save([original]).Succeeded);
        RecentProjectsService service = new(store);

        RecentProjectsUpdateResult failed;
        using (FileStream locked = new(
            storePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            failed = service.RecordSuccessfulProjectActivation(replacement);
        }

        Assert.Equal(RecentProjectsUpdateStatus.Failed, failed.Status);
        Assert.Equal("RecentProjectsWriteFailed", failed.Notice?.Code);
        Assert.Equal([original], service.Current.Select(entry => entry.Path));
        Assert.Equal([original], store.Load().Paths);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void RemoveAndClearPersistOnlyActualChanges()
    {
        using TemporaryDirectory directory = new();
        RecentProjectsStore store = new(directory.PathFor("recent-projects.json"));
        string first = directory.PathFor("First.midora");
        string second = directory.PathFor("Second.midora");
        Assert.True(store.Save([first, second]).Succeeded);
        RecentProjectsService service = new(store);

        Assert.Equal(
            RecentProjectsUpdateStatus.NoChange,
            service.Remove(directory.PathFor("Missing.midora")).Status);
        Assert.Equal(RecentProjectsUpdateStatus.Applied, service.Remove(first).Status);
        Assert.Equal([second], store.Load().Paths);
        Assert.Equal(RecentProjectsUpdateStatus.Applied, service.Clear().Status);
        Assert.Empty(store.Load().Paths);
        Assert.Equal(RecentProjectsUpdateStatus.NoChange, service.Clear().Status);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-recent-project-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string PathFor(string fileName) => System.IO.Path.Combine(Path, fileName);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
