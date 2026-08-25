using Midora.Compiler;
using Midora.Domain;
using Midora.Mapping.Contract.V2;
using Midora.Midi;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectMappingFunctionEditCommandsTests
{
    [Fact]
    public void FunctionUpdatePreservesIdentityReferenceAbiAndUndoIsExact()
    {
        Fixture fixture = CreateFixture();
        long nextStableId = fixture.Project.NextStableId;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        AssertController(compilation.LastAttempt, 21);

        document.Execute(ProjectDomainEditCommands.UpdateMappingFunction(
            fixture.Instrument.Id,
            fixture.Function.Id,
            "  More Boost  ",
            "value + 10 + context.ProjectTick - context.ProjectTick",
            [nameof(MappingContextV2.ProjectTick)]));

        Assert.Same(fixture.Function, fixture.Instrument.MappingFunctions.Single());
        Assert.Equal("More Boost", fixture.Function.Name);
        Assert.Equal("value + 10 + context.ProjectTick - context.ProjectTick", fixture.Function.Body);
        Assert.Equal([nameof(MappingContextV2.ProjectTick)],
            fixture.Function.DeclaredContextFields.Order().ToArray());
        Assert.Equal(MappingExpressionAbiV3.Version, fixture.Function.AbiVersion);
        Assert.Equal(fixture.Function.Id, fixture.Step.MappingFunctionId);
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        AssertController(compilation.LastAttempt, 30);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(fixture.Function, fixture.Instrument.MappingFunctions.Single());
        Assert.Equal("Boost", fixture.Function.Name);
        Assert.Equal("value + 1", fixture.Function.Body);
        Assert.Empty(fixture.Function.DeclaredContextFields);
        Assert.Equal(fixture.Function.Id, fixture.Step.MappingFunctionId);
        AssertController(compilation.LastAttempt, 21);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void InvalidFunctionSourceCanBeSavedAsDiagnosticAndUndone()
    {
        Fixture fixture = CreateFixture();
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpdateMappingFunction(
            fixture.Instrument.Id,
            fixture.Function.Id,
            fixture.Function.Name,
            "return ;",
            []));

        Assert.False(compilation.LastAttempt.IsConsumable);
        Assert.Contains(compilation.LastAttempt.Diagnostics, value => value.Code == "MIDORA2103");
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal("value + 1", fixture.Function.Body);
        Assert.True(compilation.LastAttempt.IsConsumable);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void FunctionUpdateRejectsNameBodyAndContextFieldContractViolations()
    {
        Fixture fixture = CreateFixture();
        fixture.Instrument.MappingFunctions.Add(new CSharpMappingFunction(fixture.Project)
        {
            Name = "Other",
            Body = "value"
        });
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateMappingFunction(
                fixture.Instrument.Id,
                fixture.Function.Id,
                " other ",
                fixture.Function.Body,
                [])));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateMappingFunction(
                fixture.Instrument.Id,
                fixture.Function.Id,
                fixture.Function.Name,
                "\ud800",
                [])));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateMappingFunction(
                fixture.Instrument.Id,
                fixture.Function.Id,
                fixture.Function.Name,
                fixture.Function.Body,
                [" ProjectTick ", "ProjectTick"])));
        Assert.Throws<ArgumentException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateMappingFunction(
                fixture.Instrument.Id,
                fixture.Function.Id,
                fixture.Function.Name,
                fixture.Function.Body,
                ["bad\nfield"])));

        Assert.False(document.CanUndo);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void ReferencedDeleteRequiresConfirmationAndPreservesBrokenReferenceForUndo()
    {
        Fixture fixture = CreateFixture();
        long nextStableId = fixture.Project.NextStableId;
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.DeleteMappingFunction(
                fixture.Instrument.Id,
                fixture.Function.Id,
                referencedDeletionConfirmed: false)));
        document.Execute(ProjectDomainEditCommands.DeleteMappingFunction(
            fixture.Instrument.Id,
            fixture.Function.Id,
            referencedDeletionConfirmed: true));

        Assert.DoesNotContain(fixture.Function, fixture.Instrument.MappingFunctions);
        Assert.Equal(fixture.Function.Id, fixture.Step.MappingFunctionId);
        Assert.False(compilation.LastAttempt.IsConsumable);
        Assert.Contains(compilation.LastAttempt.Diagnostics, value => value.Code == "MIDORA1234");
        Assert.Equal(nextStableId, fixture.Project.NextStableId);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Same(fixture.Function, fixture.Instrument.MappingFunctions[0]);
        Assert.Equal(fixture.Function.Id, fixture.Step.MappingFunctionId);
        Assert.True(compilation.LastAttempt.IsConsumable);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void UnreferencedFunctionCanBeDeletedWithoutConfirmation()
    {
        Fixture fixture = CreateFixture();
        CSharpMappingFunction unused = new(fixture.Project)
        {
            Name = "Unused",
            Body = "value"
        };
        fixture.Instrument.MappingFunctions.Add(unused);
        using ProjectCompilationSession compilation = new(fixture.Project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DeleteMappingFunction(
            fixture.Instrument.Id,
            unused.Id,
            referencedDeletionConfirmed: false));

        Assert.DoesNotContain(unused, fixture.Instrument.MappingFunctions);
        document.Undo();
        Assert.Same(unused, fixture.Instrument.MappingFunctions[1]);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    private static void AssertController(CanonicalCompiledResult result, byte expected)
    {
        CanonicalMidiEvent value = Assert.Single(result.Events.ToArray(), item =>
            item.Tick == 0
            && item.Role == CanonicalEventRole.ControlChange
            && item.Message.MessageType == MidiMessageType.ControlChange
            && item.Message.Byte1 == 11);
        Assert.Equal(expected, value.Message.Byte2);
    }

    private static Fixture CreateFixture()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Piano",
            TemplateLengthTicks = 480,
            RequiresChannelIsolation = true,
            OverlapPolicy = OverlapPolicy.Warn
        };
        CSharpMappingFunction function = new(project)
        {
            Name = "Boost",
            Body = "value + 1"
        };
        instrument.MappingFunctions.Add(function);
        SubVoice voice = new(project);
        TemplateEvent controller = TemplateEvent.ControlChange(project, 0, 11, 20);
        ValueMappingStep step = new(project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        };
        controller.ValueMappings.Add(step);
        voice.Events.Add(controller);
        voice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) {
            Name = "Track",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 960 };
        segment.Notes.Add(new LogicalNote(project)
        {
            LengthTicks = 240,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        return new(project, instrument, function, step);
    }

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation) =>
        new(compilation, ProjectDocumentOrigin.Persisted);

    private static void AssertCurrentCompilationMatchesFull(ProjectCompilationSession compilation)
    {
        using MidoraCompiler fullCompiler = new();
        CanonicalCompiledResult expected = fullCompiler.CompileFull(compilation.Project);
        CanonicalCompiledResult actual = compilation.LastAttempt;
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.FailureStage, actual.FailureStage);
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }

    private sealed record Fixture(
        MidoraProject Project,
        EventInstrument Instrument,
        CSharpMappingFunction Function,
        ValueMappingStep Step);
}
