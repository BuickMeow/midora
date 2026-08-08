using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class PreviewCompilerTests
{
    [Fact]
    public void EventInstrumentPreviewUsesTemporaryContextAndCursorTempo()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 960);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 240, 60, 100));
        fixture.Project.Conductor.Tempos.Add(new TempoChange(fixture.Project, 480, 90m));
        int trackCount = fixture.Project.Tracks.Count;
        int segmentCount = fixture.Track.Segments.Count;

        CanonicalCompiledResult result = new PreviewCompiler().CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(
                fixture.Instrument.Id,
                Pitch: 67,
                Velocity: 111,
                GateLengthTicks: 360,
                CursorTick: 600));

        Assert.True(result.IsConsumable);
        Assert.False(result.IsPartial);
        Assert.Equal(CompilationPurpose.EventInstrumentPreview, result.Purpose);
        Assert.Equal(90m, result.Conductor.Tempos[0].BeatsPerMinute);
        CanonicalMidiEvent noteOn = Assert.Single(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal(67, noteOn.Message.Byte1);
        Assert.Equal(100, noteOn.Message.Byte2); // Template velocity remains the mapping target value.
        Assert.Equal(trackCount, fixture.Project.Tracks.Count);
        Assert.Equal(segmentCount, fixture.Track.Segments.Count);
        Assert.Empty(fixture.Segment.Notes);
    }

    [Fact]
    public void SubVoicePreviewKeepsInstrumentContextButFiltersOutput()
    {
        var fixture = CompilerTestProject.Create(subVoiceCount: 2);
        fixture.Instrument.SubVoices[0].Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        fixture.Instrument.SubVoices[1].Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 72, 90));
        SubVoice selected = fixture.Instrument.SubVoices[1];

        CanonicalCompiledResult result = new PreviewCompiler().CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id, selected.Id));

        Assert.True(result.IsConsumable);
        CanonicalMidiEvent note = Assert.Single(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal(72, note.Message.Byte1);
        Assert.Single(result.Allocations.ToArray());
        Assert.Equal(selected.Id, result.Allocations[0].SubVoiceId);
    }

    [Fact]
    public void EventInstrumentPreviewAppliesMultipleSoloWithMuteTakingPriority()
    {
        var fixture = CompilerTestProject.Create(subVoiceCount: 4);
        for (int i = 0; i < fixture.Instrument.SubVoices.Count; i++)
        {
            fixture.Instrument.SubVoices[i].Events.Add(
                TemplateEvent.Note(fixture.Project, 0, 120, 60 + i, 100));
        }
        SubVoice first = fixture.Instrument.SubVoices[0];
        SubVoice mutedSolo = fixture.Instrument.SubVoices[1];
        SubVoice second = fixture.Instrument.SubVoices[2];

        CanonicalCompiledResult result = new PreviewCompiler().CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id)
            {
                SoloSubVoiceIds = [first.Id, mutedSolo.Id, second.Id],
                MutedSubVoiceIds = [mutedSolo.Id]
            });

        Assert.True(result.IsConsumable, string.Join(" | ", result.Diagnostics.Select(value => $"{value.Code}:{value.Message}")));
        Assert.Equal(
            [first.Id, second.Id],
            result.Allocations.ToArray().Select(value => value.SubVoiceId).Order().ToArray());
        Assert.Equal(
            [60, 62],
            result.Events.ToArray()
                .Where(value => value.Role == CanonicalEventRole.NoteOn)
                .Select(value => (int)value.Message.Byte1)
                .Order()
                .ToArray());
    }

    [Fact]
    public void EventInstrumentPreviewWithoutSoloExcludesEveryMutedSubVoiceIncludingAllMuted()
    {
        var fixture = CompilerTestProject.Create(subVoiceCount: 2);
        foreach (SubVoice voice in fixture.Instrument.SubVoices)
        {
            voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        }

        CanonicalCompiledResult oneAudible = new PreviewCompiler().CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id)
            {
                MutedSubVoiceIds = [fixture.Instrument.SubVoices[0].Id],
                SoloSubVoiceIds = []
            });
        CanonicalCompiledResult allMuted = new PreviewCompiler().CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id)
            {
                MutedSubVoiceIds = fixture.Instrument.SubVoices.Select(value => value.Id).ToArray()
            });

        Assert.True(oneAudible.IsConsumable);
        Assert.Single(oneAudible.Allocations.ToArray());
        Assert.Equal(fixture.Instrument.SubVoices[1].Id, oneAudible.Allocations[0].SubVoiceId);
        Assert.True(allMuted.IsConsumable);
        Assert.Empty(allMuted.Allocations.ToArray());
        Assert.DoesNotContain(allMuted.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn);
    }

    [Fact]
    public void EventInstrumentPreviewRejectsInvalidOrAmbiguousSubVoiceListeningState()
    {
        var fixture = CompilerTestProject.Create(subVoiceCount: 2);
        MidoraId firstId = fixture.Instrument.SubVoices[0].Id;
        MidoraId foreignId = fixture.Project.AllocateStableId();
        PreviewCompiler compiler = new();

        Assert.Throws<ArgumentException>(() => compiler.CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id)
            {
                MutedSubVoiceIds = [firstId, firstId]
            }));
        Assert.Throws<ArgumentException>(() => compiler.CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id)
            {
                SoloSubVoiceIds = [foreignId]
            }));
        Assert.Throws<ArgumentException>(() => compiler.CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id, firstId)
            {
                MutedSubVoiceIds = []
            }));
    }

    [Fact]
    public void SegmentPreviewUsesOnlySelectedSegmentAndProjectConductorRange()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Segment.ProjectStartTick = 480;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120, 64);
        Segment other = new(fixture.Project) { ProjectStartTick = 1_200, LengthTicks = 480 };
        CompilerTestProject.RegisterSegment(fixture.Project, other);
        CompilerTestProject.AddNote(other, fixture.Instrument, 0, 120, 72);
        fixture.Track.Segments.Add(other);
        fixture.Project.Conductor.Tempos.Add(new TempoChange(fixture.Project, 240, 90m));
        fixture.Project.Conductor.Tempos.Add(new TempoChange(fixture.Project, 720, 140m));

        CanonicalCompiledResult result = new PreviewCompiler().CompileSegment(
            fixture.Project, fixture.Track.Id, fixture.Segment.Id);

        Assert.True(result.IsConsumable);
        Assert.Equal(CompilationPurpose.SegmentPreview, result.Purpose);
        Assert.Equal(480, result.StartTick);
        Assert.Equal(960, result.EndTick);
        Assert.Equal(90m, result.Conductor.Tempos[0].BeatsPerMinute);
        Assert.True(result.Conductor.Tempos[0].IsRangeRestore);
        Assert.Contains(result.Conductor.Tempos.ToArray(), value => value.Tick == 720 && value.BeatsPerMinute == 140m);
        CanonicalMidiEvent note = Assert.Single(result.Events.ToArray(), value => value.Message.MessageType == MidiMessageType.NoteOn);
        Assert.Equal(64, note.Message.Byte1);
        Assert.DoesNotContain(result.Events.ToArray(), value => value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte1 == 72);
    }

    [Fact]
    public void SegmentPreviewPreservesDamagedInstrumentBindingError()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120, 64);
        _ = fixture.Project.EventInstruments.Remove(fixture.Instrument);
        fixture.Project.DamagedEventInstruments.Add(new(
            fixture.Instrument.Id,
            fixture.Instrument.Name,
            "event-instruments/damaged.pb",
            "Invalid protobuf",
            0));

        CanonicalCompiledResult result = new PreviewCompiler().CompileSegment(
            fixture.Project,
            fixture.Track.Id,
            fixture.Segment.Id);

        Assert.False(result.IsConsumable);
        Assert.True(result.IsPartial);
        CompilerDiagnostic diagnostic = Assert.Single(result.Diagnostics, value =>
            value.Code == "MIDORA1305");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(fixture.Track.Id, diagnostic.Source.TrackId);
        Assert.Equal(fixture.Instrument.Id, diagnostic.Source.EventInstrumentId);
        Assert.DoesNotContain(result.Diagnostics, value => value.Code == "MIDORA1303");
    }

    [Fact]
    public void PreviewContextsPreserveSelectedInstrumentLibraryFolderIdentity()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        EventInstrumentLibraryFolder folder = new(fixture.Project) { Name = "Folder" };
        fixture.Project.EventInstrumentFolders.Add(folder);
        fixture.Instrument.LibraryFolderId = folder.Id;
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120, 64);

        CanonicalCompiledResult instrumentPreview = new PreviewCompiler().CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id));
        CanonicalCompiledResult segmentPreview = new PreviewCompiler().CompileSegment(
            fixture.Project,
            fixture.Track.Id,
            fixture.Segment.Id);

        Assert.True(instrumentPreview.IsConsumable);
        Assert.True(segmentPreview.IsConsumable);
        Assert.DoesNotContain(instrumentPreview.Diagnostics, value => value.Code == "MIDORA1021");
        Assert.DoesNotContain(segmentPreview.Diagnostics, value => value.Code == "MIDORA1021");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SegmentPreviewRejectsUnboundAndBrokenInstrumentBindings(bool brokenReference)
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Track.EventInstrumentId = brokenReference
            ? fixture.Project.AllocateStableId()
            : null;

        CanonicalCompiledResult result = new PreviewCompiler().CompileSegment(
            fixture.Project,
            fixture.Track.Id,
            fixture.Segment.Id);

        Assert.False(result.IsConsumable);
        Assert.True(result.IsPartial);
        CompilerDiagnostic diagnostic = Assert.Single(result.Diagnostics, value =>
            value.Code == "MIDORA1306");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(fixture.Track.Id, diagnostic.Source.TrackId);
        Assert.Equal(
            brokenReference,
            result.Diagnostics.Any(value => value.Code == "MIDORA1303"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EventInstrumentPreviewClampsExtremeContainerLength(bool extremeGate)
    {
        var fixture = CompilerTestProject.Create();
        long gateLength = extremeGate ? long.MaxValue : fixture.Instrument.TemplateLengthTicks;
        if (!extremeGate)
        {
            fixture.Instrument.Envelopes.Add(new InstrumentEnvelope(fixture.Project)
            {
                ReleaseTicks = long.MaxValue
            });
        }

        CanonicalCompiledResult result = new PreviewCompiler().CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(
                fixture.Instrument.Id,
                GateLengthTicks: gateLength));

        Assert.True(result.IsConsumable);
        Assert.Equal(long.MaxValue, result.EndTick);
        Assert.DoesNotContain(result.Diagnostics, value => value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void HeldPreviewGateOpenUsesMaximumGateSentinelAndDoesNotCleanAtWindowEnd()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.TemplateLengthTicks = 2_000;
        TemplateEvent control = TemplateEvent.ControlChange(fixture.Project, 0, 11, 1);
        control.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.GateLength,
            Operation = MappingOperation.Override
        });
        control.ValueTargetSettings.Overflow = MappingOverflow.Clamp;
        fixture.Voice.Events.Add(control);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 2_000, 60, 100));

        CanonicalCompiledResult result = new PreviewCompiler().CompileHeldEventInstrumentGateOpen(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id),
            windowEndTick: 600);

        Assert.True(result.IsConsumable, string.Join(" | ", result.Diagnostics.Select(value => $"{value.Code}:{value.Message}")));
        Assert.True(result.Context.IsPreview);
        CanonicalMidiEvent mapped = Assert.Single(result.Events.ToArray(), value =>
            value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11
            && value.Role == CanonicalEventRole.ControlChange);
        Assert.Equal(127, mapped.Message.Byte2);
        Assert.Contains(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn);
        Assert.DoesNotContain(result.Events.ToArray(), value =>
            value.Tick == 600
            || value.Source.Origin == SourceOrigin.CompilerBoundaryCleanup);
        Assert.DoesNotContain(result.Events.ToArray(), value =>
            value.Message.MessageType is MidiMessageType.NoteOff
            || value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte2 == 0);
    }

    [Fact]
    public void HeldPreviewGateOpenBoundsLoopExpansionToTheRequestedCausalWindow()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.TemplateLengthTicks = 300;
        fixture.Instrument.LoopStartTick = 100;
        fixture.Instrument.LoopEndTick = 200;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 120, 10, 60, 100));

        CanonicalCompiledResult result = new PreviewCompiler().CompileHeldEventInstrumentGateOpen(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id),
            windowEndTick: 450);

        Assert.True(result.IsConsumable, string.Join(" | ", result.Diagnostics.Select(value => $"{value.Code}:{value.Message}")));
        Assert.Equal(
            [120L, 220L, 320L, 420L],
            result.Events.ToArray()
                .Where(value => value.Role == CanonicalEventRole.NoteOn)
                .Select(value => value.Tick)
                .ToArray());
        Assert.DoesNotContain(result.Events.ToArray(), value => value.Tick >= 450);
    }

    [Fact]
    public void HeldPreviewGateEndSeparatesFrozenMappingLengthFromTheCausalReleaseTick()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.TemplateLengthTicks = 1_000;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.CutAtNoteOff;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 1_000, 60, 100));
        TemplateEvent control = TemplateEvent.ControlChange(fixture.Project, 400, 11, 1);
        control.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.GateLength,
            Operation = MappingOperation.Override
        });
        fixture.Voice.Events.Add(control);

        CanonicalCompiledResult result = new PreviewCompiler().CompileHeldEventInstrumentGateEnd(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id),
            finalGateLengthTicks: 120,
            effectiveGateEndTick: 480);

        Assert.True(result.IsConsumable, string.Join(" | ", result.Diagnostics.Select(value => $"{value.Code}:{value.Message}")));
        CanonicalMidiEvent mapped = Assert.Single(result.Events.ToArray(), value =>
            value.Tick == 400
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11
            && value.Role == CanonicalEventRole.ControlChange);
        Assert.Equal(120, mapped.Message.Byte2);
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == 480
            && value.Message.MessageType is MidiMessageType.NoteOff);
    }

    [Fact]
    public void HeldDraftNotePreviewUsesSegmentProjectPositionAndParameterStateWithoutMutation()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 1_000);
        fixture.Segment.ProjectStartTick = 1_000;
        fixture.Segment.ContentOffsetTick = 200;
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.TemplateLengthTicks = 1_000;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 1_000, 60, 100));
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "expression",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(11),
            Steps =
            {
                new ValueMappingStep(fixture.Project)
                {
                    Operation = MappingOperation.Remap,
                    Source = MappingSource.LogicalParameter,
                    LogicalParameterId = parameter.Id,
                    SourceMinimum = 0,
                    SourceMaximum = 1,
                    TargetMinimum = 0,
                    TargetMaximum = 127
                }
            }
        };
        fixture.Instrument.ParameterMappings.Add(mapping);
        LogicalParameterLane lane = new(fixture.Project) { ParameterId = parameter.Id };
        lane.Points.Add(new(fixture.Project, 200, 0, CurveInterpolation.Step));
        lane.Points.Add(new(fixture.Project, 320, 1, CurveInterpolation.Step));
        fixture.Segment.ParameterLanes.Add(lane);
        SegmentNotePreviewRequest request = new(
            fixture.Track.Id,
            fixture.Segment.Id,
            StartTick: 320,
            Pitch: 67,
            Velocity: 111);
        int originalNoteCount = fixture.Segment.Notes.Count;
        PreviewCompiler preview = new();

        CanonicalCompiledResult open = preview.CompileHeldSegmentNoteGateOpen(
            fixture.Project,
            request,
            windowLengthTicks: 480);
        CanonicalCompiledResult ended = preview.CompileHeldSegmentNoteGateEnd(
            fixture.Project,
            request,
            finalGateLengthTicks: 120,
            effectiveGateEndTick: 240);

        Assert.True(open.IsConsumable, string.Join(" | ", open.Diagnostics.Select(value => $"{value.Code}:{value.Message}")));
        Assert.Equal(1_120, open.StartTick);
        Assert.Equal(1_600, open.EndTick);
        Assert.Contains(open.Events.ToArray(), value =>
            value.Tick == 1_120
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 127);
        Assert.Contains(open.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.NoteOn && value.Message.Byte1 == 67);
        Assert.True(ended.IsConsumable, string.Join(" | ", ended.Diagnostics.Select(value => $"{value.Code}:{value.Message}")));
        Assert.Contains(ended.Events.ToArray(), value =>
            value.Tick == 1_360
            && value.Message.MessageType == MidiMessageType.NoteOff);
        Assert.Equal(originalNoteCount, fixture.Segment.Notes.Count);
    }

    [Fact]
    public void HeldPreviewGateStartRejectsAClaimedFinalLengthAndInvalidDirectCompilerContext()
    {
        var fixture = CompilerTestProject.Create();
        PreviewCompiler preview = new();

        Assert.Throws<ArgumentException>(() => preview.CompileHeldEventInstrumentGateOpen(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id, GateLengthTicks: 120),
            480));
        using MidoraCompiler compiler = new();
        Assert.Throws<ArgumentException>(() => compiler.CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.Playback,
                EndTick = 480,
                HeldPreviewGateOpen = true
            }));
        Assert.Throws<ArgumentException>(() => compiler.CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.Playback,
                EndTick = 480,
                HeldPreviewFinalGateLengthTicks = 120
            }));
    }
}
