using Midora.Midi;

namespace Midora.Audio.Bass.Tests;

public sealed class MidiRenderEventStreamProtocolTests
{
    [Fact]
    public void ExactPcmHitSuppressesDemandAwareSourceBeforeIpc()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = CreateDirectory();
        try
        {
            DemandAwarePeriodicProvider provider = new();
            MidiRenderPlan plan = CreateCachedPlan(provider, cacheHit: true);
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(plan, directory));
            using MidiRenderEventStreamReader reader = new(producer.Descriptor);
            reader.RequestThrough(10_000);
            Assert.True(SpinWait.SpinUntil(() => reader.IsCompleted, TimeSpan.FromSeconds(5)));

            Assert.False(reader.TryPeek(out _));
            Assert.Equal(0, producer.Snapshot.EmittedRecordCount);
            Assert.True(producer.Snapshot.FullySuppressedWindowCount > 0);
            Assert.True(producer.Snapshot.SuppressedSourceWindowCount > 0);
            Assert.True(provider.SuppressedQueryCount > 0);
            Assert.Equal(0, provider.DemandedQueryCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PcmMissProducesDemandAwareSourceEvents()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = CreateDirectory();
        try
        {
            DemandAwarePeriodicProvider provider = new();
            MidiRenderPlan plan = CreateCachedPlan(provider, cacheHit: false);
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(plan, directory));
            using MidiRenderEventStreamReader reader = new(producer.Descriptor);
            reader.RequestThrough(10_000);
            Assert.True(SpinWait.SpinUntil(() => reader.IsCompleted, TimeSpan.FromSeconds(5)));

            int count = 0;
            while (reader.TryDequeue(out _)) count++;
            Assert.Equal(10, count);
            Assert.Equal(10, producer.Snapshot.EmittedRecordCount);
            Assert.Equal(0, producer.Snapshot.FullySuppressedWindowCount);
            Assert.True(provider.DemandedQueryCount > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MixedPcmHitAndMissProducesOnlyTheMissSource()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = CreateDirectory();
        try
        {
            DemandAwareMultiSourceProvider provider = new();
            MidiRenderPlan plan = CreateMixedCachedPlan(provider);
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(plan, directory));
            using MidiRenderEventStreamReader reader = new(producer.Descriptor);
            reader.RequestThrough(1_000);
            Assert.True(SpinWait.SpinUntil(() => reader.IsCompleted, TimeSpan.FromSeconds(5)));

            List<int> sources = [];
            while (reader.TryDequeue(out ScheduledPortMidiMessage value))
            {
                sources.Add(value.Scheduled.SourceIndex);
            }
            Assert.NotEmpty(sources);
            Assert.All(sources, sourceIndex => Assert.Equal(1, sourceIndex));
            Assert.True(provider.SuppressedSourceCount > 0);
            Assert.True(provider.DemandedSourceCount > 0);
            Assert.Equal(0, producer.Snapshot.FullySuppressedWindowCount);
            Assert.True(producer.Snapshot.SuppressedSourceWindowCount > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MonitoringChangePermanentlyBypassesAnExactPcmOwnerForFutureWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = CreateDirectory();
        try
        {
            using BlockingDemandAwareProvider provider = new();
            MidiRenderPlan plan = CreateCachedPlan(provider, cacheHit: true);
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(plan, directory));
            Assert.True(provider.FirstSuppressedQuery.Wait(TimeSpan.FromSeconds(5)));

            Task apply = Task.Run(() => producer.ApplyMonitoringCommands(
                [
                    MidiMonitoringCommand.DisableSource(0),
                    MidiMonitoringCommand.EnableSource(0)
                ],
                rewindFrame: 0));
            provider.Release();
            await apply.WaitAsync(TimeSpan.FromSeconds(5));
            using MidiRenderEventStreamReader reader = new(producer.Descriptor);
            reader.RequestThrough(10_000);
            Assert.True(SpinWait.SpinUntil(() => reader.IsCompleted, TimeSpan.FromSeconds(5)));

            int count = 0;
            while (reader.TryDequeue(out ScheduledPortMidiMessage value))
            {
                Assert.Equal(0, value.Scheduled.SourceIndex);
                count++;
            }
            Assert.True(count > 0);
            Assert.True(provider.DemandedQueryCount > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MonitoringAfterSuppressedPrefixRebuildsAReadableGenerationFromTheAudibleFrame()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = CreateDirectory();
        try
        {
            DemandAwarePeriodicProvider provider = new();
            MidiRenderPlan plan = CreateCachedPlan(provider, cacheHit: true);
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(plan, directory));
            using MidiRenderEventStreamReader reader = new(producer.Descriptor);
            reader.RequestThrough(10_000);
            Assert.True(SpinWait.SpinUntil(() => reader.IsCompleted, TimeSpan.FromSeconds(5)));
            Assert.False(reader.TryPeek(out _));

            producer.ApplyMonitoringCommands(
            [
                MidiMonitoringCommand.DisableSource(0),
                MidiMonitoringCommand.EnableSource(0)
            ],
            rewindFrame: 3_000);
            reader.Seek(3_000);
            reader.RequestThrough(10_000);
            Assert.True(SpinWait.SpinUntil(() => reader.IsCompleted, TimeSpan.FromSeconds(5)));

            List<long> frames = [];
            while (reader.TryDequeue(out ScheduledPortMidiMessage value))
                frames.Add(value.Scheduled.SampleFrame);
            Assert.Equal(
                [3_000L, 4_000L, 5_000L, 6_000L, 7_000L, 8_000L, 9_000L],
                frames);
            Assert.True(provider.DemandedQueryCount > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MonitoringInterruptsADenseUnpublishedWindowBeforeEnumeratingItsEntireSuffix()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = CreateDirectory();
        try
        {
            using SlowlyEnumeratingDemandAwareProvider provider = new();
            MidiRenderPlan plan = CreateCachedPlan(provider, cacheHit: false);
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(plan, directory));
            Assert.True(provider.EnumerationStarted.Wait(TimeSpan.FromSeconds(5)));

            long startedAt = Environment.TickCount64;
            producer.ApplyMonitoringCommands(
                [MidiMonitoringCommand.DisableSource(0)],
                rewindFrame: 0);

            Assert.True(Environment.TickCount64 - startedAt < 2_000);
            Assert.True(provider.EnumeratedRecordCount < 1_000);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReaderSeekCannotMixOldPublishedCountsWithTheNewMonitoringGeneration()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = CreateDirectory();
        try
        {
            DemandAwarePeriodicProvider provider = new();
            MidiRenderPlan plan = CreateCachedPlan(provider, cacheHit: false);
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(plan, directory));
            using MidiRenderEventStreamReader reader = new(producer.Descriptor);
            reader.RequestThrough(10_000);

            for (int iteration = 0; iteration < 200; iteration++)
            {
                bool enabled = (iteration & 1) != 0;
                producer.ApplyMonitoringCommands(
                    [enabled
                        ? MidiMonitoringCommand.EnableSource(0)
                        : MidiMonitoringCommand.DisableSource(0)],
                    rewindFrame: 0);
                reader.Seek(0);
                reader.RequestThrough(10_000);
                Assert.False(reader.IsFaulted);
            }

            Assert.True(SpinWait.SpinUntil(
                () => reader.IsCompleted || reader.IsFaulted,
                TimeSpan.FromSeconds(5)));
            Assert.False(reader.IsFaulted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MonitoringSeekFiltersOlderRecordsPublishedAfterTheSeekSnapshot()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = CreateDirectory();
        try
        {
            using DelayedDemandAwarePeriodicProvider provider = new();
            MidiRenderPlan plan = CreateCachedPlan(provider, cacheHit: true);
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(plan, directory));
            using MidiRenderEventStreamReader reader = new(producer.Descriptor);
            reader.RequestThrough(10_000);
            Assert.True(SpinWait.SpinUntil(() => reader.IsCompleted, TimeSpan.FromSeconds(5)));

            producer.ApplyMonitoringCommands(
            [
                MidiMonitoringCommand.DisableSource(0),
                MidiMonitoringCommand.EnableSource(0)
            ],
            rewindFrame: 0);
            Assert.True(provider.DemandedQueryStarted.Wait(TimeSpan.FromSeconds(5)));

            reader.Seek(3_000);
            reader.RequestThrough(10_000);
            provider.ReleaseDemandedQuery();
            Assert.True(SpinWait.SpinUntil(
                () => reader.IsCompleted || reader.IsFaulted,
                TimeSpan.FromSeconds(5)));
            Assert.False(reader.IsFaulted);

            List<long> frames = [];
            while (reader.TryDequeue(out ScheduledPortMidiMessage value))
            {
                frames.Add(value.Scheduled.SampleFrame);
            }
            Assert.Equal(
                [3_000L, 4_000L, 5_000L, 6_000L, 7_000L, 8_000L, 9_000L],
                frames);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RollingStreamExposesPartialSafeFrontierWhenCommittedWindowExceedsRing()
    {
        if (!OperatingSystem.IsWindows()) return;
        const int eventCount = 300_000;
        const long eventFrame = 100;
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-midi-event-stream-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            MidiRenderPlan plan = new(
                1_000,
                1_000,
                [],
                sourceIds: [1],
                unitDescriptors: [new MidiRenderUnitDescriptor(0, 0)],
                eventPageProvider: new FutureRepeatedEventProvider(eventCount, eventFrame));
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(plan, directory));
            using MidiRenderEventStreamReader reader = new(producer.Descriptor);
            reader.RequestThrough(1_000);

            Assert.True(
                SpinWait.SpinUntil(
                    () => reader.SafeThroughFrame >= eventFrame || reader.IsFaulted,
                    TimeSpan.FromSeconds(5)),
                "The reader filled with future events without exposing a safe render frontier.");
            Assert.False(reader.IsFaulted);

            int received = 0;
            long deadline = Environment.TickCount64 + 15_000;
            while ((!reader.IsCompleted || reader.TryPeek(out _))
                && Environment.TickCount64 < deadline)
            {
                while (reader.TryDequeue(out ScheduledPortMidiMessage value))
                {
                    Assert.Equal(eventFrame, value.Scheduled.SampleFrame);
                    received++;
                }
                if (!reader.IsCompleted) Thread.Yield();
            }

            Assert.True(reader.IsCompleted);
            Assert.Equal(eventCount, received);
            Assert.Equal(1_000, reader.SafeThroughFrame);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RollingStreamBackpressuresMoreThanOneRingAtTheSameFrameWithoutLoss()
    {
        if (!OperatingSystem.IsWindows()) return;
        const int eventCount = 300_000;
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-midi-event-stream-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            MidiRenderPlan plan = new(
                48_000,
                48_000,
                [],
                sourceIds: [1],
                unitDescriptors: [new MidiRenderUnitDescriptor(0, 0)],
                eventPageProvider: new RepeatedEventProvider(eventCount));
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(plan, directory));
            using MidiRenderEventStreamReader reader = new(producer.Descriptor);
            reader.RequestThrough(48_000);

            int received = 0;
            long deadline = Environment.TickCount64 + 15_000;
            while ((!reader.IsCompleted || reader.TryPeek(out _))
                && Environment.TickCount64 < deadline)
            {
                while (reader.TryDequeue(out ScheduledPortMidiMessage value))
                {
                    Assert.Equal(0, value.Scheduled.SampleFrame);
                    Assert.Equal((byte)60, value.Scheduled.Message.Byte1);
                    received++;
                }
                if (!reader.IsCompleted) Thread.Yield();
            }

            Assert.False(reader.IsFaulted);
            Assert.True(reader.IsCompleted);
            Assert.Equal(eventCount, received);
            Assert.Equal(48_000, reader.SafeThroughFrame);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RollingStreamSeekFindsFirstEventAtOrAfterFrame()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-midi-event-stream-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            MidiRenderPlan plan = new(
                1_000,
                10_000,
                [],
                sourceIds: [1],
                unitDescriptors: [new MidiRenderUnitDescriptor(0, 0)],
                eventPageProvider: new PeriodicEventProvider());
            using MidiRenderEventStreamProducer producer = Assert.IsType<MidiRenderEventStreamProducer>(
                MidiRenderEventStreamProducer.Create(plan, directory));
            using MidiRenderEventStreamReader reader = new(producer.Descriptor);
            reader.RequestThrough(10_000);
            Assert.True(SpinWait.SpinUntil(() => reader.IsCompleted, TimeSpan.FromSeconds(5)));

            reader.Seek(4_501);
            Assert.True(SpinWait.SpinUntil(() => reader.TryPeek(out _), TimeSpan.FromSeconds(5)));
            Assert.True(reader.TryDequeue(out ScheduledPortMidiMessage value));
            Assert.Equal(5_000, value.Scheduled.SampleFrame);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RepeatedEventProvider(int eventCount) : IMidiRenderEventPageProvider
    {
        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            CancellationToken cancellationToken = default)
        {
            if (startFrame != 0 || endFrame <= 0) yield break;
            for (int index = 0; index < eventCount; index++)
            {
                if ((index & 0xfff) == 0) cancellationToken.ThrowIfCancellationRequested();
                yield return new(0, new(
                    0,
                    MidiMessage.NoteOn(0, 60, checked((byte)(1 + index % 127))),
                    0));
            }
        }
    }

    private sealed class FutureRepeatedEventProvider(
        int eventCount,
        long eventFrame) : IMidiRenderEventPageProvider
    {
        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            CancellationToken cancellationToken = default)
        {
            if (eventFrame < startFrame || eventFrame >= endFrame) yield break;
            for (int index = 0; index < eventCount; index++)
            {
                if ((index & 0xfff) == 0) cancellationToken.ThrowIfCancellationRequested();
                yield return new(0, new(
                    eventFrame,
                    MidiMessage.NoteOn(0, 60, checked((byte)(1 + index % 127))),
                    0));
            }
        }
    }

    private sealed class PeriodicEventProvider : IMidiRenderEventPageProvider
    {
        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            CancellationToken cancellationToken = default)
        {
            long first = checked(((startFrame + 999) / 1_000) * 1_000);
            for (long frame = first; frame < endFrame; frame += 1_000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(0, new(frame, MidiMessage.NoteOn(0, 60, 100), 0));
            }
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-midi-event-stream-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static MidiRenderPlan CreateCachedPlan(
        IMidiRenderEventPageProvider provider,
        bool cacheHit)
    {
        string fingerprint = new('a', 64);
        string cacheKey = new('b', 64);
        return new(
            1_000,
            10_000,
            [],
            sourceIds: [1],
            segments:
            [
                new MidiSegmentRenderPlan(
                    1,
                    2,
                    0,
                    0,
                    10_000,
                    fingerprint,
                    cacheKey,
                    cacheHit ? -2 : 0,
                    cacheHit)
            ],
            unitDescriptors: [new MidiRenderUnitDescriptor(0, 0)],
            eventPageProvider: provider,
            cacheSourceBindings: [new MidiRenderCacheSourceBinding(0, 0)]);
    }

    private static MidiRenderPlan CreateMixedCachedPlan(
        IMidiRenderEventPageProvider provider)
    {
        string fingerprint = new('a', 64);
        return new(
            1_000,
            1_000,
            [],
            sourceIds: [1, 2],
            segments:
            [
                new MidiSegmentRenderPlan(
                    1,
                    2,
                    0,
                    0,
                    1_000,
                    fingerprint,
                    new string('b', 64),
                    -2,
                    pcmCacheHit: true),
                new MidiSegmentRenderPlan(
                    3,
                    4,
                    1,
                    0,
                    1_000,
                    fingerprint,
                    new string('c', 64),
                    0,
                    pcmCacheHit: false)
            ],
            unitDescriptors:
            [
                new MidiRenderUnitDescriptor(0, 0),
                new MidiRenderUnitDescriptor(0, 1)
            ],
            eventPageProvider: provider,
            cacheSourceBindings:
            [
                new MidiRenderCacheSourceBinding(0, 0),
                new MidiRenderCacheSourceBinding(1, 1)
            ]);
    }

    private sealed class DemandAwarePeriodicProvider : IMidiRenderEventDemandAwarePageProvider
    {
        public int DemandedQueryCount { get; private set; }
        public int SuppressedQueryCount { get; private set; }

        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The rolling producer must use the demand-aware overload.");

        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            MidiRenderEventDemandSnapshot demand,
            CancellationToken cancellationToken = default)
        {
            if (!demand.MayDemandSource(0, startFrame, endFrame))
            {
                SuppressedQueryCount++;
                yield break;
            }
            DemandedQueryCount++;
            long first = checked(((startFrame + 999) / 1_000) * 1_000);
            for (long frame = first; frame < endFrame; frame += 1_000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (demand.IsSourceDemanded(0, frame))
                {
                    yield return new(
                        0,
                        new(frame, MidiMessage.NoteOn(0, 60, 100), 0));
                }
            }
        }
    }

    private sealed class DemandAwareMultiSourceProvider : IMidiRenderEventDemandAwarePageProvider
    {
        private int _suppressedSourceCount;
        private int _demandedSourceCount;

        public int SuppressedSourceCount => Volatile.Read(ref _suppressedSourceCount);
        public int DemandedSourceCount => Volatile.Read(ref _demandedSourceCount);

        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The rolling producer must use the demand-aware overload.");

        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            MidiRenderEventDemandSnapshot demand,
            CancellationToken cancellationToken = default)
        {
            for (int sourceIndex = 0; sourceIndex < 2; sourceIndex++)
            {
                if (!demand.MayDemandSource(sourceIndex, startFrame, endFrame))
                {
                    Interlocked.Increment(ref _suppressedSourceCount);
                    continue;
                }
                Interlocked.Increment(ref _demandedSourceCount);
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(
                    0,
                    new(
                        startFrame,
                        MidiMessage.NoteOn(
                            checked((byte)sourceIndex),
                            checked((byte)(60 + sourceIndex)),
                            100),
                        sourceIndex));
            }
        }
    }

    private sealed class BlockingDemandAwareProvider :
        IMidiRenderEventDemandAwarePageProvider,
        IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _firstQuery = 1;
        private int _demandedQueryCount;

        public ManualResetEventSlim FirstSuppressedQuery { get; } = new(false);
        public int DemandedQueryCount => Volatile.Read(ref _demandedQueryCount);

        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The rolling producer must use the demand-aware overload.");

        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            MidiRenderEventDemandSnapshot demand,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _firstQuery, 0) != 0)
            {
                Assert.False(demand.MayDemandSource(0, startFrame, endFrame));
                FirstSuppressedQuery.Set();
                _release.Wait(cancellationToken);
                yield break;
            }
            if (!demand.MayDemandSource(0, startFrame, endFrame))
            {
                yield break;
            }
            Interlocked.Increment(ref _demandedQueryCount);
            yield return new(
                0,
                new(startFrame, MidiMessage.NoteOn(0, 60, 100), 0));
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
            FirstSuppressedQuery.Dispose();
        }
    }

    private sealed class SlowlyEnumeratingDemandAwareProvider :
        IMidiRenderEventDemandAwarePageProvider,
        IDisposable
    {
        private int _enumeratedRecordCount;

        public ManualResetEventSlim EnumerationStarted { get; } = new(false);
        public int EnumeratedRecordCount => Volatile.Read(ref _enumeratedRecordCount);

        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The rolling producer must use the demand-aware overload.");

        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            MidiRenderEventDemandSnapshot demand,
            CancellationToken cancellationToken = default)
        {
            if (!demand.MayDemandSource(0, startFrame, endFrame)) yield break;
            EnumerationStarted.Set();
            for (int index = 0; index < 1_000_000; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index != 0) Thread.Sleep(1);
                Interlocked.Increment(ref _enumeratedRecordCount);
                yield return new(
                    0,
                    new(startFrame, MidiMessage.NoteOn(0, 60, 100), 0));
            }
        }

        public void Dispose() => EnumerationStarted.Dispose();
    }

    private sealed class DelayedDemandAwarePeriodicProvider :
        IMidiRenderEventDemandAwarePageProvider,
        IDisposable
    {
        private readonly ManualResetEventSlim _releaseDemandedQuery = new(false);

        public ManualResetEventSlim DemandedQueryStarted { get; } = new(false);

        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The rolling producer must use the demand-aware overload.");

        public IEnumerable<ScheduledPortMidiMessage> Query(
            long startFrame,
            long endFrame,
            MidiRenderEventDemandSnapshot demand,
            CancellationToken cancellationToken = default)
        {
            if (!demand.MayDemandSource(0, startFrame, endFrame)) yield break;
            DemandedQueryStarted.Set();
            _releaseDemandedQuery.Wait(cancellationToken);
            long first = checked(((startFrame + 999) / 1_000) * 1_000);
            for (long frame = first; frame < endFrame; frame += 1_000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (demand.IsSourceDemanded(0, frame))
                {
                    yield return new(
                        0,
                        new(frame, MidiMessage.NoteOn(0, 60, 100), 0));
                }
            }
        }

        public void ReleaseDemandedQuery() => _releaseDemandedQuery.Set();

        public void Dispose()
        {
            _releaseDemandedQuery.Set();
            _releaseDemandedQuery.Dispose();
            DemandedQueryStarted.Dispose();
        }
    }
}
