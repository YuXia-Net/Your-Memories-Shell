using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MemoryShell;

internal class ExplicitComboBox : ComboBox
{
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (IsDropDownOpen) base.OnPointerWheelChanged(e);
    }
}
