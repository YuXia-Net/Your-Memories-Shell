using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed class AppLockSetupEditor : StackPanel
{
    private readonly RadioButtons mode = new() { MaxColumns = 3 };
    private readonly Grid credentials = new() { ColumnSpacing = 20, RowSpacing = 16 };
    private AppLockDialog.CredentialInput? first;
    private AppLockDialog.CredentialInput? second;

    public AppLockKind? Kind => mode.SelectedIndex is 1 or 2 ? (AppLockKind)mode.SelectedIndex : null;

    public AppLockSetupEditor(AppLockKind? initialKind = null)
    {
        Spacing = 18;
        foreach (var label in new[] { L10n.Get("S6A12BD75A56B"), "PIN", L10n.Get("SCDA2E381D603") }) mode.Items.Add(label);
        Children.Add(mode);
        Children.Add(new TextBlock
        {
            Text = L10n.Get("S1957ECD5AF64"),
            TextWrapping = TextWrapping.Wrap, Foreground = WorkspaceTheme.Brush("ShellMutedBrush")
        });
        credentials.ColumnDefinitions.Add(new ColumnDefinition());
        credentials.ColumnDefinitions.Add(new ColumnDefinition());
        credentials.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        credentials.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(credentials);
        mode.SelectionChanged += (_, _) => Refresh();
        mode.SelectedIndex = initialKind is { } selected ? (int)selected : 0;
        SizeChanged += (_, args) => ArrangeCredentials(args.NewSize.Width);
    }

    private void Refresh()
    {
        Clear();
        credentials.Children.Clear();
        first = second = null;
        if (Kind is not { } kind) return;
        first = new AppLockDialog.CredentialInput(kind);
        second = new AppLockDialog.CredentialInput(kind);
        credentials.Children.Add(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = L10n.Get("S5332873229C3"), TextWrapping = TextWrapping.Wrap }, first } });
        credentials.Children.Add(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = L10n.Get("S47A640781DF9"), TextWrapping = TextWrapping.Wrap }, second } });
        ArrangeCredentials(ActualWidth);
    }

    private void ArrangeCredentials(double width)
    {
        var narrow = width < 500;
        mode.MaxColumns = narrow ? 1 : 3;
        for (var index = 0; index < credentials.Children.Count; index++)
        {
            var field = (FrameworkElement)credentials.Children[index];
            Grid.SetRow(field, narrow ? index : 0);
            Grid.SetColumn(field, narrow ? 0 : index);
            Grid.SetColumnSpan(field, narrow ? 2 : 1);
        }
    }

    public string? ValidationError()
    {
        if (Kind is not { } kind) return null;
        if (first?.CanSubmit != true || !AppLockStore.IsValid(kind, first.Value)) return AppLockStore.Requirement(kind);
        if (second?.CanSubmit != true || first.Value != second.Value) return L10n.Get("S1FEE59BF77DC");
        return null;
    }

    public Task<AppLockState?> CreateAsync()
    {
        var kind = Kind;
        var value = first?.Value ?? "";
        return Task.Run(() => AppLockStore.Create(kind, value));
    }

    public Task SaveAsync(AppLockStore storage)
    {
        var kind = Kind;
        var value = first?.Value ?? "";
        return storage.SetAsync(kind, value);
    }

    public void Clear() { first?.Clear(); second?.Clear(); }
}
