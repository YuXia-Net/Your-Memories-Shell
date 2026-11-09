using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? cloudTimer;
    private long uploadedRevision;
    private bool uploadInProgress;

    private void StartCloudSchedule()
    {
        cloudTimer?.Stop();
        if (database?.HasMaster != true || store.Cloud is not { Enabled: true } settings)
            return;
        if (cloudTimer is null)
        {
            cloudTimer = Root.DispatcherQueue.CreateTimer();
            cloudTimer.Tick += async (_, _) => await UploadBackupAsync(manual: false);
        }
        cloudTimer.IsRepeating = settings.Policy == CloudBackupPolicy.Interval;
        cloudTimer.Interval = settings.Policy == CloudBackupPolicy.OnChange ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromMinutes(Math.Clamp(settings.IntervalMinutes, 1, 1440));
        cloudTimer.Start();
    }

    private void QueueChangedBackup()
    {
        if (database?.HasMaster == true && store.Cloud is { Enabled: true, Policy: CloudBackupPolicy.OnChange })
            StartCloudSchedule();
    }

    private async Task<CloudSettings?> PromptCloudSettingsAsync(CloudSettings? settings)
    {
        var editor = new CloudSettingsEditor(settings);
        var dialog = Dialog(L10n.Get("SFD590125CB74"), editor, L10n.Get("SEE4FDF7DBC31"));
        CloudSettings? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                result = editor.Read();
            }
            catch (Exception ex)
            {
                result = null;
                args.Cancel = true;
                editor.ShowError(ex.Message);
            }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }

    private void UpdateCloudSettings(CloudSettings? settings)
    {
        var previous = store.Cloud;
        store.Cloud = settings;
        try { SaveWorkspace(); }
        catch
        {
            store.Cloud = previous;
            throw;
        }
        uploadedRevision = 0;
        StartCloudSchedule();
        Status.Text = settings is null ? L10n.Get("S837A3D0DEAD5") : L10n.Get("S829EBECF3981");
    }

    private async void UploadCloud_Click(object sender, RoutedEventArgs e) => await UploadBackupAsync(manual: true);

    private async Task UploadBackupAsync(bool manual)
    {
        if (database is not { } active || unlockedLifetime is not { } lifetime || uploadInProgress || restoreInProgress)
            return;
        if (!active.HasMaster && (!manual || !await EnsureMasterAsync(active)))
            return;
        if (store.Cloud is not { } settings)
        {
            if (manual)
                Status.Text = L10n.Get("SD6E1736238A9");
            return;
        }
        if (!manual && (!settings.Enabled || uploadedRevision == vaultRevision))
            return;

        uploadInProgress = true;
        var revision = vaultRevision;
        var ct = lifetime.Token;
        var path = Path.Combine(WorkspaceStore.Folder, $".backup-{Guid.NewGuid():N}.sqlite");
        try
        {
            Status.Text = L10n.Get("S48F3A4761503");
            var snapshot = await Task.Run(active.ExportSnapshot, ct);
            await File.WriteAllBytesAsync(path, snapshot, ct);
            var name = await CloudBackup.UploadAsync(settings, path, ct);
            if (active == database && store.Cloud == settings)
            {
                uploadedRevision = revision;
                Status.Text = L10n.Format("SEBD7113A3642", name);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (active == database && store.Cloud == settings)
                Status.Text = L10n.Format("S2BB7E7800612", ex.Message);
        }
        finally
        {
            TryDelete(path);
            uploadInProgress = false;
            if (active == database && store.Cloud is { Enabled: true, Policy: CloudBackupPolicy.OnChange } &&
                uploadedRevision != vaultRevision && cloudTimer is not null)
            {
                cloudTimer.Interval = TimeSpan.FromSeconds(uploadedRevision == revision ? 2 : 60);
                cloudTimer.Start();
            }
        }
    }

    private async void BrowseCloud_Click(object sender, RoutedEventArgs e)
    {
        if (database is null || database.IsRandomOnly)
            return;
        try
        {
            var settings = store.Cloud ?? await PromptCloudSettingsAsync(null);
            if (settings is not null && unlockedLifetime is { } lifetime)
                await BrowseCloudAsync(settings, lifetime.Token);
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async Task RestoreAtStartupAsync()
    {
        var choice = Dialog(L10n.Get("SC73D0932DC28"), L10n.Get("SF89BC6410843"), L10n.Get("SBCE85A317EC1"));
        choice.SecondaryButtonText = L10n.Get("S40F585E75856");
        var source = await choice.ShowAsync();
        if (source == ContentDialogResult.Primary)
        {
            await PickLocalRestoreAsync();
        }
        else if (source == ContentDialogResult.Secondary && await PromptCloudSettingsAsync(null) is { } settings)
        {
            try { await BrowseCloudAsync(settings, CancellationToken.None); }
            catch (Exception ex)
            {
                await Dialog(L10n.Get("SFFF72A68E9F6"), ex.Message, L10n.Get("S3FD47EDCE45B")).ShowAsync();
            }
        }
    }

    private async Task BrowseCloudAsync(CloudSettings settings, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var operation = timeout.Token;
        Status.Text = L10n.Get("S9FB108122B0A");
        var backups = await CloudBackup.ListAsync(settings, operation);
        if (closed)
            return;
        if (backups.Count == 0)
        {
            Status.Text = L10n.Get("SACD918C761AA");
            await Dialog(L10n.Get("S40F585E75856"), L10n.Get("SACD918C761AA"), L10n.Get("S3FD47EDCE45B")).ShowAsync();
            return;
        }
        var list = new ListView { MaxHeight = 410, MinWidth = 440 };
        foreach (var name in backups)
            list.Items.Add(name);
        list.SelectedIndex = 0;
        var dialog = Dialog(L10n.Get("SD2F9D9D6D5B3"), list, L10n.Get("SCFA204228D41"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not string chosen)
        {
            Status.Text = L10n.Get("S9D68F6592E1D");
            return;
        }

        var stage = Path.Combine(WorkspaceStore.Folder, $".download-{Guid.NewGuid():N}.sqlite");
        try
        {
            Status.Text = L10n.Get("S33CE32292C5B");
            await CloudBackup.DownloadAsync(settings, chosen, stage, ct);
            if (!closed && !ct.IsCancellationRequested)
                await RestoreSnapshotAsync(stage);
        }
        finally { TryDelete(stage); }
    }
}


