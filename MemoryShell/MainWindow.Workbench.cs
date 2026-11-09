using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private double sidebarPreferredWidth = 252;
    private WorkspaceLayout workspaceLayout = new();
    private bool layoutReady;

    private void RestoreWorkspaceLayout()
    {
        workspaceLayout = WorkspaceLayout.Load();
        sidebarPreferredWidth = workspaceLayout.SidebarWidth;
        SetSidebarVisible(workspaceLayout.SidebarVisible);
        layoutReady = true;
    }

    private void SaveWorkspaceLayout()
    {
        if (!layoutReady || closed) return;
        workspaceLayout = workspaceLayout with { SidebarWidth = sidebarPreferredWidth, SidebarVisible = SidebarPane.Visibility == Visibility.Visible };
        try { workspaceLayout.Save(); }
        catch (Exception ex) { Status.Text = L10n.Format("S315759AD125C", ex.Message); }
    }
    private uint? sidebarPointer;
    private double sidebarDragOrigin;
    private double sidebarDragWidth;

    private void Sessions_AddTabButtonClick(TabView sender, object args) => AddStartTab();

    private void AddStartTab()
    {
        var tab = new TabViewItem { Header = L10n.Get("S9C5D341609CD"), Tag = "start", IsClosable = true };
        Sessions.TabItems.Add(tab);
        Sessions.SelectedItem = tab;
    }

    private void AttachWelcomePage()
    {
        if (Welcome is null || Sessions.SelectedItem is not TabViewItem selected ||
            (selected != StartTab && !Equals(selected.Tag, "start"))) return;
        if (ReferenceEquals(selected.Content, Welcome)) return;
        if (Welcome.Parent is TabViewItem parent) parent.Content = null;
        foreach (var item in Sessions.TabItems.OfType<TabViewItem>())
            if (ReferenceEquals(item.Content, Welcome)) item.Content = null;
        selected.Content = Welcome;
    }

    private void InitializeWorkspaceShortcuts()
    {
        Add(VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, AddStartTab);
        Add(VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => NewProfile_Click(this, new RoutedEventArgs()));
        Add(VirtualKey.L, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => ConnectionManager_Click(this, new RoutedEventArgs()));
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                if (database is null || openingWorkspace) return;
                action();
                args.Handled = true;
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }
    }

    private void SidebarGrip_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed || !SidebarGrip.CapturePointer(args.Pointer)) return;
        sidebarPointer = args.Pointer.PointerId;
        sidebarDragOrigin = point.Position.X;
        sidebarDragWidth = SidebarColumn.ActualWidth;
        args.Handled = true;
    }

    private void SidebarGrip_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (sidebarPointer != args.Pointer.PointerId) return;
        sidebarPreferredWidth = Math.Clamp(sidebarDragWidth + args.GetCurrentPoint(Root).Position.X - sidebarDragOrigin,
            166, Math.Max(166, Root.ActualWidth - 360));
        SetSidebarWidth(Root.ActualWidth);
        args.Handled = true;
    }

    private void SidebarGrip_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (sidebarPointer == args.Pointer.PointerId) SidebarGrip.ReleasePointerCapture(args.Pointer);
    }

    private void SidebarGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        sidebarPointer = null;
        SaveWorkspaceLayout();
    }
    private void SidebarGrip_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args) => ResetLayout_Click(sender, new RoutedEventArgs());

    private void ResetLayout_Click(object sender, RoutedEventArgs args)
    {
        workspaceLayout = new();
        sidebarPreferredWidth = workspaceLayout.SidebarWidth;
        SetSidebarVisible(true);
        SetSidebarMode(Sessions.SelectedItem is TabViewItem tab && panes.ContainsKey(tab));
        foreach (var pane in panes.Values) pane.ResetPanelLayout();
        SaveWorkspaceLayout();
    }
}
