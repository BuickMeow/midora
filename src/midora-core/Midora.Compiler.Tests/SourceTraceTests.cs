using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class SourceTraceTests
{
    [Fact]
    public void TemplateMappingFailureIdentifiesEventStepAndFunction()
    {
        var fixture = CompilerTestProject.Create();
        CSharpMappingFunction function = AddFunction(
            fixture.Project,
            fixture.Instrument,
            "Too loud",
            "200");
        TemplateEvent sourceEvent = TemplateEvent.Note(fixture.Project, 0, 120, 60, 100);
        ValueMappingStep step = new(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        };
        sourceEvent.ValueMappings.Add(step);
        fixture.Voice.Events.Add(sourceEvent);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        CompilerDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            value => value.Code == "MIDORA2101");
        Assert.Equal(sourceEvent.Id, diagnostic.Source.SourceEventId);
        Assert.Equal(step.Id, diagnostic.Source.MappingStepId);
        Assert.Equal(function.Id, diagnostic.Source.MappingFunctionId);
    }

    [Fact]
    public void MappingRuntimeFailureReportsTheBoundedEvaluationFailure()
    {
        var fixture = CompilerTestProject.Create();
        CSharpMappingFunction function = AddFunction(
            fixture.Project,
            fixture.Instrument,
            "Runtime failure",
            "value / 0");
        TemplateEvent sourceEvent = TemplateEvent.ControlChange(fixture.Project, 0, 1, 20);
        ValueMappingStep step = new(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        };
        sourceEvent.ValueMappings.Add(step);
        fixture.Voice.Events.Add(sourceEvent);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        CompilerDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            value => value.Code == "MIDORA2101");
        Assert.Contains("NaN or Infinity", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(sourceEvent.Id, diagnostic.Source.SourceEventId);
        Assert.Equal(step.Id, diagnostic.Source.MappingStepId);
        Assert.Equal(function.Id, diagnostic.Source.MappingFunctionId);
    }

    [Fact]
    public void TemplateMappingCanonicalEventKeepsEventStepAndFunction()
    {
        var fixture = CompilerTestProject.Create();
        CSharpMappingFunction function = AddFunction(
            fixture.Project,
            fixture.Instrument,
            "Controller value",
            "65");
        TemplateEvent sourceEvent = TemplateEvent.ControlChange(
            fixture.Project,
            0,
            1,
            20);
        ValueMappingStep step = new(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        };
        sourceEvent.ValueMappings.Add(step);
        fixture.Voice.Events.Add(sourceEvent);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 10);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        CanonicalMidiEvent value = Assert.Single(
            result.Events.ToArray(),
            item => item.Role == CanonicalEventRole.ControlChange
                && item.Message.Byte1 == 1
                && item.Message.Byte2 == 65);
        Assert.Equal(sourceEvent.Id, value.Source.SourceEventId);
        Assert.Equal(step.Id, value.Source.MappingStepId);
        Assert.Equal(function.Id, value.Source.MappingFunctionId);
        Assert.Equal(SourceOrigin.TemplateEvent, value.Source.Origin);
    }

    [Fact]
    public void LogicalParameterMappingFailureIdentifiesParameterMappingStepAndFunction()
    {
        var fixture = CompilerTestProject.Create();
        LogicalParameterDefinition parameter = AddParameter(fixture.Project, fixture.Instrument);
        CSharpMappingFunction function = AddFunction(
            fixture.Project,
            fixture.Instrument,
            "Not finite",
            "value / 0");
        LogicalParameterMapping mapping = AddLogicalParameterMapping(
            fixture.Project,
            fixture.Instrument,
            fixture.Voice,
            parameter);
        ValueMappingStep step = new(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        };
        mapping.Steps.Add(step);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        CompilerDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            value => value.Code == "MIDORA2102");
        Assert.Equal(parameter.Id, diagnostic.Source.LogicalParameterId);
        Assert.Equal(mapping.Id, diagnostic.Source.LogicalParameterMappingId);
        Assert.Equal(step.Id, diagnostic.Source.MappingStepId);
        Assert.Equal(function.Id, diagnostic.Source.MappingFunctionId);
    }

    [Fact]
    public void LogicalParameterCanonicalEventKeepsItsFinalMappingSource()
    {
        var fixture = CompilerTestProject.Create();
        LogicalParameterDefinition parameter = AddParameter(fixture.Project, fixture.Instrument);
        CSharpMappingFunction function = AddFunction(
            fixture.Project,
            fixture.Instrument,
            "Constant",
            "65");
        LogicalParameterMapping mapping = AddLogicalParameterMapping(
            fixture.Project,
            fixture.Instrument,
            fixture.Voice,
            parameter);
        ValueMappingStep step = new(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        };
        mapping.Steps.Add(step);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 10);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        CanonicalMidiEvent value = Assert.Single(
            result.Events.ToArray(),
            item => item.Role == CanonicalEventRole.LogicalParameter);
        Assert.Equal(parameter.Id, value.Source.LogicalParameterId);
        Assert.Equal(mapping.Id, value.Source.LogicalParameterMappingId);
        Assert.Equal(step.Id, value.Source.MappingStepId);
        Assert.Equal(function.Id, value.Source.MappingFunctionId);
        Assert.Equal(SourceOrigin.LogicalParameterMapping, value.Source.Origin);
    }

    [Fact]
    public void ValueCurveCanonicalEventsKeepCurveIdentity()
    {
        var fixture = CompilerTestProject.Create();
        ValueCurve curve = new(fixture.Project)
        {
            Target = MidiValueTarget.ControlChange(1)
        };
        curve.Points.Add(new CurvePoint(fixture.Project, 0, 20));
        fixture.Voice.Curves.Add(curve);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 10);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        CanonicalMidiEvent value = Assert.Single(
            result.Events.ToArray(),
            item => item.Role == CanonicalEventRole.ControlChange
                && item.Message.Byte1 == 1);
        Assert.Equal(curve.Id, value.Source.ValueCurveId);
        Assert.Equal(SourceOrigin.ValueCurve, value.Source.Origin);
    }

    [Fact]
    public void SemanticAndCompilationDiagnosticsKeepReferencedObjectIdentity()
    {
        var fixture = CompilerTestProject.Create();
        LogicalParameterDefinition parameter = AddParameter(fixture.Project, fixture.Instrument);
        LogicalParameterMapping mapping = AddLogicalParameterMapping(
            fixture.Project,
            fixture.Instrument,
            fixture.Voice,
            parameter);
        MidoraId missingFunctionId = fixture.Project.AllocateStableId();
        ValueMappingStep step = new(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = missingFunctionId
        };
        mapping.Steps.Add(step);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        CompilerDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            value => value.Code == "MIDORA1234");
        Assert.Equal(parameter.Id, diagnostic.Source.LogicalParameterId);
        Assert.Equal(mapping.Id, diagnostic.Source.LogicalParameterMappingId);
        Assert.Equal(step.Id, diagnostic.Source.MappingStepId);
        Assert.Equal(missingFunctionId, diagnostic.Source.MappingFunctionId);
    }

    [Fact]
    public void InitialStateAndHardBoundaryCleanupExposeGeneratedOrigins()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Voice.InitialState.Program = 12;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 480, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.Contains(result.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.InitialState
            && value.Message.MessageType == Midora.Midi.MidiMessageType.ProgramChange
            && value.Message.Byte1 == 12
            && value.Source.Origin == SourceOrigin.MergedInitialState);
        Assert.Contains(result.Events.ToArray(), value =>
            value.Tick == result.EndTick
            && value.Role == CanonicalEventRole.NoteOff
            && value.Source.Origin == SourceOrigin.CompilerBoundaryCleanup);
        CanonicalMidiEvent[] resets = result.Events.ToArray().Where(value =>
            value.Tick == result.EndTick
            && value.Role == CanonicalEventRole.Reset).ToArray();
        Assert.NotEmpty(resets);
        CanonicalMidiEvent soundOff = Assert.Single(resets, value =>
            value.Message.MessageType == Midora.Midi.MidiMessageType.ControlChange
            && value.Message.Byte1 == 120);
        Assert.Equal(SourceOrigin.CompilerBoundaryCleanup, soundOff.Source.Origin);
        Assert.All(
            resets.Where(value => value != soundOff),
            value => Assert.Equal(SourceOrigin.ProjectResetDefaults, value.Source.Origin));
    }

    private static LogicalParameterDefinition AddParameter(
        MidoraProject project,
        EventInstrument instrument)
    {
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Expression",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 127,
            DefaultValue = 64
        };
        instrument.LogicalParameters.Add(parameter);
        return parameter;
    }

    private static CSharpMappingFunction AddFunction(
        MidoraProject project,
        EventInstrument instrument,
        string name,
        string body)
    {
        CSharpMappingFunction function = new(project)
        {
            Name = name,
            Body = body
        };
        instrument.MappingFunctions.Add(function);
        return function;
    }

    private static LogicalParameterMapping AddLogicalParameterMapping(
        MidoraProject project,
        EventInstrument instrument,
        SubVoice subVoice,
        LogicalParameterDefinition parameter)
    {
        LogicalParameterMapping mapping = new(project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = subVoice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        instrument.ParameterMappings.Add(mapping);
        return mapping;
    }
}
