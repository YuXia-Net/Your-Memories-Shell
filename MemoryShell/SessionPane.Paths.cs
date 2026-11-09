using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private void RememberRemotePath(string target)
    {
        var records = store.RemotePaths;
        var entry = new RemotePathEntry(profile.Id, target);
        var previous = records.IndexOf(entry);
        if (previous == records.Count - 1 && previous >= 0)
            return;

        if (previous >= 0)
            records.RemoveAt(previous);
        records.Add(entry);
        var oldest = records.Count > 500 ? records[0] : null;
        if (oldest is not null)
            records.RemoveAt(0);

        try { saveWorkspace(); }
        catch (Exception ex)
        {
            records.RemoveAt(records.Count - 1);
            if (oldest is not null)
                records.Insert(0, oldest);
            if (previous >= 0)
                records.Insert(previous, entry);
            SetStatus(L10n.Format("SF09C414E5674", ex.Message));
        }
    }

    private async Task ChooseRecentPathAsync()
    {
        var recent = store.RemotePaths.Where(item => item.ProfileId == profile.Id).Reverse().Select(item => item.Path).ToArray();
        if (recent.Length == 0)
        {
            fileStatus.Text = L10n.Get("S2664C2199F5D");
            return;
        }

        var list = new ListView { ItemsSource = recent, MaxHeight = 380, MinWidth = 350, SelectedIndex = 0 };
        if (await Dialog(L10n.Get("S61A7B2BF5088"), list, L10n.Get("SD69B5AB453F7")).ShowAsync() == ContentDialogResult.Primary &&
            list.SelectedItem is string selected)
            await FileActionAsync(() => RefreshFilesAsync(selected, useCache: true));
    }

    private Task InsertRemotePathAsync()
    {
        var target = SelectedFile?.FullPath ?? directory;
        if (target.Any(char.IsControl))
        {
            fileStatus.Text = L10n.Get("SEC4DB8BC9DA5");
            return Task.CompletedTask;
        }

        var quoted = "'" + target.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        var start = command.SelectionStart;
        command.Text = command.Text.Remove(start, command.SelectionLength).Insert(start, quoted);
        command.SelectionStart = start + quoted.Length;
        command.SelectionLength = 0;
        command.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        return Task.CompletedTask;
    }
}
