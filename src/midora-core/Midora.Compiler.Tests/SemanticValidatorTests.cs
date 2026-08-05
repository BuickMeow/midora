using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class SemanticValidatorTests
{
    [Fact]
    public void BrokenEnvelopeReferenceIsAnError()
    {
        var fixture = CompilerTestProject.Create();
        LogicalParameterDefinition parameter = new()
        {
            Name = "value",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping mapping = new()
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        mapping.Steps.Add(new ValueMappingStep
        {
            Source = MappingSource.Envelope,
            EnvelopeId = MidoraId.New()
        });
        fixture.Instrument.ParameterMappings.Add(mapping);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1233");
    }

    [Fact]
    public void DisabledStepDoesNotActivateBrokenFunctionReference()
    {
        var fixture = CompilerTestProject.Create();
        TemplateEvent controller = TemplateEvent.ControlChange(0, 1, 20);
        controller.ValueMappings.Add(new ValueMappingStep
        {
            IsEnabled = false,
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = MidoraId.New()
        });
        fixture.Voice.Events.Add(controller);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 20);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.DoesNotContain(result.Diagnostics, value => value.Code is "MIDORA1234" or "MIDORA1271");
    }

    [Fact]
    public void InvalidUnreferencedMappingFunctionIsWarningOnly()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Instrument.MappingFunctions.Add(new CSharpMappingFunction
        {
            Name = "unused invalid",
            Body = "return ;"
        });

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA2104"
            && value.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void InvalidUnusedInstrumentDoesNotBlockWholeProjectCompilation()
    {
        var fixture = CompilerTestProject.Create();
        EventInstrument unused = new()
        {
            Name = "Unused",
            RootNote = 200,
            TemplateLengthTicks = -1,
            LoopStartTick = 20,
            LoopEndTick = 10
        };
        fixture.Project.EventInstruments.Add(unused);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Source.EventInstrumentId == unused.Id
            && value.Severity == DiagnosticSeverity.Warning);
        Assert.DoesNotContain(result.Diagnostics, value => value.Source.EventInstrumentId == unused.Id
            && value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void EventInstrumentPreviewTreatsSelectedDefinitionAsParticipating()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Instrument.RootNote = 200;

        CanonicalCompiledResult result = new PreviewCompiler().CompileEventInstrument(
            fixture.Project,
            new EventInstrumentPreviewRequest(fixture.Instrument.Id, Pitch: 60, GateLengthTicks: 120));

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1210"
            && value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ForbiddenReverbControllerIsAnError()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(0, 91, 64));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1242" && value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ExplicitNoteNumberMappingRequiresPerNoteIsolation()
    {
        var fixture = CompilerTestProject.Create();
        TemplateEvent note = TemplateEvent.Note(0, 120, 60, 100);
        note.NumberMappings.Add(new ValueMappingStep
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Add,
            Constant = 0
        });
        fixture.Voice.Events.Add(note);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1214");
    }

    [Fact]
    public void ChannelUnitThresholdIsInfoAndHardLimitIsError()
    {
        var near = CompilerTestProject.Create(248);
        foreach (SubVoice voice in near.Instrument.SubVoices)
        {
            voice.Events.Add(TemplateEvent.Note(0, 120, 60, 100));
        }
        CompilerTestProject.AddNote(near.Segment, near.Instrument, 0, 480);
        CanonicalCompiledResult nearResult = new MidoraCompiler().CompileFull(near.Project);
        Assert.True(nearResult.IsConsumable);
        Assert.Contains(nearResult.Diagnostics, value => value.Code == "MIDORA2250" && value.Severity == DiagnosticSeverity.Info);

        var over = CompilerTestProject.Create(256);
        EventInstrument second = new()
        {
            Name = "Second",
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        second.SubVoices.Add(new SubVoice());
        over.Project.EventInstruments.Add(second);
        foreach (SubVoice voice in over.Instrument.SubVoices)
        {
            voice.Events.Add(TemplateEvent.Note(0, 120, 60, 100));
        }
        second.SubVoices[0].Events.Add(TemplateEvent.Note(0, 120, 60, 100));
        CompilerTestProject.AddNote(over.Segment, over.Instrument, 0, 480);
        LogicalTrack secondTrack = new() { Name = "Second", EventInstrumentId = second.Id };
        Segment secondSegment = new() { LengthTicks = 1_920 };
        CompilerTestProject.AddNote(secondSegment, second, 0, 480);
        secondTrack.Segments.Add(secondSegment);
        over.Project.Tracks.Add(secondTrack);
        CanonicalCompiledResult overResult = new MidoraCompiler().CompileFull(over.Project);
        Assert.False(overResult.IsConsumable);
        Assert.Contains(overResult.Diagnostics, value => value.Code == "MIDORA2202" && value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ExactlyTwoHundredFiftySixChannelUnitsAreLegal()
    {
        var fixture = CompilerTestProject.Create(256);
        foreach (SubVoice voice in fixture.Instrument.SubVoices)
        {
            voice.Events.Add(TemplateEvent.Note(0, 120, 60, 100));
        }
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.Equal(256, result.Statistics.PeakChannelUnitCount);
        Assert.Equal(256, result.Allocations.Length);
        Assert.Contains(result.Allocations.ToArray(), value => value.ZeroBasedPort == 15 && value.ZeroBasedChannel == 15);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA2250"
            && value.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void UnrepresentableSegmentRangeReturnsDiagnosticInsteadOfThrowing()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Segment.ProjectStartTick = long.MaxValue - 10;
        fixture.Segment.LengthTicks = 20;

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1310");
    }

    [Fact]
    public void TimeSignatureUsesInitialReleaseBoundsAndMarkersMayShareTick()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Project.Conductor.Markers.Add(new ProjectMarker(MidoraId.New(), 120, "A"));
        fixture.Project.Conductor.Markers.Add(new ProjectMarker(MidoraId.New(), 120, "B"));
        fixture.Project.Conductor.TimeSignatures.Add(new TimeSignatureChange(240, 100, 4));

        CanonicalCompiledResult invalid = new MidoraCompiler().CompileFull(fixture.Project);
        Assert.False(invalid.IsConsumable);
        Assert.Contains(invalid.Diagnostics, value => value.Code == "MIDORA1013");

        fixture.Project.Conductor.TimeSignatures[^1] = new TimeSignatureChange(240, 99, 64);
        CanonicalCompiledResult valid = new MidoraCompiler().CompileFull(fixture.Project);
        Assert.True(valid.IsConsumable);
        Assert.Equal(["A", "B"], valid.Conductor.Markers.ToArray().Select(value => value.Name).ToArray());
    }

    [Fact]
    public void SegmentOverlapIsRejected()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Track.Segments.Add(new Segment { ProjectStartTick = 100, LengthTicks = 100 });
        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1311");
    }

    [Fact]
    public void UnboundNonEmptyTrackProducesInfoAndNoOutput()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Track.EventInstrumentId = null;
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1304" && value.Severity == DiagnosticSeverity.Info);
        Assert.Empty(result.Events.ToArray());
    }
}
