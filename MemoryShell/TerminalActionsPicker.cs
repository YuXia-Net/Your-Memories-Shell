using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed class TerminalActionsPicker : StackPanel
{
    private readonly RadioButtons position = new() { Header = L10n.Get("TerminalActionsPosition"), MaxColumns = 3 };
    private bool preferLeft;
    public bool AtTabs
    {
        get => position.SelectedIndex != 2;
        set => position.SelectedIndex = value ? preferLeft ? 0 : 1 : 2;
    }
    public bool OnLeft
    {
        get => preferLeft;
        set { preferLeft = value; if (AtTabs) position.SelectedIndex = value ? 0 : 1; }
    }
    public event EventHandler? SelectionChanged;

    public TerminalActionsPicker(WorkspaceAppearance appearance, bool showHeader = true)
    {
        if (!showHeader) position.Header = null;
        position.Items.Add(Choice(true, true, "TerminalActionsLeft"));
        position.Items.Add(Choice(true, false, "TerminalActionsRight"));
        position.Items.Add(Choice(false, false, "TerminalActionsCommand"));
        preferLeft = appearance.TerminalActionsOnLeft;
        AtTabs = appearance.TerminalActionsAtTabs;
        position.SelectionChanged += (_, _) =>
        {
            if (AtTabs) preferLeft = position.SelectedIndex == 0;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        Children.Add(position);
        SizeChanged += (_, args) => position.MaxColumns = args.NewSize.Width >= 660 ? 3 : args.NewSize.Width >= 440 ? 2 : 1;
    }

    private static RadioButton Choice(bool atTabs, bool onLeft, string label)
    {
        var preview = new Grid { MinWidth = 170, Height = 94, Padding = new Thickness(6), RowSpacing = 4,
            Background = WorkspaceTheme.Brush("ShellToolbarBrush"), CornerRadius = new CornerRadius(5) };
        preview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        preview.RowDefinitions.Add(new RowDefinition());
        preview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(19) });
        var top = new Grid();
        var tabGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3,
            HorizontalAlignment = onLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        var arrow = new FontIcon { Glyph = onLeft ? "\uE72B" : "\uE72A", FontSize = 10,
            Foreground = WorkspaceTheme.Brush("ShellMutedBrush"), VerticalAlignment = VerticalAlignment.Center };
        if (onLeft) tabGroup.Children.Add(arrow);
        for (var i = 0; i < 2; i++)
            tabGroup.Children.Add(new Border { Width = 23, Height = 16, BorderThickness = new Thickness(1, 1, 1, 0),
                BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"), Background = WorkspaceTheme.Brush("ShellCardBrush"), CornerRadius = new CornerRadius(3, 3, 0, 0) });
        if (!onLeft) tabGroup.Children.Add(arrow);
        top.Children.Add(tabGroup);
        var terminal = new Border { Background = WorkspaceTheme.Brush("ShellBaseBrush"), CornerRadius = new CornerRadius(2),
            Child = new TextBlock { Text = ">_", FontSize = 13, Margin = new Thickness(5, 0, 0, 0), Foreground = WorkspaceTheme.Brush("ShellMutedBrush") } };
        var command = new Grid { ColumnSpacing = 8 };
        command.ColumnDefinitions.Add(new ColumnDefinition()); command.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        command.Children.Add(new Border { BorderThickness = new Thickness(1), BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"), CornerRadius = new CornerRadius(3) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = onLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right };
        foreach (var glyph in new[] { "\uE945", "\uE8C8", "\uE713" })
            actions.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, Foreground = WorkspaceTheme.Brush("ShellAccentBrush") });
        if (!atTabs) Grid.SetColumn(actions, 1);
        (atTabs ? top : command).Children.Add(actions);
        preview.Children.Add(top); Grid.SetRow(terminal, 1); preview.Children.Add(terminal);
        Grid.SetRow(command, 2); preview.Children.Add(command);
        return new RadioButton { HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 6, Children = { preview, new TextBlock { Text = L10n.Get(label), MaxWidth = 220,
                TextAlignment = TextAlignment.Left, TextWrapping = TextWrapping.Wrap } } } };
    }
}
