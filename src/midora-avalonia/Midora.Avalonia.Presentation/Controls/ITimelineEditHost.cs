using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Presentation.Controls;

public interface ITimelineEditHost
{
    bool CanEdit { get; }
    long DefaultNoteLengthTicks { get; }
    TimelineRenderItem CreateNote(int lane, long startTick, long lengthTicks);
    void MoveItems(IReadOnlyList<TimelineRenderItem> items, long tickDelta, int laneDelta);
    void ResizeItem(TimelineRenderItem item, long newStartTick, long newEndTick);
    void EraseItems(IReadOnlyList<TimelineRenderItem> items);
    bool SplitItem(TimelineRenderItem item, long tick);
    void SetVelocity(IReadOnlyList<TimelineRenderItem> items, double velocity);
    void SetEventValue(TimelineRenderItem item, double value);
    void BeginEditTransaction();
    void EndEditTransaction();
}
