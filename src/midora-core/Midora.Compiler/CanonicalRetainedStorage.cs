using Midora.Common;

namespace Midora.Compiler;

public sealed partial class CompilationContextSummary : IRetainedStorageSource
{
    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 112)) return;
        collector.Array(_includedTrackIds);
        collector.Array(_includedSubVoiceIds);
    }
}

public sealed partial class CanonicalConductor : IRetainedStorageSource
{
    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 88)) return;
        collector.Array(_tempos);
        collector.Array(_timeSignatures);
        collector.Array(_sourceTimeSignatureMap);
        collector.Array(_keySignatures);
        if (collector.Array(_markers))
            foreach (CanonicalMarker marker in _markers) collector.Text(marker.Name);
    }
}

public sealed partial class CanonicalCompiledResult : IRetainedStorageSource
{
    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 288)) return;
        Context.CollectRetainedStorage(collector);
        Conductor.CollectRetainedStorage(collector);
        collector.Array(_events);
        collector.Array(_allocations);
        collector.Array(_channelModeSystemExclusiveEvents);
        if (collector.Array(_diagnostics))
            foreach (CompilerDiagnostic diagnostic in _diagnostics)
            {
                collector.Add(diagnostic, 208);
                collector.Text(diagnostic.Code);
                collector.Text(diagnostic.Message);
            }
        if (collector.Array(_smfTracks))
            foreach (CanonicalSmfTrackDescriptor track in _smfTracks)
            {
                collector.Text(track.Name);
                collector.Text(track.MidiChannelRootName);
            }
        if (collector.Array(_opaqueMidiEvents))
            foreach (CanonicalOpaqueMidiEvent value in _opaqueMidiEvents)
                collector.Bytes(value.Payload);
        if (_pagedEventSource is IRetainedStorageSource source)
            source.CollectRetainedStorage(collector);
    }
}
