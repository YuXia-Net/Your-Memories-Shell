using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

public sealed class SessionTabView : TabView
{
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("RightContentPresenter") is ContentPresenter footer)
            footer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }
}
