using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed class KeyWalletPicker : ExplicitComboBox
{
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
