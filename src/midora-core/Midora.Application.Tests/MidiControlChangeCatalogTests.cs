using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class MidiControlChangeCatalogTests
{
    [Fact]
    public void EditableCatalogIsTheRecognizedOrdinaryControllerIntersection()
    {
        Assert.Equal(
            [0, 1, 5, 6, 7, 10, 11, 32, 38, 42, 64, 65, 66, 67,
             71, 72, 73, 74, 75, 76, 77, 78, 84, 94, 98, 99, 100, 101],
            MidiControlChangeCatalog.EditableControllers.Select(value => value.Number));
        Assert.DoesNotContain(MidiControlChangeCatalog.EditableControllers, value => value.Number is 91 or 93);
        Assert.DoesNotContain(MidiControlChangeCatalog.EditableControllers, value => value.Number >= 120);
        Assert.Equal("CC 74 - Sound Controller 5 (Filter Cutoff Frequency)",
            MidiControlChangeCatalog.Format(74));
        Assert.Equal("CC 2", MidiControlChangeCatalog.Format(2));
    }
}
