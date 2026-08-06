using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class SemanticValidatorTests
{
    [Fact]
    public void UndefinedTemplateEventKindIsRejected()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Voice.Events.Add(new TemplateEvent(fixture.Project)
        {
            Kind = (TemplateEventKind)999,
            Tick = 0
        });
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1249");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndefinedCurveInterpolationIsRejected(bool logicalParameterLane)
    {
        var fixture = CompilerTestProject.Create();
        CurvePoint invalidPoint = new(
            fixture.Project,
            tick: 0,
            value: 64,
            interpolation: (CurveInterpolation)999);
        if (logicalParameterLane)
        {
            LogicalParameterDefinition parameter = new(fixture.Project)
            {
                Name = "Expression",
                Type = LogicalParameterType.Double,
                Minimum = 0,
                Maximum = 127,
                DefaultValue = 64
            };
            fixture.Instrument.LogicalParameters.Add(parameter);
            LogicalParameterLane lane = new(fixture.Project) { ParameterId = parameter.Id };
            lane.Points.Add(invalidPoint);
            fixture.Segment.ParameterLanes.Add(lane);
        }
        else
        {
            ValueCurve curve = new(fixture.Project)
            {
                Target = MidiValueTarget.ControlChange(1)
            };
            curve.Points.Add(invalidPoint);
            fixture.Voice.Curves.Add(curve);
        }
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code ==
            (logicalParameterLane ? "MIDORA1313" : "MIDORA1222"));
    }

    [Fact]
    public void UndefinedMidiValueTargetKindIsRejected()
    {
        var fixture = CompilerTestProject.Create();
        ValueCurve curve = new(fixture.Project)
        {
            Target = new MidiValueTarget((MidiValueKind)999)
        };
        curve.Points.Add(new CurvePoint(fixture.Project, 0, 64));
        fixture.Voice.Curves.Add(curve);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1260");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void UndefinedEventInstrumentPolicyEnumsAreRejected(int field)
    {
        var fixture = CompilerTestProject.Create();
        switch (field)
        {
            case 0:
                fixture.Instrument.OverlapPolicy = (OverlapPolicy)999;
                break;
            case 1:
                fixture.Instrument.OverlapScope = (OverlapScope)999;
                break;
            case 2:
                fixture.Instrument.ShortLifecycle = (ShortNoteLifecycle)999;
                break;
            case 3:
                fixture.Instrument.LongLifecycle = (LongNoteLifecycle)999;
                break;
        }
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1216");
    }

    [Fact]
    public void TempoMustFitTheMidiOneSetTempoFieldAfterSingleAwayFromZeroRounding()
    {
        foreach (decimal beatsPerMinute in new[] { 0.000001m, 120_000_001m })
        {
            MidoraProject project = new(480);
            project.Conductor.Tempos[0] = new TempoChange(project, 0, beatsPerMinute);

            CanonicalCompiledResult result = new MidoraCompiler().CompileFull(project);

            Assert.False(result.IsConsumable);
            Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1012");
        }
    }

    [Fact]
    public void LogicalMappingsForSameTargetRequireOneTargetPolicy()
    {
        var fixture = CompilerTestProject.Create();
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "parameter",
            Minimum = 0,
            Maximum = 127
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping first = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        LogicalParameterMapping second = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        second.TargetSettings.Rounding = MappingRounding.Floor;
        fixture.Instrument.ParameterMappings.Add(first);
        fixture.Instrument.ParameterMappings.Add(second);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1275");
    }

    [Fact]
    public void EnumItemValueOutsideLogicalParameterRangeIsRejected()
    {
        var fixture = CompilerTestProject.Create();
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "mode",
            Type = LogicalParameterType.Enum,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0,
            UsesExplicitEnumValues = true
        };
        parameter.EnumItems.Add(new LogicalParameterEnumItem(fixture.Project)
        {
            Name = "valid",
            Value = 0
        });
        parameter.EnumItems.Add(new LogicalParameterEnumItem(fixture.Project)
        {
            Name = "outside",
            Value = 2
        });
        fixture.Instrument.LogicalParameters.Add(parameter);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1105");
    }

    [Fact]
    public void UndefinedLogicalParameterTypeIsRejected()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Instrument.LogicalParameters.Add(new LogicalParameterDefinition(fixture.Project)
        {
            Name = "invalid",
            Type = (LogicalParameterType)999,
            Minimum = 0,
            Maximum = 1
        });

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1102");
    }

    [Fact]
    public void BrokenEnvelopeReferenceIsAnError()
    {
        var fixture = CompilerTestProject.Create();
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "value",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Envelope,
            EnvelopeId = fixture.Project.AllocateStableId()
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
        TemplateEvent controller = TemplateEvent.ControlChange(fixture.Project, 0, 1, 20);
        controller.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            IsEnabled = false,
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = fixture.Project.AllocateStableId()
        });
        fixture.Voice.Events.Add(controller);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 20);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.DoesNotContain(result.Diagnostics, value => value.Code is "MIDORA1234" or "MIDORA1271");
    }

    [Fact]
    public void UnknownMappingEnumsFailOnlyWhenStepParticipates()
    {
        var fixture = CompilerTestProject.Create();
        TemplateEvent controller = TemplateEvent.ControlChange(fixture.Project, 0, 1, 20);
        ValueMappingStep step = new(fixture.Project)
        {
            Source = (MappingSource)999,
            Operation = (MappingOperation)999,
            InputOverflow = (MappingInputOverflow)999,
            DivideByZero = (DivideByZeroPolicy)999
        };
        controller.ValueMappings.Add(step);
        fixture.Voice.Events.Add(controller);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 20);

        CanonicalCompiledResult active = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(active.IsConsumable);
        Assert.Contains(active.Diagnostics, value => value.Code == "MIDORA1270");

        step.IsEnabled = false;
        CanonicalCompiledResult disabled = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(disabled.IsConsumable);
        Assert.DoesNotContain(disabled.Diagnostics, value => value.Code == "MIDORA1270");
    }

    [Fact]
    public void InvalidUnreferencedMappingFunctionIsWarningOnly()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Instrument.MappingFunctions.Add(new CSharpMappingFunction(fixture.Project)
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
        EventInstrument unused = new(fixture.Project)
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
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 0, 91, 64));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1242" && value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ExplicitNoteNumberMappingRequiresPerNoteIsolation()
    {
        var fixture = CompilerTestProject.Create();
        TemplateEvent note = TemplateEvent.Note(fixture.Project, 0, 120, 60, 100);
        note.NumberMappings.Add(new ValueMappingStep(fixture.Project)
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
            voice.Events.Add(TemplateEvent.Note(near.Project, 0, 120, 60, 100));
        }
        CompilerTestProject.AddNote(near.Segment, near.Instrument, 0, 480);
        CanonicalCompiledResult nearResult = new MidoraCompiler().CompileFull(near.Project);
        Assert.True(nearResult.IsConsumable);
        Assert.Contains(nearResult.Diagnostics, value => value.Code == "MIDORA2250" && value.Severity == DiagnosticSeverity.Info);

        var over = CompilerTestProject.Create(256);
        EventInstrument second = new(over.Project)
        {
            Name = "Second",
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        second.SubVoices.Add(new SubVoice(over.Project));
        over.Project.EventInstruments.Add(second);
        foreach (SubVoice voice in over.Instrument.SubVoices)
        {
            voice.Events.Add(TemplateEvent.Note(over.Project, 0, 120, 60, 100));
        }
        second.SubVoices[0].Events.Add(TemplateEvent.Note(over.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(over.Segment, over.Instrument, 0, 480);
        LogicalTrack secondTrack = new(over.Project) { Name = "Second", EventInstrumentId = second.Id };
        Segment secondSegment = new(over.Project) { LengthTicks = 1_920 };
        CompilerTestProject.RegisterSegment(over.Project, secondSegment);
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
            voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
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
        ProjectMarker firstMarker = new(fixture.Project, 120, "A");
        ProjectMarker secondMarker = new(fixture.Project, 120, "B");
        fixture.Project.Conductor.Markers.Add(secondMarker);
        fixture.Project.Conductor.Markers.Add(firstMarker);
        fixture.Project.Conductor.TimeSignatures.Add(new TimeSignatureChange(fixture.Project, 240, 100, 4));

        CompilationRequest range = new() { EndTick = 480 };
        CanonicalCompiledResult invalid = new MidoraCompiler().CompileFull(fixture.Project, range);
        Assert.False(invalid.IsConsumable);
        Assert.Contains(invalid.Diagnostics, value => value.Code == "MIDORA1013");

        fixture.Project.Conductor.TimeSignatures[^1] = new TimeSignatureChange(fixture.Project, 240, 99, 64);
        CanonicalCompiledResult valid = new MidoraCompiler().CompileFull(fixture.Project, range);
        Assert.True(valid.IsConsumable);
        Assert.True(firstMarker.Id.CompareTo(secondMarker.Id) < 0);
        Assert.Equal(["A", "B"], valid.Conductor.Markers.ToArray().Select(value => value.Name).ToArray());
    }

    [Fact]
    public void SegmentOverlapIsRejected()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Track.Segments.Add(new Segment(fixture.Project) { ProjectStartTick = 100, LengthTicks = 100 });
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

    [Fact]
    public void EmptyTrackAndMarkerNamesAreValid()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Track.Name = string.Empty;
        fixture.Project.Conductor.Markers.Add(new ProjectMarker(fixture.Project, 120, string.Empty));

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.DoesNotContain(result.Diagnostics, value => value.Code is "MIDORA1016" or "MIDORA1301");
    }

    [Fact]
    public void TemplateNoteOffBeyondTemplateLengthIsRejected()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 400, 100, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1241");
    }
}
