using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class CanonicalOrderingAndFoldingTests
{
    [Theory]
    [InlineData(TemplateEventKind.ControlChange)]
    [InlineData(TemplateEventKind.Bank)]
    [InlineData(TemplateEventKind.Program)]
    [InlineData(TemplateEventKind.PitchBend)]
    [InlineData(TemplateEventKind.RegisteredParameter)]
    [InlineData(TemplateEventKind.NonRegisteredParameter)]
    public void ExplicitEqualStateEventsAtDifferentTicksAreNotFolded(TemplateEventKind kind)
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        TemplateEvent first = CreateStateEvent(fixture.Project, kind, 60);
        TemplateEvent second = CreateStateEvent(fixture.Project, kind, 120);
        fixture.Voice.Events.Add(first);
        fixture.Voice.Events.Add(second);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 240, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        CanonicalMidiEvent[] retained = result.Events.ToArray()
            .Where(value => value.Source.SourceEventId == first.Id || value.Source.SourceEventId == second.Id)
            .ToArray();
        Assert.Contains(retained, value => value.Source.SourceEventId == first.Id && value.Tick == 60);
        Assert.Contains(retained, value => value.Source.SourceEventId == second.Id && value.Tick == 120);
    }

    [Fact]
    public void SameTickStateAndNoteEventsUseTheFixedSemanticRoleOrder()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        fixture.Voice.Events.Add(TemplateEvent.Bank(fixture.Project, 120, 1, 2));
        fixture.Voice.Events.Add(TemplateEvent.Program(fixture.Project, 120, 3));
        fixture.Voice.Events.Add(new TemplateEvent(fixture.Project)
        {
            Kind = TemplateEventKind.RegisteredParameter,
            Tick = 120,
            Number = 4,
            Value = 5
        });
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 120, 1, 6));
        fixture.Voice.Events.Add(new TemplateEvent(fixture.Project)
        {
            Kind = TemplateEventKind.PitchBend,
            Tick = 120,
            Value = 7
        });
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 120, 120, 61, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        CanonicalEventRole[] roles = result.Events.ToArray()
            .Where(value => value.Tick == 120)
            .Select(value => value.Role)
            .Distinct()
            .ToArray();
        Assert.Equal(
            [
                CanonicalEventRole.NoteOff,
                CanonicalEventRole.Bank,
                CanonicalEventRole.Program,
                CanonicalEventRole.Parameter,
                CanonicalEventRole.ControlChange,
                CanonicalEventRole.PitchBend,
                CanonicalEventRole.NoteOn
            ],
            roles);
    }

    [Fact]
    public void SameTickNoteOnsFollowExplicitObjectOrderInsteadOfIdOrPitch()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        TemplateEvent lowerIdAndPitch = TemplateEvent.Note(fixture.Project, 0, 120, 60, 100);
        TemplateEvent higherIdAndPitch = TemplateEvent.Note(fixture.Project, 0, 120, 70, 100);
        fixture.Voice.Events.Add(higherIdAndPitch);
        fixture.Voice.Events.Add(lowerIdAndPitch);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 480);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        CanonicalMidiEvent[] noteOns = result.Events.ToArray()
            .Where(value => value.Tick == 0 && value.Role == CanonicalEventRole.NoteOn)
            .ToArray();
        Assert.Equal(2, noteOns.Length);
        Assert.Equal(higherIdAndPitch.Id, noteOns[0].Source.SourceEventId);
        Assert.Equal(lowerIdAndPitch.Id, noteOns[1].Source.SourceEventId);
        Assert.Equal((byte)70, noteOns[0].Message.Byte1);
        Assert.Equal((byte)60, noteOns[1].Message.Byte1);
    }

    private static TemplateEvent CreateStateEvent(MidoraProject project, TemplateEventKind kind, long tick)
    {
        return kind switch
        {
            TemplateEventKind.ControlChange => TemplateEvent.ControlChange(project, tick, 1, 64),
            TemplateEventKind.Bank => TemplateEvent.Bank(project, tick, 1, 2),
            TemplateEventKind.Program => TemplateEvent.Program(project, tick, 3),
            TemplateEventKind.PitchBend => new TemplateEvent(project)
            {
                Kind = kind,
                Tick = tick,
                Value = 4
            },
            TemplateEventKind.RegisteredParameter or TemplateEventKind.NonRegisteredParameter =>
                new TemplateEvent(project)
                {
                    Kind = kind,
                    Tick = tick,
                    Number = 5,
                    Value = 6
                },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}
