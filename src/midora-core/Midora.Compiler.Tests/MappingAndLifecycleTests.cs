using Midora.Domain;
using Midora.Mapping.Contract.V2;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class MappingAndLifecycleTests
{
    [Theory]
    [InlineData(MappingRounding.Round, 63)]
    [InlineData(MappingRounding.Floor, 62)]
    [InlineData(MappingRounding.Ceiling, 63)]
    public void IntegerRoundingIsConfiguredOnTargetAndAppliedOnlyAtFinalOutput(
        MappingRounding rounding,
        int expected)
    {
        var fixture = CompilerTestProject.Create();
        TemplateEvent program = TemplateEvent.Program(fixture.Project, 0, 0);
        program.ValueTargetSettings.Rounding = rounding;
        program.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Override,
            Constant = 62.5
        });
        fixture.Voice.Events.Add(program);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        CanonicalMidiEvent value = Assert.Single(result.Events.ToArray(),
            item => item.Role == CanonicalEventRole.Program);
        Assert.Equal((byte)expected, value.Message.Byte1);
    }

    [Fact]
    public void SharedSubVoiceEventMappingAppliesToEveryPointOfTheExactTarget()
    {
        var fixture = CompilerTestProject.Create();
        TemplateEvent first = TemplateEvent.ControlChange(fixture.Project, 0, 1, 10);
        first.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Add,
            Constant = 5
        });
        TemplateEvent second = TemplateEvent.ControlChange(fixture.Project, 120, 1, 20);
        fixture.Voice.Events.AddRange([first, second]);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        CanonicalMidiEvent[] values = result.Events.ToArray().Where(value =>
            value.Role == CanonicalEventRole.ControlChange
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 1).ToArray();
        Assert.Equal([15, 25], values.Select(value => (int)value.Message.Byte2).ToArray());
        Assert.Same(first.ValueMappings, second.ValueMappings);
    }

    [Fact]
    public void RoundMidpointUsesAwayFromZeroForNegativePitchBend()
    {
        var fixture = CompilerTestProject.Create();
        TemplateEvent pitchBend = new(fixture.Project)
        {
            Kind = TemplateEventKind.PitchBend,
            Value = 0
        };
        pitchBend.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Override,
            Constant = -62.5
        });
        fixture.Voice.Events.Add(pitchBend);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        CanonicalMidiEvent value = Assert.Single(result.Events.ToArray(),
            item => item.Role == CanonicalEventRole.PitchBend);
        int signedValue = (value.Message.Byte2 << 7 | value.Message.Byte1) - 8192;
        Assert.Equal(-63, signedValue);
    }

    [Fact]
    public void CurveUsesItsTargetRoundingSettings()
    {
        var fixture = CompilerTestProject.Create();
        ValueCurve curve = new(fixture.Project) { Target = MidiValueTarget.ControlChange(1) };
        curve.TargetSettings.Rounding = MappingRounding.Floor;
        curve.Points.Add(new CurvePoint(fixture.Project, 0, 62.5));
        fixture.Voice.Curves.Add(curve);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        CanonicalMidiEvent value = Assert.Single(result.Events.ToArray(), item => item.Tick == 0
            && item.Message.MessageType == MidiMessageType.ControlChange && item.Message.Byte1 == 1);
        Assert.Equal((byte)62, value.Message.Byte2);
    }

    [Fact]
    public void CurveDiscretizationEvaluatesEveryIntegerTickAndSuppressesRepeatedFinalValues()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 20);
        ValueCurve curve = new(fixture.Project) { Target = MidiValueTarget.ControlChange(1) };
        curve.Points.Add(new CurvePoint(fixture.Project, 0, 0));
        curve.Points.Add(new CurvePoint(fixture.Project, 10, 1));
        fixture.Voice.Curves.Add(curve);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 11);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine,
            result.Diagnostics.Select(value => value.Message)));
        CanonicalMidiEvent[] values = result.Events.ToArray().Where(value =>
            value.Role == CanonicalEventRole.ControlChange
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 1).ToArray();
        Assert.Equal([0L, 5L], values.Select(value => value.Tick).ToArray());
        Assert.Equal([0, 1], values.Select(value => (int)value.Message.Byte2).ToArray());
    }

    [Fact]
    public void CurveProducesNoEventBeforeItsFirstPoint()
    {
        var fixture = CompilerTestProject.Create();
        ValueCurve curve = new(fixture.Project) { Target = MidiValueTarget.ControlChange(1) };
        curve.Points.Add(new CurvePoint(fixture.Project, 100, 64));
        fixture.Voice.Curves.Add(curve);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 240, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        CanonicalMidiEvent value = Assert.Single(result.Events.ToArray(), item =>
            item.Role == CanonicalEventRole.ControlChange
            && item.Message.MessageType == MidiMessageType.ControlChange
            && item.Message.Byte1 == 1);
        Assert.Equal(100, value.Tick);
        Assert.Equal((byte)64, value.Message.Byte2);
    }

    [Fact]
    public void CurveFinalOverflowPolicyBelongsToTarget()
    {
        var fixture = CompilerTestProject.Create();
        ValueCurve curve = new(fixture.Project) { Target = MidiValueTarget.ControlChange(1) };
        curve.Points.Add(new CurvePoint(fixture.Project, 0, 200));
        fixture.Voice.Curves.Add(curve);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult failed = new MidoraCompiler().CompileFull(fixture.Project);
        curve.TargetSettings.Overflow = MappingOverflow.Clamp;
        CanonicalCompiledResult clamped = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(failed.IsConsumable);
        Assert.Contains(failed.Diagnostics, value => value.Code == "MIDORA1224");
        Assert.True(clamped.IsConsumable);
        Assert.Contains(clamped.Events.ToArray(), value => value.Tick == 0
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 1 && value.Message.Byte2 == 127);
    }

    [Fact]
    public void TriggerVelocityCanDriveNoteVelocityWithoutChannelIsolation()
    {
        var fixture = CompilerTestProject.Create();
        TemplateEvent noteEvent = TemplateEvent.Note(fixture.Project, 0, 120, 60, 27);
        noteEvent.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.TriggerVelocity,
            Operation = MappingOperation.Override
        });
        fixture.Voice.Events.Add(noteEvent);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(fixture.Instrument.RequiresChannelIsolation);
        Assert.True(result.IsConsumable, string.Join(Environment.NewLine,
            result.Diagnostics.Select(value => value.Message)));
        CanonicalMidiEvent noteOn = Assert.Single(result.Events.ToArray(),
            value => value.Role == CanonicalEventRole.NoteOn);
        Assert.Equal((byte)110, noteOn.Message.Byte2);
    }

    [Fact]
    public void BuiltInAndCSharpMappingsRunDuringCompilation()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Instrument.RequiresChannelIsolation = true;
        CSharpMappingFunction function = new(fixture.Project)
        {
            Name = "velocity boost",
            Body = "return Math.Min(127, value + context.TriggerVelocity / 10.0);"
        };
        function.DeclaredContextFields.Add(nameof(MappingContextV2.TriggerVelocity));
        fixture.Instrument.MappingFunctions.Add(function);
        TemplateEvent noteEvent = TemplateEvent.Note(fixture.Project, 0, 120, 60, 80);
        noteEvent.NumberMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.TriggerNote,
            Operation = MappingOperation.Override
        });
        noteEvent.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        });
        noteEvent.ValueTargetSettings.Overflow = MappingOverflow.Clamp;
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
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 400, 60, 100));
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 300, 1, 50));
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
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 120, 60, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 900);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        long[] onTicks = result.Events.ToArray().Where(value => value.Role == CanonicalEventRole.NoteOn)
            .Select(value => value.Tick).ToArray();

        Assert.Equal([120L, 360L, 600L, 840L], onTicks);
    }

    [Fact]
    public void NoteCoveringEntireLoopSustainsUntilLoopLifecycleEnds()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 1_440);
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.LoopStartTick = 120;
        fixture.Instrument.LoopEndTick = 360;
        fixture.Voice.Events.Add(TemplateEvent.Note(
            fixture.Project,
            tick: 0,
            lengthTicks: 400,
            note: 60,
            velocity: 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 900);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(
            result.IsConsumable,
            string.Join(Environment.NewLine, result.Diagnostics.Select(value => value.Message)));
        Assert.Single(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOn);
        CanonicalMidiEvent noteOff = Assert.Single(
            result.Events.ToArray(),
            value => value.Role == CanonicalEventRole.NoteOff);
        Assert.Equal(1_020, noteOff.Tick);
    }

    [Fact]
    public void LongLoopExitsIntoPostLoopTailWithoutStartingNewReleaseNotes()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 1_440);
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.LoopStartTick = 120;
        fixture.Instrument.LoopEndTick = 360;
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 400, 1, 77));
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 400, 20, 67, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 900);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 940
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 1
            && value.Message.Byte2 == 77);
        Assert.DoesNotContain(result.Events.ToArray(), value => value.Tick >= 900
            && value.Role == CanonicalEventRole.NoteOn);
        Assert.Contains(result.Allocations.ToArray(), value => value.EndTick == 1_440);
    }

    [Fact]
    public void LogicalParameterMappingReceivesLoopedAndPostLoopTemplateTick()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 1_440);
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.LoopStartTick = 120;
        fixture.Instrument.LoopEndTick = 360;
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "dummy",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        CSharpMappingFunction function = new(fixture.Project)
        {
            Name = "template tick",
            Body = "return context.TemplateTick % 128;"
        };
        function.DeclaredContextFields.Add(nameof(MappingContextV2.TemplateTick));
        fixture.Instrument.MappingFunctions.Add(function);
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        });
        mapping.TargetSettings.Overflow = MappingOverflow.Clamp;
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
        TemplateEvent note = TemplateEvent.Note(fixture.Project, 0, 120, 60, 80);
        ValueMappingStep step = new(fixture.Project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Override,
            Constant = 0
        };
        note.ValueMappings.Add(step);
        fixture.Voice.Events.Add(note);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult failed = new MidoraCompiler().CompileFull(fixture.Project);
        note.ValueTargetSettings.Overflow = MappingOverflow.Clamp;
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
        mapping.TargetSettings.Rounding = MappingRounding.Floor;
        fixture.Instrument.ParameterMappings.Add(mapping);
        LogicalParameterLane lane = new(fixture.Project) { ParameterId = parameter.Id };
        lane.Points.Add(new(fixture.Project, 0, 0));
        lane.Points.Add(new(fixture.Project, 10, 1));
        fixture.Segment.ParameterLanes.Add(lane);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 10, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 20);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        byte[] values = result.Events.ToArray().Where(value => value.Role != CanonicalEventRole.Reset
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11).Select(value => value.Message.Byte2).ToArray();

        Assert.Equal(11, values.Length);
        Assert.Equal((byte)0, values[0]);
        Assert.Equal((byte)12, values[1]);
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
        InstrumentEnvelope envelope = new(fixture.Project) { ReleaseTicks = 100, EndValue = 0 };
        fixture.Instrument.Envelopes.Add(envelope);
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "dummy",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(11)
        };
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Envelope,
            EnvelopeId = envelope.Id,
            Operation = MappingOperation.Override
        });
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.Remap,
            SourceMinimum = 0,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 127
        });
        mapping.TargetSettings.Overflow = MappingOverflow.Clamp;
        fixture.Instrument.ParameterMappings.Add(mapping);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 400, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 200);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics.Select(value => value.Message)));
        Assert.Contains(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOff && value.Tick == 300);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 250
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11 && value.Message.Byte2 == 63);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 299
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11 && value.Message.Byte2 == 0);
    }

    [Fact]
    public void EventEnvelopeMappingUsesHeldOriginalControllerStateDuringRelease()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.RequiresChannelIsolation = true;
        InstrumentEnvelope envelope = new(fixture.Project)
        {
            StartValue = 1,
            PeakValue = 1,
            SustainValue = 1,
            EndValue = 0,
            ReleaseTicks = 100
        };
        fixture.Instrument.Envelopes.Add(envelope);
        TemplateEvent expression = TemplateEvent.ControlChange(
            fixture.Project,
            tick: 0,
            controller: 11,
            value: 127);
        expression.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Envelope,
            EnvelopeId = envelope.Id,
            Operation = MappingOperation.Multiply
        });
        expression.ValueTargetSettings.Overflow = MappingOverflow.Clamp;
        fixture.Voice.Events.Add(expression);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 400, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 200);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(
            result.IsConsumable,
            string.Join(Environment.NewLine, result.Diagnostics.Select(value => value.Message)));
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 0
            && value.Role == CanonicalEventRole.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 127);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 250
            && value.Role == CanonicalEventRole.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 63);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 299
            && value.Role == CanonicalEventRole.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 0);
        Assert.Contains(result.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.NoteOff && value.Tick == 300);
    }

    [Fact]
    public void EventEnvelopeMappingUsesHeldOriginalStateThroughoutAttackAndDecay()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.RequiresChannelIsolation = true;
        InstrumentEnvelope envelope = new(fixture.Project)
        {
            StartValue = 0,
            PeakValue = 1,
            AttackTicks = 127,
            HoldTicks = 0,
            DecayTicks = 127,
            SustainValue = 0,
            ReleaseTicks = 0,
            EndValue = 0
        };
        fixture.Instrument.Envelopes.Add(envelope);
        TemplateEvent expression = TemplateEvent.ControlChange(
            fixture.Project,
            tick: 0,
            controller: 11,
            value: 127);
        expression.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Envelope,
            EnvelopeId = envelope.Id,
            Operation = MappingOperation.Multiply
        });
        expression.ValueTargetSettings.Overflow = MappingOverflow.Clamp;
        fixture.Voice.Events.Add(expression);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 400, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 300);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(
            result.IsConsumable,
            string.Join(Environment.NewLine, result.Diagnostics.Select(value => value.Message)));
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 64
            && value.Role == CanonicalEventRole.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 64);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 127
            && value.Role == CanonicalEventRole.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 127);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 191
            && value.Role == CanonicalEventRole.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 63);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 254
            && value.Role == CanonicalEventRole.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 0);
    }

    [Fact]
    public void EventEnvelopeMappingUsesEffectiveInitialStateWithoutAnEventPoint()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Voice.InitialState.Controllers[11] = 100;
        InstrumentEnvelope envelope = new(fixture.Project)
        {
            StartValue = 1,
            PeakValue = 1,
            SustainValue = 1,
            EndValue = 0,
            ReleaseTicks = 100
        };
        fixture.Instrument.Envelopes.Add(envelope);
        SubVoiceEventMapping mapping = fixture.Voice.GetOrCreateEventMapping(
            TemplateEventMappingTarget.Create(
                TemplateEventKind.ControlChange,
                11,
                TemplateEventMappingParameter.Value));
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Envelope,
            EnvelopeId = envelope.Id,
            Operation = MappingOperation.Multiply
        });
        mapping.TargetSettings.Overflow = MappingOverflow.Clamp;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 400, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 200);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(
            result.IsConsumable,
            string.Join(Environment.NewLine, result.Diagnostics.Select(value => value.Message)));
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 0
            && value.Role == CanonicalEventRole.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 100);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 250
            && value.Role == CanonicalEventRole.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 49);
        Assert.Contains(result.Events.ToArray(), value => value.Tick == 299
            && value.Role == CanonicalEventRole.ControlChange
            && value.Message.Byte1 == 11
            && value.Message.Byte2 == 0);
    }

    [Fact]
    public void OneShotDoesNotEnterEnvelopeReleaseAtGateEnd()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 600);
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.ShortLifecycle = ShortNoteLifecycle.OneShot;
        InstrumentEnvelope envelope = new(fixture.Project) { ReleaseTicks = 100, EndValue = 0 };
        fixture.Instrument.Envelopes.Add(envelope);
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "dummy",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(11)
        };
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Envelope,
            EnvelopeId = envelope.Id,
            Operation = MappingOperation.Override
        });
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.Remap,
            SourceMinimum = 0,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 127
        });
        mapping.TargetSettings.Overflow = MappingOverflow.Clamp;
        fixture.Instrument.ParameterMappings.Add(mapping);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 400, 60, 100));
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
        TemplateEvent controller = TemplateEvent.ControlChange(fixture.Project, 120, 1, 0);
        controller.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.TemplateTick,
            Operation = MappingOperation.Override
        });
        controller.ValueTargetSettings.Overflow = MappingOverflow.Clamp;
        fixture.Voice.Events.Add(controller);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 120, 30, 60, 100));
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
        fixture.Voice.Events.Add(new TemplateEvent(fixture.Project)
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
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "offset",
            Type = LogicalParameterType.Integer,
            Minimum = 0,
            Maximum = 127,
            DefaultValue = 10
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 0, 1, 40));
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 10, 60, 100));
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.LogicalParameter,
            LogicalParameterId = parameter.Id,
            Operation = MappingOperation.Add
        });
        mapping.TargetSettings.Overflow = MappingOverflow.Clamp;
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
        TemplateEvent controller = TemplateEvent.ControlChange(fixture.Project, 0, 1, 2);
        controller.ValueMappings.Add(new ValueMappingStep(fixture.Project)
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
    public void EmptyRemapInputRangeUsesConfiguredDivideByZeroPolicy()
    {
        var fixture = CompilerTestProject.Create();
        TemplateEvent controller = TemplateEvent.ControlChange(fixture.Project, 0, 1, 2);
        ValueMappingStep step = new(fixture.Project)
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.Remap,
            SourceMinimum = 1,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 100,
            DivideByZero = DivideByZeroPolicy.TargetMaximum
        };
        controller.ValueMappings.Add(step);
        fixture.Voice.Events.Add(controller);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 20);

        CanonicalCompiledResult fallback = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(fallback.IsConsumable);
        Assert.Contains(fallback.Events.ToArray(), value => value.Role == CanonicalEventRole.ControlChange
            && value.Message.Byte1 == 1 && value.Message.Byte2 == 127);

        step.DivideByZero = DivideByZeroPolicy.Fail;
        CanonicalCompiledResult failure = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(failure.IsConsumable);
        Assert.Contains(failure.Diagnostics, value => value.Code == "MIDORA2101");
    }

    [Fact]
    public void CutNewRejectNewOmitsConflictingTriggerWithDiagnostic()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 1_000);
        fixture.Instrument.OverlapPolicy = OverlapPolicy.CutNewRejectNew;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 400, 60, 100));
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
        InstrumentEnvelope envelope = new(fixture.Project) { ReleaseTicks = 50 };
        fixture.Instrument.Envelopes.Add(envelope);
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "dummy",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(11)
        };
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Envelope,
            EnvelopeId = envelope.Id,
            Operation = MappingOperation.Override
        });
        fixture.Instrument.ParameterMappings.Add(mapping);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 400, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 400);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 100, 400);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics.Select(value => value.Message)));
        Assert.Equal([0L, 100L], result.Events.ToArray().Where(value => value.Role == CanonicalEventRole.NoteOn)
            .Select(value => value.Tick).ToArray());
        Assert.Contains(result.Events.ToArray(), value => value.Role == CanonicalEventRole.NoteOff && value.Tick == 150);
    }
}
