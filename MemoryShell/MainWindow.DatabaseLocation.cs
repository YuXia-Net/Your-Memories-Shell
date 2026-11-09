using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private async void ChangeDatabaseLocation_Click(object sender, RoutedEventArgs args)
    {
        if (database is not { } active) return;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            if (await picker.PickSingleFolderAsync() is not { } selected || database != active || closed) return;
            if (string.Equals(Path.GetFullPath(selected.Path), Path.GetDirectoryName(active.FilePath), StringComparison.OrdinalIgnoreCase)) return;
            if (uploadInProgress || restoreInProgress) throw new InvalidOperationException(L10n.Get("DatabaseMoveBusy"));
            cloudTimer?.Stop();
            SaveWorkspace();
            VaultDatabase? replacement = null;
            try
            {
                replacement = active.CopyToFolder(selected.Path);
                WorkspaceStore.SaveDatabaseFolder(selected.Path);
                database = replacement;
                replacement = null;
                active.Dispose();
                Status.Text = L10n.Get("DatabaseMoved");
            }
            finally { replacement?.Dispose(); StartCloudSchedule(); }
        }
        catch (Exception ex) { await Dialog(L10n.Get("ChangeDatabaseLocation"), ex.Message, "").ShowAsync(); }
    }
}
