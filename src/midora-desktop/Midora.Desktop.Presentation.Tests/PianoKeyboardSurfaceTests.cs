using Midora.Desktop.Presentation.Controls;

namespace Midora.Desktop.Presentation.Tests;

public sealed class PianoKeyboardSurfaceTests
{
    [Fact]
    public void PreviewVelocityIncreasesTowardTheBottomOfTheKeyboard()
    {
        Assert.Equal(31, PianoKeyboardSurface.CalculatePreviewVelocity(0, 100));
        Assert.Equal(79, PianoKeyboardSurface.CalculatePreviewVelocity(50, 100));
        Assert.Equal(127, PianoKeyboardSurface.CalculatePreviewVelocity(100, 100));
        Assert.True(
            PianoKeyboardSurface.CalculatePreviewVelocity(75, 100)
            > PianoKeyboardSurface.CalculatePreviewVelocity(25, 100));
    }
}
