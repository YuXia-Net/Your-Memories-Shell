using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MemoryShell;

internal sealed class KeyWalletPicker : ComboBox
{
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        // 收起时让滚轮继续传给页面，不改变密钥选择。
        if (IsDropDownOpen)
            base.OnPointerWheelChanged(e);
    }

    public SavedPrivateKey? SelectedKey => (SelectedItem as ComboBoxItem)?.Tag as SavedPrivateKey;
    public KeyWalletPicker(IEnumerable<SavedPrivateKey> keys)
    {
        Header = L10n.Get("KeySource");
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Items.Add(new ComboBoxItem { Content = L10n.Get("KeyFileOrSaved") });
        foreach (var key in keys.OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase))
            Items.Add(new ComboBoxItem { Content = key.Name, Tag = key });
        SelectedIndex = -1;
    }
}
