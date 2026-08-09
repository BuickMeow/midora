using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace Midora.Desktop.Presentation.Controls;

internal sealed class RenderedSurfaceAutomationPeer(
    FrameworkElement owner,
    string className) : FrameworkElementAutomationPeer(owner)
{
    protected override string GetClassNameCore() => className;

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Pane;

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override string GetNameCore()
    {
        string name = base.GetNameCore();
        return string.IsNullOrWhiteSpace(name)
            ? AutomationProperties.GetName(Owner)
            : name;
    }
}
