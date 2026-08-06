using Midora.Domain;
using Midora.Mapping.Contract.V1;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class CompilationTests
{
    [Fact]
    public void LogicalNoteCompilesWithTranspositionAndExactBoundaryCleanup()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 480, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480, 64);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        CanonicalMidiEvent noteOn = Assert.Single(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal(0, noteOn.Tick);
        Assert.Equal((byte)64, noteOn.Message.Byte1);
        Assert.Equal((byte)100, noteOn.Message.Byte2);
        CanonicalMidiEvent noteOff = Assert.Single(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOff);
        Assert.Equal(480, noteOff.Tick);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 480 && value.Role == CanonicalEventRole.Reset);
    }

    [Fact]
    public void SameTickNoteOffPrecedesNoteOn()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 960);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 240, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 240, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        CanonicalMidiEvent[] atTick = result.Events.ToArray()
            .Where(value => value.Tick == 240 && value.Message.Byte1 == 60
                && value.Message.MessageType is MidiMessageType.NoteOn or MidiMessageType.NoteOff)
            .ToArray();

        Assert.True(atTick.Length >= 2);
        Assert.Equal(CanonicalEventRole.NoteOff, atTick[0].Role);
        Assert.Equal(CanonicalEventRole.NoteOn, atTick[^1].Role);
    }

    [Fact]
    public void BankSelectMayContainOnlyLsb()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Voice.Events.Add(TemplateEvent.Bank(fixture.Project, 0, null, 7));
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        CanonicalMidiEvent[] bank = result.Events.ToArray()
            .Where(value => value.Role == CanonicalEventRole.Bank)
            .ToArray();

        CanonicalMidiEvent lsb = Assert.Single(bank);
        Assert.Equal((byte)32, lsb.Message.Byte1);
        Assert.Equal((byte)7, lsb.Message.Byte2);
    }

    [Fact]
    public void RangeStartRestoresStateWithoutRetriggeringEarlierNote()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 960);
        fixture.Voice.InitialState.Program = 12;
        fixture.Instrument.TemplateLengthTicks = 800;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 800, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 900);
        CompilationRequest request = new() { Purpose = CompilationPurpose.Range, StartTick = 240, EndTick = 720 };

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project, request);

        Assert.False(result.IsPartial);
        Assert.DoesNotContain(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 240
            && value.Message.MessageType == MidiMessageType.ProgramChange && value.Message.Byte1 == 12);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 720 && value.Role == CanonicalEventRole.NoteOff);
    }

    [Fact]
    public void RangeStartRestoresEveryRpnTransactionWithNullFunction()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 960);
        fixture.Voice.Events.Add(new TemplateEvent(fixture.Project)
        {
            Kind = TemplateEventKind.RegisteredParameter,
            Tick = 0,
            Number = 1,
            Value = 100
        });
        fixture.Voice.Events.Add(new TemplateEvent(fixture.Project)
        {
            Kind = TemplateEventKind.RegisteredParameter,
            Tick = 120,
            Number = 2,
            Value = 200
        });
        fixture.Instrument.TemplateLengthTicks = 800;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 800, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 900);
        fixture.Project.GlobalResetDefaults.RegisteredParameters[1] = 321;

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project, new CompilationRequest
        {
            Purpose = CompilationPurpose.Range,
            StartTick = 300,
            EndTick = 700
        });

        CanonicalMidiEvent[] restores = result.Events.ToArray()
            .Where(value => value.Tick == 300 && value.Role == CanonicalEventRole.RangeRestore)
            .ToArray();
        CanonicalMidiEvent[][] groups = restores.GroupBy(value => value.SemanticTargetKey)
            .Select(value => value.ToArray())
            .Where(group => group.Any(value => value.Message.Byte1 == 38
                && value.Message.Byte2 is 100 or 72))
            .ToArray();
        Assert.Equal(2, groups.Length);
        Assert.All(groups, group =>
        {
            Assert.Equal(6, group.Length);
            Assert.Contains(group, value => value.Message.Byte1 == 101 && value.Message.Byte2 == 127);
            Assert.Contains(group, value => value.Message.Byte1 == 100 && value.Message.Byte2 == 127);
        });
        Assert.Contains(restores, value => value.Message.Byte1 == 38 && value.Message.Byte2 == 100);
        Assert.Contains(restores, value => value.Message.Byte1 == 38 && value.Message.Byte2 == 72);
        CanonicalMidiEvent[] parameterOneReset = Assert.Single(result.Events.ToArray()
            .Where(value => value.Tick == 700 && value.Role == CanonicalEventRole.Reset)
            .GroupBy(value => value.SemanticTargetKey)
            .Select(value => value.ToArray()),
            group => group.Any(value => value.Message.Byte1 == 100 && value.Message.Byte2 == 1));
        Assert.Equal(6, parameterOneReset.Length);
        Assert.Contains(parameterOneReset, value => value.Message.Byte1 == 6 && value.Message.Byte2 == 2);
        Assert.Contains(parameterOneReset, value => value.Message.Byte1 == 38 && value.Message.Byte2 == 65);
    }

    [Fact]
    public void RangeEndReplacesFilteredResetWhenAllocationEndsExactlyAtBoundary()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 0, 1, 91));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project, new CompilationRequest
        {
            Purpose = CompilationPurpose.Range,
            StartTick = 0,
            EndTick = 480
        });

        Assert.True(result.IsConsumable);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 480
            && value.Role == CanonicalEventRole.Reset
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 1);
    }

    [Fact]
    public void FullAndIncrementalResultsAreFormallyEqual()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 240, 60, 100));
        LogicalNote note = CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);
        MidoraCompiler incrementalCompiler = new();
        _ = incrementalCompiler.CompileFull(fixture.Project);
        note.Note = 67;
        ProjectChangeSet change = new();
        change.TrackIds.Add(fixture.Track.Id);

        CanonicalCompiledResult incremental = incrementalCompiler.CompileIncremental(fixture.Project, change);
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.Equal(full.Fingerprint, incremental.Fingerprint);
        Assert.Equal(full.Events.ToArray(), incremental.Events.ToArray());
        Assert.Equal(full.Allocations.ToArray(), incremental.Allocations.ToArray());
        Assert.Equal(full.Statistics, incremental.Statistics);
        Assert.Equal(1, incrementalCompiler.LastTelemetry.RecompiledTrackCount);
    }

    [Fact]
    public void RepeatedFullCompileProducesIdenticalCanonicalForm()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Project.GlobalInitialState.Controllers.Add(11, 100);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 240, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);

        CanonicalCompiledResult first = new MidoraCompiler().CompileFull(fixture.Project);
        CanonicalCompiledResult second = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Events.ToArray(), second.Events.ToArray());
        Assert.Equal(first.Allocations.ToArray(), second.Allocations.ToArray());
        Assert.Equal(first.Conductor.Tempos, second.Conductor.Tempos);
        Assert.Equal(first.Conductor.TimeSignatures, second.Conductor.TimeSignatures);
    }

    [Fact]
    public void DictionaryInsertionOrderDoesNotAffectCanonicalForm()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Project.GlobalInitialState.Controllers.Add(11, 100);
        fixture.Project.GlobalInitialState.Controllers.Add(1, 64);
        fixture.Project.GlobalInitialState.RegisteredParameters.Add(0x0100, 8_192);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 240, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);
        CanonicalCompiledResult first = new MidoraCompiler().CompileFull(fixture.Project);

        fixture.Project.GlobalInitialState.Controllers.Clear();
        fixture.Project.GlobalInitialState.Controllers.Add(1, 64);
        fixture.Project.GlobalInitialState.Controllers.Add(11, 100);
        CanonicalCompiledResult second = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Events.ToArray(), second.Events.ToArray());
        Assert.Equal(first.Allocations.ToArray(), second.Allocations.ToArray());
    }

    [Fact]
    public void UnchangedTrackFragmentIsReused()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 240, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);
        MidoraCompiler compiler = new();
        _ = compiler.CompileFull(fixture.Project);

        CanonicalCompiledResult result = compiler.CompileIncremental(fixture.Project, new ProjectChangeSet());

        Assert.Equal(0, compiler.LastTelemetry.RecompiledTrackCount);
        Assert.Equal(1, compiler.LastTelemetry.ReusedTrackCount);
    }

    [Fact]
    public void AllocatorUsesPortOneChannelTenAsMelodicUnit()
    {
        var fixture = CompilerTestProject.Create(10);
        foreach (SubVoice voice in fixture.Instrument.SubVoices)
        {
            voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        }
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.Contains(result.Allocations.ToArray(), value => value.ZeroBasedPort == 0 && value.ZeroBasedChannel == 9);
    }

    [Fact]
    public void IncrementalCacheReplaysTrackExpansionDiagnostics()
    {
        var fixture = CompilerTestProject.Create();
        CSharpMappingFunction function = new(fixture.Project) { Name = "throws", Body = "throw new InvalidOperationException();" };
        fixture.Instrument.MappingFunctions.Add(function);
        TemplateEvent value = TemplateEvent.ControlChange(fixture.Project, 0, 1, 20);
        value.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        });
        fixture.Voice.Events.Add(value);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        MidoraCompiler compiler = new();

        CanonicalCompiledResult full = compiler.CompileFull(fixture.Project);
        CanonicalCompiledResult incremental = compiler.CompileIncremental(fixture.Project, new ProjectChangeSet());

        Assert.False(full.IsConsumable);
        Assert.False(incremental.IsConsumable);
        Assert.Contains(full.Diagnostics, item => item.Code == "MIDORA2101");
        Assert.Contains(incremental.Diagnostics, item => item.Code == "MIDORA2101");
        Assert.Equal(1, compiler.LastTelemetry.ReusedTrackCount);
    }

    [Fact]
    public void EmptySubVoiceInstanceConsumesChannelUnitAndReportsInfo()
    {
        var fixture = CompilerTestProject.Create();
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.DoesNotContain(result.Events.ToArray(), value =>
            value.Role is CanonicalEventRole.NoteOn or CanonicalEventRole.NoteOff);
        Assert.Single(result.Allocations.ToArray());
        Assert.Equal(1, result.Statistics.PeakChannelUnitCount);
        Assert.Contains(result.Diagnostics, value =>
            value.Code == "MIDORA1225"
            && value.Severity == DiagnosticSeverity.Info
            && value.Source.SubVoiceId == fixture.Voice.Id);
    }

    [Fact]
    public void RangeConductorRestoresStateAndFiltersMarkers()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 1_000);
        fixture.Project.Conductor.Tempos.Add(new TempoChange(fixture.Project, 100, 90m));
        fixture.Project.Conductor.Tempos.Add(new TempoChange(fixture.Project, 500, 140m));
        fixture.Project.Conductor.TimeSignatures.Add(new TimeSignatureChange(fixture.Project, 120, 3, 4));
        fixture.Project.Conductor.KeySignatures.Add(new KeySignatureChange(fixture.Project, 160, 2, false));
        fixture.Project.Conductor.Markers.Add(new ProjectMarker(fixture.Project, 200, "before"));
        fixture.Project.Conductor.Markers.Add(new ProjectMarker(fixture.Project, 400, "inside"));
        CompilationRequest request = new() { Purpose = CompilationPurpose.Range, StartTick = 300, EndTick = 700 };

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project, request);

        Assert.Equal(300, result.Conductor.Tempos[0].Tick);
        Assert.Equal(90m, result.Conductor.Tempos[0].BeatsPerMinute);
        Assert.True(result.Conductor.Tempos[0].IsRangeRestore);
        Assert.Contains(result.Conductor.Tempos.ToArray(), value => value.Tick == 500 && !value.IsRangeRestore);
        Assert.Single(result.Conductor.Markers.ToArray());
        Assert.Equal("inside", result.Conductor.Markers[0].Name);
        Assert.True(result.Conductor.TimeSignatures[0].IsRangeRestore);
        Assert.True(result.Conductor.KeySignatures[0].IsRangeRestore);
    }

    [Fact]
    public void ConductorChangeChangesCanonicalFingerprint()
    {
        var fixture = CompilerTestProject.Create();
        MidoraCompiler compiler = new();
        CompilationRequest range = new() { EndTick = 480 };
        CanonicalCompiledResult before = compiler.CompileFull(fixture.Project, range);
        fixture.Project.Conductor.Tempos.Add(new TempoChange(fixture.Project, 120, 100m));

        CanonicalCompiledResult after = compiler.CompileIncremental(
            fixture.Project, new ProjectChangeSet { AffectsConductor = true }, range);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.Equal(1, compiler.LastTelemetry.ReusedTrackCount);
    }

    [Fact]
    public void SourceIdentityChangeInvalidatesCanonicalFingerprintUsedByMonitoringCache()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        MidoraCompiler compiler = new();
        CanonicalCompiledResult before = compiler.CompileFull(fixture.Project);
        LogicalTrack replacement = new(fixture.Project)
        {
            Name = fixture.Track.Name,
            EventInstrumentId = fixture.Track.EventInstrumentId
        };
        replacement.Segments.Add(fixture.Segment);
        fixture.Project.Tracks[0] = replacement;

        CanonicalCompiledResult after = compiler.CompileIncremental(fixture.Project, ProjectChangeSet.Everything);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.All(after.Events.ToArray().Where(value => value.Source.TrackId != default),
            value => Assert.Equal(replacement.Id, value.Source.TrackId));
    }

    [Fact]
    public void MappingContextNameChangeInvalidatesIncrementalTrackFragment()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Instrument.RequiresChannelIsolation = true;
        CSharpMappingFunction function = new(fixture.Project)
        {
            Name = "name-sensitive",
            Body = "return context.EventInstrumentName == \"Renamed\" ? 100 : 20;"
        };
        function.DeclaredContextFields.Add(nameof(MappingContextV1.EventInstrumentName));
        fixture.Instrument.MappingFunctions.Add(function);
        TemplateEvent note = TemplateEvent.Note(fixture.Project, 0, 120, 60, 80);
        note.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        });
        fixture.Voice.Events.Add(note);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        MidoraCompiler compiler = new();
        CanonicalCompiledResult before = compiler.CompileFull(fixture.Project);

        fixture.Instrument.Name = "Renamed";
        CanonicalCompiledResult after = compiler.CompileIncremental(fixture.Project, new ProjectChangeSet());

        Assert.Contains(before.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn
            && value.Message.Byte2 == 20);
        Assert.Contains(after.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn
            && value.Message.Byte2 == 100);
        Assert.Equal(1, compiler.LastTelemetry.RecompiledTrackCount);
    }

    [Fact]
    public void OverlappingNotesShareChannelGroupWhenIsolationIsDisabled()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 0, 1, 64));
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 240, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480, 60);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 120, 480, 64);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.Equal(1, result.Statistics.PeakChannelUnitCount);
        ChannelUnitAllocation[] allocations = result.Allocations.ToArray();
        Assert.Equal(2, allocations.Length);
        Assert.Single(allocations.Select(value => value.InstanceGroupId).Distinct());
        Assert.Single(allocations.Select(value => (value.ZeroBasedPort, value.ZeroBasedChannel)).Distinct());
        Assert.Equal(2, result.Events.ToArray().Count(value => value.Role == CanonicalEventRole.NoteOn));
    }

    [Theory]
    [InlineData(OverlapPolicy.Reject, false, DiagnosticSeverity.Error, false)]
    [InlineData(OverlapPolicy.Warn, false, DiagnosticSeverity.Warning, true)]
    [InlineData(OverlapPolicy.Warn, true, DiagnosticSeverity.Warning, false)]
    public void RejectAndWarnOverlapHaveFixedSeverityAndConsumability(
        OverlapPolicy policy,
        bool treatWarningsAsErrors,
        DiagnosticSeverity expectedSeverity,
        bool expectedConsumable)
    {
        var fixture = CompilerTestProject.Create();
        fixture.Instrument.OverlapPolicy = policy;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 480, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 120, 480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project, new CompilationRequest
        {
            TreatWarningsAsErrors = treatWarningsAsErrors
        });

        Assert.Equal(expectedConsumable, result.IsConsumable);
        CompilerDiagnostic diagnostic = Assert.Single(result.Diagnostics, value => value.Code == "MIDORA2201");
        Assert.Equal(expectedSeverity, diagnostic.Severity);
    }
}
