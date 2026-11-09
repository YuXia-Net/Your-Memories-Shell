using Microsoft.UI.Xaml;
namespace MemoryShell;
public sealed partial class MainWindow
{
    private async void VaultOverview_Click(object sender, RoutedEventArgs args) => await ShowSettingsAsync(L10n.Get("S6BEDA481D19D"));
}
