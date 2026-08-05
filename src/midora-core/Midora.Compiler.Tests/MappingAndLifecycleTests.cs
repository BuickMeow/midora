using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class MappingAndLifecycleTests
{
    [Fact]
    public void BuiltInAndCSharpMappingsRunDuringCompilation()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Instrument.RequiresChannelIsolation = true;
        CSharpMappingFunction function = new()
        {
            Name = "velocity boost",
            Body = "return Math.Min(127, value + context.TriggerVelocity / 10.0);"
        };
        function.DeclaredContextFields.Add(nameof(MappingContext.TriggerVelocity));
        fixture.Instrument.MappingFunctions.Add(function);
        TemplateEvent noteEvent = TemplateEvent.Note(0, 120, 60, 80);
        noteEvent.NumberMappings.Add(new ValueMappingStep
        {
            Source = MappingSource.TriggerNote,
            Operation = MappingOperation.Override
        });
        noteEvent.ValueMappings.Add(new ValueMappingStep
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id,
            Overflow = MappingOverflow.Clamp
        });
        fixture.Voice.Events.Add(noteEvent);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480, 65);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics.Select(value => value.Message)));
        CanonicalMidiEvent noteOn = Assert.Single(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal((byte)70, noteOn.Message.Byte1); // mapped 65, then FollowPitchDelta +5
        Assert.Equal((byte)91, noteOn.Message.Byte2);
    }

    [Fact]
    public void ShortCutSuppressesFutureEventsAndCutsNotesAtGate()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.CutAtNoteOff;
        fixture.Voice.Events.Add(TemplateEvent.Note(0, 400, 60, 100));
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(300, 1, 50));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.Contains(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOff && value.Tick == 240);
        Assert.DoesNotContain(result.Events.ToArray(), value => value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 1 && value.Message.Byte2 == 50);
    }

    [Fact]
    public void LongLoopRepeatsEventsUntilGate()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 1440);
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.LoopStartTick = 120;
        fixture.Instrument.LoopEndTick = 360;
        fixture.Voice.Events.Add(TemplateEvent.Note(120, 60, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 900);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        long[] onTicks = result.Events.ToArray().Where(value => value.Role == CanonicalEventRole.NoteOn)
            .Select(value => value.Tick).ToArray();

        Assert.Equal([120L, 360L, 600L, 840L], onTicks);
    }

    [Fact]
    public void LongLoopExitsIntoPostLoopTailWithoutStartingNewReleaseNotes()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 1_440);
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.LoopStartTick = 120;
        fixture.Instrument.LoopEndTick = 360;
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(400, 1, 77));
        fixture.Voice.Events.Add(TemplateEvent.Note(400, 20, 67, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 900);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 940
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 1
            && value.Message.Byte2 == 77);
        Assert.DoesNotContain(result.Events.ToArray(), value => value.Tick >= 900
            && value.Role == CanonicalEventRole.NoteOn);
        Assert.Contains(result.Allocations.ToArray(), value => value.EndTick == 1_020);
    }

    [Fact]
    public void LogicalParameterMappingReceivesLoopedAndPostLoopTemplateTick()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 1_440);
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.LoopStartTick = 120;
        fixture.Instrument.LoopEndTick = 360;
        LogicalParameterDefinition parameter = new()
        {
            Name = "dummy",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        CSharpMappingFunction function = new()
        {
            Name = "template tick",
            Body = "return context.TemplateTick % 128;"
        };
        function.DeclaredContextFields.Add(nameof(MappingContext.TemplateTick));
        fixture.Instrument.MappingFunctions.Add(function);
        LogicalParameterMapping mapping = new()
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        mapping.Steps.Add(new ValueMappingStep
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id,
            Overflow = MappingOverflow.Clamp
        });
        fixture.Instrument.ParameterMappings.Add(mapping);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 900);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics.Select(value => value.Message)));
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 360
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 1
            && value.Message.Byte2 == 120);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 900
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 1
            && value.Message.Byte2 == 104);
    }

    [Fact]
    public void NoteVelocityMappingZeroRequiresExplicitClampPolicy()
    {
        var fixture = CompilerTestProject.Create();
        TemplateEvent note = TemplateEvent.Note(0, 120, 60, 80);
        ValueMappingStep step = new()
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Override,
            Constant = 0,
            Overflow = MappingOverflow.Fail
        };
        note.ValueMappings.Add(step);
        fixture.Voice.Events.Add(note);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult failed = new MidoraCompiler().CompileFull(fixture.Project);
        step.Overflow = MappingOverflow.Clamp;
        CanonicalCompiledResult clamped = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(failed.IsConsumable);
        Assert.Contains(failed.Diagnostics, value => value.Code == "MIDORA2101");
        Assert.True(clamped.IsConsumable);
        Assert.Contains(clamped.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn
            && value.Message.Byte2 == 1);
    }

    [Fact]
    public void ParameterLaneGeneratesMappedControllerCurve()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 20);
        LogicalParameterDefinition parameter = new()
        {
            Name = "expression",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        fixture.Instrument.ParameterMappings.Add(new LogicalParameterMapping
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(11),
            Steps =
            {
                new ValueMappingStep
                {
                    Operation = MappingOperation.Remap,
                    Source = MappingSource.LogicalParameter,
                    LogicalParameterId = parameter.Id,
                    SourceMinimum = 0,
                    SourceMaximum = 1,
                    TargetMinimum = 0,
                    TargetMaximum = 127,
                    Overflow = MappingOverflow.Clamp
                }
            }
        });
        LogicalParameterLane lane = new() { ParameterId = parameter.Id };
        lane.Points.Add(new(0, 0));
        lane.Points.Add(new(10, 1));
        fixture.Segment.ParameterLanes.Add(lane);
        fixture.Voice.Events.Add(TemplateEvent.Note(0, 10, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 20);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        byte[] values = result.Events.ToArray().Where(value => value.Role != CanonicalEventRole.Reset
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11).Select(value => value.Message.Byte2).ToArray();

        Assert.Equal(11, values.Length);
        Assert.Equal((byte)0, values[0]);
        Assert.Equal((byte)127, values[10]);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == result.EndTick
            && value.Role == CanonicalEventRole.Reset
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11);
    }

    [Fact]
    public void EnvelopeIsNormalizedMappingSourceAndDelaysActiveNoteOffThroughRelease()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.RequiresChannelIsolation = true;
        InstrumentEnvelope envelope = new() { ReleaseTicks = 100, EndValue = 0 };
        fixture.Instrument.Envelopes.Add(envelope);
        LogicalParameterDefinition parameter = new()
        {
            Name = "dummy",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping mapping = new()
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(11)
        };
        mapping.Steps.Add(new ValueMappingStep
        {
            Source = MappingSource.Envelope,
            EnvelopeId = envelope.Id,
            Operation = MappingOperation.Override
        });
        mapping.Steps.Add(new ValueMappingStep
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.Remap,
            SourceMinimum = 0,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 127,
            Overflow = MappingOverflow.Clamp
        });
        fixture.Instrument.ParameterMappings.Add(mapping);
        fixture.Voice.Events.Add(TemplateEvent.Note(0, 400, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 200);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics.Select(value => value.Message)));
        Assert.Contains(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOff && value.Tick == 300);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 250
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11 && value.Message.Byte2 == 64);
    }

    [Fact]
    public void OneShotDoesNotEnterEnvelopeReleaseAtGateEnd()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.OneShot;
        InstrumentEnvelope envelope = new() { ReleaseTicks = 100, EndValue = 0 };
        fixture.Instrument.Envelopes.Add(envelope);
        LogicalParameterDefinition parameter = new()
        {
            Name = "dummy",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping mapping = new()
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(11)
        };
        mapping.Steps.Add(new ValueMappingStep
        {
            Source = MappingSource.Envelope,
            EnvelopeId = envelope.Id,
            Operation = MappingOperation.Override
        });
        mapping.Steps.Add(new ValueMappingStep
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.Remap,
            SourceMinimum = 0,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 127,
            Overflow = MappingOverflow.Clamp
        });
        fixture.Instrument.ParameterMappings.Add(mapping);
        fixture.Voice.Events.Add(TemplateEvent.Note(0, 400, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 200);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.Contains(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOff && value.Tick == 400);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 0
            && value.Role == CanonicalEventRole.LogicalParameter
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11 && value.Message.Byte2 == 127);
        Assert.DoesNotContain(result.Events.ToArray(), value => value.Tick >= 200 && value.Tick < 400
            && value.Role == CanonicalEventRole.LogicalParameter
            && value.Message.Byte1 == 11);
    }

    [Fact]
    public void LoopMappingReceivesTemplateTickInsteadOfElapsedInstanceTick()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 1_200);
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.LoopStartTick = 120;
        fixture.Instrument.LoopEndTick = 360;
        TemplateEvent controller = TemplateEvent.ControlChange(120, 1, 0);
        controller.ValueMappings.Add(new ValueMappingStep
        {
            Source = MappingSource.TemplateTick,
            Operation = MappingOperation.Override,
            Overflow = MappingOverflow.Clamp
        });
        fixture.Voice.Events.Add(controller);
        fixture.Voice.Events.Add(TemplateEvent.Note(120, 30, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 900);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        CanonicalMidiEvent[] values = result.Events.ToArray().Where(value =>
            value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 1 && value.Role == CanonicalEventRole.ControlChange).ToArray();

        Assert.Equal([120L, 360L, 600L, 840L], values.Select(value => value.Tick).ToArray());
        Assert.All(values, value => Assert.Equal((byte)120, value.Message.Byte2));
    }

    [Fact]
    public void PitchBendRangeKeepsSemitoneAndCentsTransactionsAtomic()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Voice.Events.Add(new TemplateEvent
        {
            Kind = TemplateEventKind.PitchBendRange,
            Tick = 0,
            Value = 12,
            SecondaryValue = 50
        });
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        CanonicalMidiEvent[] parameter = result.Events.ToArray().Where(value =>
            value.Tick == 0 && value.Role == CanonicalEventRole.Parameter).ToArray();

        Assert.Contains(parameter, value => value.Message.Byte1 == 6 && value.Message.Byte2 == 12);
        Assert.Contains(parameter, value => value.Message.Byte1 == 38 && value.Message.Byte2 == 50);
        Assert.Equal(6, parameter.Length);
    }

    [Fact]
    public void LogicalParameterMappingComposesFromRawSubVoiceStateAndWinsSameTickConflict()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 20);
        LogicalParameterDefinition parameter = new()
        {
            Name = "offset",
            Type = LogicalParameterType.Integer,
            Minimum = 0,
            Maximum = 127,
            DefaultValue = 10
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(0, 1, 40));
        fixture.Voice.Events.Add(TemplateEvent.Note(0, 10, 60, 100));
        LogicalParameterMapping mapping = new()
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        mapping.Steps.Add(new ValueMappingStep
        {
            Source = MappingSource.LogicalParameter,
            LogicalParameterId = parameter.Id,
            Operation = MappingOperation.Add,
            Overflow = MappingOverflow.Clamp
        });
        fixture.Instrument.ParameterMappings.Add(mapping);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 20);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        CanonicalMidiEvent[] atStart = result.Events.ToArray().Where(value => value.Tick == 0
            && value.Message.MessageType == MidiMessageType.ControlChange && value.Message.Byte1 == 1).ToArray();

        CanonicalMidiEvent final = Assert.Single(atStart);
        Assert.Equal((byte)50, final.Message.Byte2);
        Assert.Equal(CanonicalEventRole.LogicalParameter, final.Role);
    }

    [Fact]
    public void RemapInputOverflowDefaultsToClamp()
    {
        var fixture = CompilerTestProject.Create();
        TemplateEvent controller = TemplateEvent.ControlChange(0, 1, 2);
        controller.ValueMappings.Add(new ValueMappingStep
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.Remap,
            SourceMinimum = 0,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 100
        });
        fixture.Voice.Events.Add(controller);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 20);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.Contains(result.Events.ToArray(), value => value.Role == CanonicalEventRole.ControlChange
            && value.Message.Byte1 == 1 && value.Message.Byte2 == 100);
    }

    [Fact]
    public void CutNewRejectNewOmitsConflictingTriggerWithDiagnostic()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 1_000);
        fixture.Instrument.OverlapPolicy = OverlapPolicy.CutNewRejectNew;
        fixture.Voice.Events.Add(TemplateEvent.Note(0, 400, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 400);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 100, 400);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.Single(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA2203");
    }

    [Fact]
    public void CutPreviousRecomputesPreviousGateAndRelease()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 1_000);
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.OverlapPolicy = OverlapPolicy.CutPrevious;
        InstrumentEnvelope envelope = new() { ReleaseTicks = 50 };
        fixture.Instrument.Envelopes.Add(envelope);
        LogicalParameterDefinition parameter = new()
        {
            Name = "dummy",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping mapping = new()
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(11)
        };
        mapping.Steps.Add(new ValueMappingStep
        {
            Source = MappingSource.Envelope,
            EnvelopeId = envelope.Id,
            Operation = MappingOperation.Override
        });
        fixture.Instrument.ParameterMappings.Add(mapping);
        fixture.Voice.Events.Add(TemplateEvent.Note(0, 400, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 400);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 100, 400);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics.Select(value => value.Message)));
        Assert.Equal([0L, 100L], result.Events.ToArray().Where(value => value.Role == CanonicalEventRole.NoteOn)
            .Select(value => value.Tick).ToArray());
        Assert.Contains(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOff && value.Tick == 150);
    }
}
