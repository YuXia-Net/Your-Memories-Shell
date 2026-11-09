using MemoryShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private readonly List<string> directoryNavigation = [];
    private int directoryNavigationIndex = -1;
    private Button? directoryBack;
    private Button? directoryForward;
    private readonly List<Control> directoryControls = [];
    private TreeView directoryTree = null!;
    private readonly Dictionary<TreeViewNode, string> treePaths = [];
    private readonly Dictionary<TreeViewNode, RemoteEntry> treeFiles = [];
    private readonly HashSet<TreeViewNode> loadingDirectories = [];
    private TreeViewNode? homeDirectoryNode;
    private Border? directoryTreeBorder;
    private bool directoryTreeVisible = true;
    private Action? refreshDirectoryTreeVisibility;

    private UIElement BuildDirectoryNavigation()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        directoryBack = NavigationButton(L10n.Get("S0F3067909FE0"), "\uE72B", () => StepDirectoryAsync(-1));
        directoryForward = NavigationButton(L10n.Get("S52FB26CEC44E"), "\uE72A", () => StepDirectoryAsync(1));
        bar.Children.Add(directoryBack);
        bar.Children.Add(directoryForward);
        bar.Children.Add(NavigationButton(L10n.Get("S963F3B5D1C4F"), "\uE74A", () => FileActionAsync(() => RefreshFilesAsync(ParentPath(directory), useCache: true))));
        return bar;
    }

    private Button NavigationButton(string title, string glyph, Func<Task> action)
    {
        var button = ToolbarButton(new Button(), title, new FontIcon { Glyph = glyph, FontSize = 14 }, action);
        directoryControls.Add(button);
        return button;
    }

    private Task StepDirectoryAsync(int delta)
    {
        var targetIndex = directoryNavigationIndex + delta;
        return targetIndex < 0 || targetIndex >= directoryNavigation.Count ? Task.CompletedTask :
            FileActionAsync(() => RefreshFilesAsync(directoryNavigation[targetIndex], useCache: true, navigationPosition: targetIndex));
    }

    private void RecordDirectoryNavigation(string target, int? position)
    {
        if (position is int index) directoryNavigationIndex = index;
        else if (directoryNavigationIndex < 0 || directoryNavigation[directoryNavigationIndex] != target)
        {
            if (directoryNavigationIndex + 1 < directoryNavigation.Count)
                directoryNavigation.RemoveRange(directoryNavigationIndex + 1, directoryNavigation.Count - directoryNavigationIndex - 1);
            directoryNavigation.Add(target);
            if (directoryNavigation.Count > 100) directoryNavigation.RemoveAt(0);
            directoryNavigationIndex = directoryNavigation.Count - 1;
        }
        UpdateDirectoryNavigation();
    }

    private void UpdateDirectoryNavigation()
    {
        var enabled = session is not null && !busy && !disposed;
        foreach (var control in directoryControls) control.IsEnabled = enabled;
        path.IsEnabled = enabled;
        if (addressBreadcrumb is not null) addressBreadcrumb.IsEnabled = enabled;
        if (directoryBack is not null) directoryBack.IsEnabled = enabled && directoryNavigationIndex > 0;
        if (directoryForward is not null) directoryForward.IsEnabled = enabled && directoryNavigationIndex + 1 < directoryNavigation.Count;
    }

    private Button BuildPathHistoryButton()
    {
        var button = new Button { Content = L10n.Get("SB0385CFE4B42"), MinWidth = 0, MinHeight = 29, Padding = new Thickness(7, 3, 7, 3) };
        var filter = new TextBox { PlaceholderText = L10n.Get("SE9275FC16BAF") };
        var entries = new ListView { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None, MaxHeight = 280 };
        var empty = new TextBlock { Text = L10n.Get("S39DA44143730"), Opacity = .65, Margin = new Thickness(8) };
        var body = new StackPanel { Spacing = 6, Children = { entries, empty, filter } };
        var flyout = new Flyout { Content = body, Placement = FlyoutPlacementMode.TopEdgeAlignedRight };
        void Fill()
        {
            var values = store.RemotePaths.Where(item => item.ProfileId == profile.Id && item.Path.Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                .Reverse().Select(item => item.Path).ToArray();
            entries.ItemsSource = values;
            empty.Text = filter.Text.Length > 0 ? L10n.Get("S06CD8B06CCCA") : L10n.Get("S39DA44143730");
            empty.Visibility = values.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        flyout.Opening += (_, _) => { body.Width = Math.Clamp(ActualWidth - 80, 220, 550); filter.Text = ""; Fill(); };
        filter.TextChanged += (_, _) => Fill();
        entries.ItemClick += async (_, args) =>
        {
            if (args.ClickedItem is not string selected) return;
            flyout.Hide();
            await FileActionAsync(() => RefreshFilesAsync(selected, useCache: true));
        };
        button.Flyout = flyout;
        directoryControls.Add(button);
        return button;
    }

    private UIElement BuildDirectoryTree()
    {
        directoryTree = new TreeView { SelectionMode = TreeViewSelectionMode.Single };
        TreeViewNode AddRoot(string label, string target)
        {
            var node = new TreeViewNode { Content = label, HasUnrealizedChildren = true };
            treePaths[node] = target;
            directoryTree.RootNodes.Add(node);
            return node;
        }
        AddRoot("/", "/");
        homeDirectoryNode = AddRoot(L10n.Get("S6BFF6A717A8C"), directory);
        directoryTree.Expanding += async (_, args) =>
        {
            if (session is not { } connected || !args.Node.HasUnrealizedChildren ||
                !treePaths.TryGetValue(args.Node, out var target) || !loadingDirectories.Add(args.Node)) return;
            try
            {
                var entries = await connected.ListDirectoryAsync(target, cancellation);
                if (!disposed && ReferenceEquals(session, connected) && treePaths.ContainsKey(args.Node)) PopulateDirectoryNode(args.Node, entries);
            }
            catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested) { }
            catch (Exception ex) { if (!disposed) fileStatus.Text = L10n.Format("S0ABA856CEC45", ex.Message); }
            finally { loadingDirectories.Remove(args.Node); }
        };
        directoryTree.ItemInvoked += async (_, args) =>
        {
            if (args.InvokedItem is not TreeViewNode node || !treePaths.TryGetValue(node, out var target)) return;
            if (treeFiles.TryGetValue(node, out var file))
            {
                await FileActionAsync(() => RefreshFilesAsync(ParentPath(target), useCache: true));
                fileFilter.Text = "";
                files.SelectedItem = files.Items.OfType<RemoteFileRow>().FirstOrDefault(row => row.Entry.FullPath == target);
            }
            else await FileActionAsync(() => RefreshFilesAsync(target, useCache: true));
        };
        directoryTree.AddHandler(UIElement.DoubleTappedEvent, new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler(async (_, args) =>
        {
            if (directoryTree.SelectedNode is not { } selected || !treeFiles.TryGetValue(selected, out var entry)) return;
            args.Handled = true;
            if (SelectedFile?.FullPath == entry.FullPath) await OpenSelectedFileAsync();
        }), true);
        directoryTreeBorder = new Border
        {
            BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"), BorderThickness = new Thickness(0, 0, 1, 0),
            Margin = new Thickness(0, 0, 6, 0), Child = directoryTree
        };
        return directoryTreeBorder;
    }

    private void PopulateDirectoryNode(TreeViewNode node, IReadOnlyList<RemoteEntry> entries)
    {
        var wanted = entries.Where(item => (!filePreferences.ExplorerStyle || item.IsDirectory) && !item.IsSymbolicLink &&
            (store.ShowHiddenFiles || !item.Name.StartsWith('.'))).OrderBy(item => !item.IsDirectory).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var wantedPaths = wanted.Select(item => item.FullPath).ToHashSet(StringComparer.Ordinal);
        foreach (var child in node.Children.ToArray())
        {
            if (treePaths.TryGetValue(child, out var target) && wantedPaths.Contains(target)) continue;
            ForgetDirectoryNode(child);
            node.Children.Remove(child);
        }
        var existing = node.Children.ToDictionary(child => treePaths[child], StringComparer.Ordinal);
        for (var index = 0; index < wanted.Length; index++)
        {
            var entry = wanted[index];
            if (!existing.TryGetValue(entry.FullPath, out var child))
            {
                child = new TreeViewNode { Content = entry.Name, HasUnrealizedChildren = entry.IsDirectory };
                if (!entry.IsDirectory) treeFiles[child] = entry;
                treePaths[child] = entry.FullPath;
                node.Children.Insert(index, child);
            }
            else if (node.Children.IndexOf(child) != index)
            {
                node.Children.Remove(child);
                node.Children.Insert(index, child);
            }
        }
        node.HasUnrealizedChildren = false;
    }

    private void ForgetDirectoryNode(TreeViewNode node)
    {
        foreach (var child in node.Children) ForgetDirectoryNode(child);
        treePaths.Remove(node);
        treeFiles.Remove(node);
    }

    private void UpdateDirectoryTree()
    {
        if (directoryTree is null) return;
        if (!store.ShowHiddenFiles)
        {
            foreach (var node in treePaths.Keys.Where(node => node.Parent is not null && node.Content is string name && name.StartsWith('.')).ToArray())
            {
                node.Parent?.Children.Remove(node);
                ForgetDirectoryNode(node);
            }
        }
        foreach (var node in treePaths.Where(item => item.Value == directory).Select(item => item.Key).ToArray())
            PopulateDirectoryNode(node, directoryEntries);
        var selected = treePaths.FirstOrDefault(item => item.Value == directory).Key;
        if (selected is not null) directoryTree.SelectedNode = selected;
    }

    private Task CopyRemotePathAsync() => CopyRemotePathAsync(SelectedFile?.FullPath ?? directory);

    private static Task CopyRemotePathAsync(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
        return Task.CompletedTask;
    }
}
