using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Playback;

internal sealed class CanonicalMidiRenderEventPageProvider :
    IMidiRenderEventDemandAwarePageProvider
{
    private readonly CanonicalCompiledResult _compiled;
    private readonly TempoSampleMap _map;
    private readonly int _sampleRate;
    private readonly IReadOnlyDictionary<MidoraId, int> _sourceIndices;
    private readonly IReadOnlySet<MidoraId>? _audibleTrackIds;

    public CanonicalMidiRenderEventPageProvider(
        CanonicalCompiledResult compiled,
        TempoSampleMap map,
        int sampleRate,
        IReadOnlyDictionary<MidoraId, int> sourceIndices,
        IReadOnlySet<MidoraId>? audibleTrackIds)
    {
        _compiled = compiled ?? throw new ArgumentNullException(nameof(compiled));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        _sourceIndices = sourceIndices ?? throw new ArgumentNullException(nameof(sourceIndices));
        _audibleTrackIds = audibleTrackIds;
    }

    public IEnumerable<ScheduledPortMidiMessage> Query(
        long startFrame,
        long endFrame,
        CancellationToken cancellationToken = default) =>
        QueryCore(startFrame, endFrame, demand: null, cancellationToken);

    public IEnumerable<ScheduledPortMidiMessage> Query(
        long startFrame,
        long endFrame,
        MidiRenderEventDemandSnapshot demand,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demand);
        return QueryCore(startFrame, endFrame, demand, cancellationToken);
    }

    private IEnumerable<ScheduledPortMidiMessage> QueryCore(
        long startFrame,
        long endFrame,
        MidiRenderEventDemandSnapshot? demand,
        CancellationToken cancellationToken)
    {
        if (startFrame < 0 || endFrame <= startFrame)
            throw new ArgumentOutOfRangeException(nameof(startFrame));
        long totalFrames = _map.TickToSampleFrame(
            _compiled.EndTick,
            _compiled.StartTick,
            _sampleRate);
        if (startFrame >= totalFrames) yield break;
        endFrame = Math.Min(endFrame, totalFrames);
        long startTick = _map.SampleFrameToTick(
            startFrame,
            _compiled.StartTick,
            _sampleRate,
            _compiled.EndTick);
        long endTick = _map.SampleFrameToTick(
            endFrame,
            _compiled.StartTick,
            _sampleRate,
            _compiled.EndTick);
        if (endTick < _compiled.EndTick) endTick++;
        if (endTick <= startTick) endTick = Math.Min(_compiled.EndTick, startTick + 1);
        if (endTick <= startTick) yield break;

        IReadOnlySet<MidoraId>? demandedSourceIds = null;
        if (demand is not null)
        {
            HashSet<MidoraId> selected = [];
            foreach ((MidoraId sourceId, int sourceIndex) in _sourceIndices)
            {
                if (demand.MayDemandSource(sourceIndex, startFrame, endFrame))
                {
                    selected.Add(sourceId);
                }
            }
            if (selected.Count == 0)
            {
                yield break;
            }
            demandedSourceIds = selected;
        }

        IEnumerable<CanonicalMidiRenderEventPage> pages = demandedSourceIds is null
            ? _compiled.QueryMidiRenderEventPages(
                startTick,
                endTick,
                includeStateAtStart: startFrame == 0,
                cancellationToken)
            : _compiled.QueryMidiRenderEventPages(
                startTick,
                endTick,
                includeStateAtStart: startFrame == 0,
                demandedSourceIds,
                cancellationToken);
        foreach (CanonicalMidiRenderEventPage page in pages)
        {
            foreach (CanonicalMidiRenderEvent value in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MidiRenderPlanAdapter.IsSupportedByInitialReleaseAudioProjection(value.Message))
                    continue;
                if (_audibleTrackIds is not null
                    && value.TrackId != default
                    && !_audibleTrackIds.Contains(value.TrackId))
                {
                    continue;
                }
                long frame = _map.TickToSampleFrame(
                    value.Tick,
                    _compiled.StartTick,
                    _sampleRate);
                if (frame < startFrame || frame >= endFrame) continue;
                int sourceIndex = _sourceIndices[value.MonitoringSourceId];
                if (demand is not null
                    && !demand.IsSourceDemanded(sourceIndex, frame))
                {
                    continue;
                }
                yield return new(
                    value.ZeroBasedPort,
                    new(frame, value.Message, sourceIndex));
            }
        }
    }
}
