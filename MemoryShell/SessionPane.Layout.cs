using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private Action<WorkspaceLayout>? saveLayout;
    internal WorkspaceLayout CapturePanelLayout(WorkspaceLayout current)
    {
        var top = RowDefinitions[0].Height.Value;
        var bottom = lowerPanelCollapsed ? expandedLowerHeight.Value : RowDefinitions[3].Height.Value;
        return current with
        {
            TerminalFraction = Math.Clamp(top / Math.Max(.001, top + bottom), .25, .85),
            LowerCollapsed = lowerPanelCollapsed, LowerTab = Math.Max(0, lower.SelectedIndex), DirectoryTreeVisible = directoryTreeVisible
        };
    }

    internal void ApplyPanelLayout(WorkspaceLayout layout, Action<WorkspaceLayout> save)
    {
        directoryTreeVisible = layout.DirectoryTreeVisible;
        refreshDirectoryTreeVisibility?.Invoke();
        RowDefinitions[0].Height = new GridLength(layout.TerminalFraction, GridUnitType.Star);
        RowDefinitions[3].Height = new GridLength(1 - layout.TerminalFraction, GridUnitType.Star);
        lower.SelectedIndex = Math.Clamp(layout.LowerTab, 0, lower.TabItems.Count - 1);
        SetLowerPanelCollapsed(layout.LowerCollapsed);
        saveLayout = save;
    }

    private void SavePanelLayout() => saveLayout?.Invoke(CapturePanelLayout(new()));

    internal void ResetPanelLayout()
    {
        directoryTreeVisible = true;
        refreshDirectoryTreeVisibility?.Invoke();
        SetLowerPanelCollapsed(false);
        RowDefinitions[0].Height = new GridLength(2.3, GridUnitType.Star);
        RowDefinitions[3].Height = new GridLength(1, GridUnitType.Star);
        lower.SelectedIndex = 0;
        SavePanelLayout();
    }

    private Grid BuildPaneSplitter()
    {
        var resting = WorkspaceTheme.Brush("ShellToolbarBrush");
        var active = WorkspaceTheme.Brush("ShellCardBrush");
        var handle = new Grid { Background = resting };
        handle.Children.Add(new Border
        {
            Width = 28, Height = 2, CornerRadius = new CornerRadius(1),
            Background = WorkspaceTheme.Brush("ShellLineBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        ToolTipService.SetToolTip(handle, L10n.Get("S0A32EBFEEFB6"));

        uint? dragging = null;
        double start = 0;
        double initial = 0;
        handle.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed || !handle.CapturePointer(e.Pointer))
                return;

            dragging = e.Pointer.PointerId;
            start = point.Position.Y;
            initial = RowDefinitions[0].ActualHeight;
            handle.Background = active;
            e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (dragging != e.Pointer.PointerId)
                return;

            var top = RowDefinitions[0];
            var bottom = RowDefinitions[3];
            var available = ActualHeight - RowDefinitions[1].ActualHeight - RowDefinitions[2].ActualHeight;
            if (available <= top.MinHeight + bottom.MinHeight)
                return;

            var height = Math.Clamp(initial + e.GetCurrentPoint(this).Position.Y - start,
                top.MinHeight, available - bottom.MinHeight);
            top.Height = new GridLength(height / available, GridUnitType.Star);
            bottom.Height = new GridLength(1 - height / available, GridUnitType.Star);
            e.Handled = true;
        };
        handle.PointerReleased += (_, e) =>
        {
            if (dragging == e.Pointer.PointerId)
                handle.ReleasePointerCapture(e.Pointer);
        };
        handle.PointerCaptureLost += (_, _) =>
        {
            dragging = null;
            handle.Background = resting;
            SavePanelLayout();
        };
        return handle;
    }
}
