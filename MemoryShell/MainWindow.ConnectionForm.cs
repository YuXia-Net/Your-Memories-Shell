using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private TabViewItem? memoryTab;
    private MemoryTerminalView? memoryTerminal;

    private ContentDialog ConnectionDialog(string title, bool save, TextBlock error,
        StackPanel device, StackPanel auth, ProxyEditor proxy, JumpEditor jump, StackPanel? probe = null)
    {
        var width = Math.Clamp(Root.ActualWidth - 48, 380, 940);
        var pages = new SettingsPages(Math.Clamp(Root.ActualHeight - 210, 260, 650));
        var general = new StackPanel { Spacing = 22 };
        general.Children.Add(SettingsPages.Section(L10n.Get("S82479CB6CA73"), device));
        general.Children.Add(SettingsPages.Section(L10n.Get("S6C422A32FA7A"), auth));
        pages.Add(L10n.Get("S7F4405D07770"), general);
        pages.Add(L10n.Get("S8DFE5E76E0EC"), new StackPanel
        {
            Spacing = 24,
            Children = { SettingsPages.Section(L10n.Get("S556000791F95"), proxy), SettingsPages.Section(L10n.Get("S542579D62B1C"), jump) }
        });
        if (probe is not null) pages.Add(L10n.Get("MonitorWallTitle"), SettingsPages.Section(L10n.Get("MonitorWallTitle"), probe));
        var layout = new Grid { Width = width - 48, RowSpacing = 10 };
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(pages);
        Grid.SetRow(error, 1);
        error.Foreground = WorkspaceTheme.Brush("ShellErrorBrush");
        error.Visibility = Visibility.Collapsed;
        error.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
        {
            error.Visibility = error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (error.Text.Length > 0)
                pages.Select(error.Text.Contains(L10n.Get("S5E84EA61E838"), StringComparison.CurrentCultureIgnoreCase) ||
                    error.Text.Contains(L10n.Get("SE7988FBD7AAB"), StringComparison.CurrentCultureIgnoreCase) ? 1 : 0);
        });
        layout.Children.Add(error);
        var dialog = Dialog(title, layout, save ? L10n.Get("SA3030BF8F16D") : L10n.Get("SA5574109F020"));
        dialog.Resources["ContentDialogMaxWidth"] = width;
        dialog.Resources["ContentDialogMinWidth"] = Math.Min(340, width);
        return dialog;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));

    private void OpenMemoryTerminal()
    {
        if (memoryTab is not null)
        {
            Sessions.SelectedItem = memoryTab;
            return;
        }

        memoryTerminal = new MemoryTerminalView(store.Appearance);
        memoryTerminal.ApplyWorkspaceAppearance(workspaceAppearance, workspaceWallpaper, store.Appearance);
        memoryTab = new TabViewItem
        {
            Header = L10n.Get("SB676597E1697"), IconSource = new SymbolIconSource { Symbol = Symbol.Remote }, Content = memoryTerminal
        };
        Sessions.TabItems.Add(memoryTab);
        Sessions.SelectedItem = memoryTab;
    }

    private void CloseMemoryTerminal()
    {
        var tab = memoryTab;
        memoryTab = null;
        memoryTerminal?.Dispose();
        memoryTerminal = null;
        if (tab is not null && !closed)
            Sessions.TabItems.Remove(tab);
    }
}





