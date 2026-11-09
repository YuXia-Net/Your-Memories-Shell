using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private static readonly Regex TemplateParameter = new(@"\{\{([A-Za-z_][A-Za-z0-9_]{0,31})\}\}", RegexOptions.CultureInvariant);
    private const int MaxTemplateParameters = 24;
    private readonly ListView commandList = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ComboBox commandGroups = new() { MinWidth = 122, MaxWidth = 220 };
    private readonly TextBox commandFilter = new() { PlaceholderText = L10n.Get("S6B67C4C4A039") };
    private readonly TextBlock commandEmpty = new() { Opacity = .7, Margin = new Thickness(8) };
    private readonly List<string?> commandGroupValues = [];
    private QuickCommand[] displayedCommands = [];
    private bool updatingCommandGroups;

    private UIElement BuildCommandPane()
    {
        var panel = new Grid { Padding = new Thickness(8, 0, 8, 8), RowSpacing = 6 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        toolbar.Children.Add(Button(L10n.Get("SD5FB4378C479"), () => EditCommandTemplateAsync(null)));
        var edit = Button(L10n.Get("S051836569928"), () => SelectedCommand is { } item ? EditCommandTemplateAsync(item) : Task.CompletedTask);
        var send = Button(L10n.Get("SEDECF0AE6E51"), RunSelectedCommandAsync);
        toolbar.Children.Add(edit);
        toolbar.Children.Add(send);
        edit.IsEnabled = send.IsEnabled = false;
        commandList.SelectionChanged += (_, _) => edit.IsEnabled = send.IsEnabled = SelectedCommand is not null;
        var menu = new MenuFlyout();
        AddMenu(L10n.Get("SEDECF0AE6E51"), TryRunSelectedCommandAsync);
        AddMenu(L10n.Get("S051836569928"), () => SelectedCommand is { } selected ? EditCommandTemplateAsync(selected) : Task.CompletedTask);
        AddMenu(L10n.Get("S2F9DAA828907"), RemoveCommandAsync);
        commandList.ContextFlyout = menu;
        commandList.RightTapped += (_, args) =>
        {
            commandList.SelectedItem = null;
            for (var node = args.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is ListViewItem item) { commandList.SelectedItem = item; break; }
                if (ReferenceEquals(node, commandList)) break;
            }
        };
        menu.Opening += (_, _) =>
        {
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>()) item.IsEnabled = SelectedCommand is not null;
        };
        void AddMenu(string title, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = title };
            item.Click += async (_, _) =>
            {
                try { await action(); } catch (Exception ex) { if (!disposed) SetStatus(ex.Message); }
            };
            menu.Items.Add(item);
        }
        panel.Children.Add(toolbar);

        var filters = new Grid { ColumnSpacing = 6 };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters.ColumnDefinitions.Add(new ColumnDefinition());
        AutomationProperties.SetName(commandGroups, L10n.Get("SF16B0A886158"));
        filters.Children.Add(commandGroups);
        SetColumn(commandFilter, 1);
        filters.Children.Add(commandFilter);
        SetRow(filters, 1);
        panel.Children.Add(filters);

        commandGroups.SelectionChanged += (_, _) =>
        {
            if (!updatingCommandGroups)
                FilterCommands();
        };
        commandFilter.TextChanged += (_, _) => FilterCommands();
        commandList.DoubleTapped += async (_, _) => await TryRunSelectedCommandAsync();
        commandList.KeyDown += async (_, args) =>
        {
            if (args.Key != VirtualKey.Enter)
                return;
            args.Handled = true;
            await TryRunSelectedCommandAsync();
        };

        SetRow(commandList, 2);
        panel.Children.Add(commandList);
        SetRow(commandEmpty, 2);
        panel.Children.Add(commandEmpty);
        RefreshCommands();
        return BuildCommandWorkspace(panel);
    }

    private string? SelectedCommandGroup => commandGroups.SelectedIndex >= 0 && commandGroups.SelectedIndex < commandGroupValues.Count
        ? commandGroupValues[commandGroups.SelectedIndex] : null;

    private QuickCommand? SelectedCommand => commandList.SelectedIndex >= 0 && commandList.SelectedIndex < displayedCommands.Length
        ? displayedCommands[commandList.SelectedIndex] : null;

    private static string GetGroup(QuickCommand command) => command.Group ?? "";

    private void RefreshCommands()
    {
        var group = SelectedCommandGroup;
        updatingCommandGroups = true;
        try
        {
            commandGroups.Items.Clear();
            commandGroupValues.Clear();
            commandGroups.Items.Add(L10n.Get("S140E85A15CFE"));
            commandGroupValues.Add(null);
            foreach (var name in store.Commands.Select(GetGroup).Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase))
            {
                commandGroups.Items.Add(name.Length == 0 ? L10n.Get("SE86EA3F280D9") : name);
                commandGroupValues.Add(name);
            }
            commandGroups.SelectedIndex = Math.Max(0, commandGroupValues.IndexOf(group));
        }
        finally
        {
            updatingCommandGroups = false;
        }
        FilterCommands();
    }

    private void FilterCommands()
    {
        var previous = SelectedCommand;
        var group = SelectedCommandGroup;
        var query = commandFilter.Text.Trim();
        displayedCommands = store.Commands
            .Where(item => (group is null || GetGroup(item) == group) && MatchesCommand(item, query))
            .OrderBy(GetGroup, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        commandList.Items.Clear();
        foreach (var item in displayedCommands)
        {
            var groupName = GetGroup(item);
            var details = new StackPanel { Spacing = 2 };
            details.Children.Add(new TextBlock
            {
                Text = groupName.Length == 0 ? item.Name : $"{groupName} · {item.Name}",
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            details.Children.Add(new TextBlock
            {
                Text = item.Text, MaxLines = 1, TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis, Opacity = .65, FontFamily = new FontFamily("Consolas")
            });
            commandList.Items.Add(new ListViewItem { Content = details, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        commandList.SelectedIndex = Array.FindIndex(displayedCommands, item => ReferenceEquals(item, previous));
        commandEmpty.Text = store.Commands.Count == 0 ? L10n.Get("SEE3360ED4F76") : L10n.Get("S2E2FC2C95A2A");
        commandEmpty.Visibility = displayedCommands.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool MatchesCommand(QuickCommand item, string query) => query.Length == 0 ||
        item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        item.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        GetGroup(item).Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private async Task EditCommandTemplateAsync(QuickCommand? original, string? initialText = null)
    {
        var name = new TextBox { Header = L10n.Get("SD44E9B3D3B31"), Text = original?.Name ?? "", MaxLength = 128 };
        var group = new TextBox { Header = L10n.Get("SD4163CE3B078"), Text = original?.Group ?? SelectedCommandGroup ?? "", MaxLength = 80 };
        var body = new TextBox
        {
            Header = L10n.Get("S928F87D4507B"), Text = original?.Text ?? initialText ?? "", AcceptsReturn = true, MaxLength = 16384,
            MinHeight = 90, MaxHeight = 220, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas")
        };
        var hint = new TextBlock
        {
            Text = L10n.Get("S869F728CD0DB"),
            TextWrapping = TextWrapping.Wrap, Opacity = .7
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var form = new StackPanel { Spacing = 8, MinWidth = 330, Children = { name, group, body, hint, error } };
        var dialog = Dialog(original is null ? L10n.Get("SBD94191A5DA7") : L10n.Get("SF9DE3D7740BC"), new ScrollViewer { Content = form, MaxHeight = 460 }, L10n.Get("SA3030BF8F16D"));
        QuickCommand? saved = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var normalized = body.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(normalized))
            {
                error.Text = L10n.Get("S731F03C5CF17");
                args.Cancel = true;
                return;
            }
            if (normalized.Any(c => char.IsControl(c) && c is not ('\n' or '\t')) || GetParameters(normalized).Length > MaxTemplateParameters)
            {
                error.Text = L10n.Format("SDD207BBF1722", MaxTemplateParameters);
                args.Cancel = true;
                return;
            }
            var item = new QuickCommand(name.Text.Trim(), normalized, group.Text.Trim());
            if (store.Commands.Any(existing => !ReferenceEquals(existing, original) &&
                string.Equals(GetGroup(existing), GetGroup(item), StringComparison.CurrentCultureIgnoreCase) &&
                string.Equals(existing.Name, item.Name, StringComparison.CurrentCultureIgnoreCase)))
            {
                error.Text = L10n.Get("S952C50A5E18E");
                args.Cancel = true;
                return;
            }
            var index = original is null ? store.Commands.Count : store.Commands.FindIndex(existing => ReferenceEquals(existing, original));
            if (index < 0)
            {
                error.Text = L10n.Get("S4F8A26B5AE32");
                args.Cancel = true;
                return;
            }
            if (original is null)
                store.Commands.Add(item);
            else
                store.Commands[index] = item;

            try
            {
                saveWorkspace();
                saved = item;
            }
            catch (Exception ex)
            {
                if (original is null)
                    store.Commands.RemoveAt(index);
                else
                    store.Commands[index] = original;
                error.Text = ex.Message;
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || saved is null || disposed)
            return;
        commandFilter.Text = "";
        RefreshCommands();
        commandGroups.SelectedIndex = commandGroupValues.IndexOf(GetGroup(saved));
        commandList.SelectedIndex = Array.FindIndex(displayedCommands, item => ReferenceEquals(item, saved));
    }

    private static string[] GetParameters(string text) => TemplateParameter.Matches(text)
        .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();

    private async Task TryRunSelectedCommandAsync()
    {
        try { await RunSelectedCommandAsync(); }
        catch (Exception ex) { if (!disposed) SetStatus(ex.Message); }
    }

    private Task RunSelectedCommandAsync() => SelectedCommand is { } selected
        ? RunQuickCommandAsync(selected) : Task.CompletedTask;

    private async Task RunQuickCommandAsync(QuickCommand selected)
    {
        if (session is null || disposed)
            return;
        var parameters = GetParameters(selected.Text);
        if (parameters.Length == 0)
        {
            await SendCommandAsync(selected.Text, false);
            return;
        }
        if (parameters.Length > MaxTemplateParameters)
        {
            SetStatus(L10n.Format("SC7C954185642", MaxTemplateParameters));
            return;
        }

        var values = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var form = new StackPanel { Spacing = 8, MinWidth = 320 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        foreach (var parameter in parameters)
        {
            var input = new TextBox { Header = parameter, MaxLength = 512 };
            values.Add(parameter, input);
            form.Children.Add(input);
        }
        form.Children.Add(error);
        var dialog = Dialog(L10n.Format("S34A3D74C85F9", selected.Name), new ScrollViewer { Content = form, MaxHeight = 420 }, L10n.Get("S2B1820D132B0"));
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!values.Values.Any(input => input.Text.Any(char.IsControl)))
                return;
            error.Text = L10n.Get("S552D3B2A15E6");
            args.Cancel = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || disposed || session is null)
            return;
        // Pass values as positional arguments rather than interpolating them into shell source.
        var indexes = parameters.Select((name, index) => (name, index))
            .ToDictionary(item => item.name, item => item.index + 1, StringComparer.Ordinal);
        var script = TemplateParameter.Replace(selected.Text, match =>
            "\"${" + indexes[match.Groups[1].Value].ToString(System.Globalization.CultureInfo.InvariantCulture) + "}\"");
        var arguments = string.Join(' ', parameters.Select(name => QuotePosixArgument(values[name].Text)));
        var resolved = "sh -c " + QuotePosixArgument(script) + " memoryshell " + arguments;
        if (resolved.Length > 65536)
        {
            SetStatus(L10n.Get("S3970D2CAA5DE"));
            return;
        }
        await SendCommandAsync(resolved, false);
    }

    private static string QuotePosixArgument(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private async Task RemoveCommandAsync()
    {
        if (SelectedCommand is not { } selected || !await ConfirmAsync(L10n.Get("S2593D331D49A"), L10n.Format("S64C3878B7399", selected.Name), L10n.Get("S2F9DAA828907")) || disposed)
            return;

        var index = store.Commands.FindIndex(item => ReferenceEquals(item, selected));
        if (index < 0)
            return;
        store.Commands.RemoveAt(index);
        try { saveWorkspace(); }
        catch (Exception ex)
        {
            store.Commands.Insert(index, selected);
            SetStatus(ex.Message);
        }
        RefreshCommands();
    }
}


