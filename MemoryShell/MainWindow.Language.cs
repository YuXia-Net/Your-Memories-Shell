using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private void AddLanguageSettings(SettingsTabHost tabs)
    {
        var language = new ComboBox { Header = L10n.Get("SCE9B6F1C7C23"), HorizontalAlignment = HorizontalAlignment.Stretch };
        language.Items.Add(new ComboBoxItem { Content = L10n.Get("S3185C2025FA2"), Tag = "system" });
        foreach (var option in L10n.Languages) language.Items.Add(new ComboBoxItem { Content = option.NativeName, Tag = option.Code });
        language.SelectedItem = language.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == L10n.Preference);
        var note = new InfoBar
        {
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
            Message = L10n.Get("S02483C45FA5A")
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = WorkspaceTheme.Brush("ShellErrorBrush") };
        var panel = new StackPanel { Spacing = 16, MinWidth = 300, MaxWidth = 520, Children = { language, note, error } };
        tabs.Add(L10n.Get("S127527C89C51"), SettingsPages.Section(L10n.Get("SCE9B6F1C7C23"), panel), () =>
        {
            L10n.SavePreference((string)((ComboBoxItem)language.SelectedItem).Tag);
            return Task.CompletedTask;
        });
    }
}
