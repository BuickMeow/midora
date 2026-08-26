using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Midora.Desktop.Presentation.Tests;

public sealed class WorkspaceSelectionScalabilityTests
{
    [Fact]
    public void TimelineSnapshotSharesImmutableMillionItemSelection()
    {
        ImmutableHashSet<MidoraId> ids = Enumerable.Range(1, 1_000_000)
            .Select(static value => new MidoraId(value))
            .ToImmutableHashSet();

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        TimelineSelectionSnapshot snapshot = new(1, ids, new MidoraId(1));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(ids, snapshot.SharedIds);
        Assert.Equal(ids.Count, snapshot.Count);
        Assert.True(
            allocated < 64 * 1024,
            $"Refreshing a million-item immutable selection allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void TinyRangeUpdateDoesNotCopyMillionItemSelection()
    {
        const int count = 1_000_000;
        MidoraId[] ids = Enumerable.Range(1, count)
            .Select(static value => new MidoraId(value))
            .ToArray();
        WorkspaceSelection selection = new();
        Assert.True(selection.ReplaceAll(ids, ids[0]));
        long revision = selection.Revision;

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        selection.ApplyRange([ids[^1]], WorkspaceSelectionRangeMode.Add);
        selection.ApplyRange([new MidoraId(count + 1L)], WorkspaceSelectionRangeMode.Remove);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(revision, selection.Revision);
        Assert.Equal(count, selection.Ids.Count);
        Assert.True(
            allocated < 256 * 1024,
            $"A two-item range update allocated {allocated:N0} bytes for an existing million-item selection.");
    }

    [Fact]
    public void TrustedWorkspaceSnapshotPublicationDoesNotRescanMillionItemRoot()
    {
        WorkspaceSelection selection = new();
        MidoraId[] ids = Enumerable.Range(1, 1_000_000)
            .Select(static value => new MidoraId(value))
            .ToArray();
        Assert.True(selection.ReplaceAll(ids, ids[0]));

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        TimelineSelectionSnapshot snapshot =
            TimelineSelectionSnapshot.FromWorkspaceSelection(selection);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(ids.Length, snapshot.Count);
        Assert.Same(selection.SharedIds, snapshot.SharedIds);
        Assert.True(
            allocated < 64 * 1024,
            $"Trusted selection publication allocated {allocated:N0} bytes.");
        Assert.True(
            watch.Elapsed < TimeSpan.FromMilliseconds(100),
            $"Trusted selection publication took {watch.Elapsed.TotalMilliseconds:0.0} ms.");
    }

    [Fact]
    public void RangeOperationsPreserveRevisionAndEndpointSemanticsWithoutBeforeSet()
    {
        WorkspaceSelection selection = new();
        MidoraId one = new(1);
        MidoraId two = new(2);
        MidoraId three = new(3);
        selection.ApplyRange([one, two], WorkspaceSelectionRangeMode.Replace);
        Assert.Equal(one, selection.Primary);
        Assert.Equal(one, selection.Anchor);

        long revision = selection.Revision;
        selection.ApplyRange([one], WorkspaceSelectionRangeMode.Add);
        selection.ApplyRange([three], WorkspaceSelectionRangeMode.Remove);
        Assert.Equal(revision, selection.Revision);

        selection.ApplyRange([one], WorkspaceSelectionRangeMode.Remove);
        Assert.Equal(two, selection.Primary);
        Assert.Equal(two, selection.Anchor);
        Assert.Equal(revision + 1, selection.Revision);

        selection.ApplyRange([two, three], WorkspaceSelectionRangeMode.Toggle);
        Assert.Equal([three], selection.Ids);
        Assert.Equal(three, selection.Primary);
        Assert.Equal(three, selection.Anchor);
    }

    [Fact]
    public void LargePrunePublishesOneSelectionRevision()
    {
        const int count = 200_000;
        MidoraId[] ids = Enumerable.Range(1, count)
            .Select(static value => new MidoraId(value))
            .ToArray();
        WorkspaceSelection selection = new();
        Assert.True(selection.ReplaceAll(ids, ids[^1]));
        long revision = selection.Revision;
        ImmutableHashSet<MidoraId> retained = ids
            .Where(static value => (value.Value & 1) == 0)
            .ToImmutableHashSet();

        Stopwatch watch = Stopwatch.StartNew();
        Assert.True(selection.RetainOnly(retained));
        watch.Stop();

        Assert.Equal(revision + 1, selection.Revision);
        Assert.Equal(retained.Count, selection.Ids.Count);
        Assert.Equal(ids[^1], selection.Primary);
        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(1),
            $"Atomic selection pruning took {watch.Elapsed.TotalMilliseconds:N1} ms.");
    }
}
