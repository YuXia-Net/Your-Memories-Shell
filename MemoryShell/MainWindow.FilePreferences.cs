using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MemoryShell;

internal enum FileColumnLayout { Automatic, Compact, Detailed }
internal enum FileSortOrder { Name, Size, Modified, Kind }

internal sealed record FilePanelPreferences(
    FileColumnLayout Columns = FileColumnLayout.Automatic,
    FileSortOrder SortBy = FileSortOrder.Name,
    bool Descending = false,
    bool FoldersFirst = true,
    int RowHeight = 29,
    string? DownloadFolder = null,
    bool ExplorerStyle = true);

public sealed partial class MainWindow
{
    private FilePanelPreferences? filePanelPreferences;
    private FilePanelPreferences FilePreferences => filePanelPreferences ??= ReadFilePreferences();
    private void SaveInitialFileStyle(bool explorer)
    {
        var next = FilePreferences with { ExplorerStyle = explorer };
        Directory.CreateDirectory(WorkspaceStore.CustomFolder);
        File.WriteAllText(FilePreferencesPath, JsonSerializer.Serialize(next, FilePreferencesJson));
        filePanelPreferences = next;
    }
    private static string FilePreferencesPath => Path.Combine(WorkspaceStore.CustomFolder, "FilePanel.json");
    private static readonly JsonSerializerOptions FilePreferencesJson = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };

    private static FilePanelPreferences ReadFilePreferences()
    {
        try
        {
            var file = new FileInfo(FilePreferencesPath);
            if (!file.Exists || file.Length > 16 * 1024)
                return new();
            using var stream = file.OpenRead();
            var result = JsonSerializer.Deserialize<FilePanelPreferences>(stream, FilePreferencesJson);
            if (result is not null && Enum.IsDefined(result.Columns) && Enum.IsDefined(result.SortBy))
                return result with
                {
                    RowHeight = Math.Clamp(result.RowHeight, 24, 52),
                    DownloadFolder = Path.IsPathFullyQualified(result.DownloadFolder ?? "") ? result.DownloadFolder : null
                };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        return new();
    }

    private void AddFileSettings(SettingsTabHost tabs)
    {
        if (database is null)
            return;
        var current = FilePreferences;
        var style = Choice(L10n.Get("FileStyle"), current.ExplorerStyle ? 0 : 1, L10n.Get("ExplorerStyle"), L10n.Get("CompactStyle"));
        var columns = Choice(L10n.Get("S3CB047F0E562"), (int)current.Columns, L10n.Get("S7EB336E42CB5"), L10n.Get("SAA55960E4B02"), L10n.Get("SCA3634C751EF"));
        var sort = Choice(L10n.Get("S473BEEA442E4"), (int)current.SortBy, L10n.Get("SD44E9B3D3B31"), L10n.Get("S50DB7447B966"), L10n.Get("S257BBCC44C83"), L10n.Get("SBA40014FF496"));
        var order = Choice(L10n.Get("S45D010A0AAC0"), current.Descending ? 1 : 0, L10n.Get("SCA715C0D8D83"), L10n.Get("S4B77C3546823"));
        var foldersFirst = new CheckBox { Content = L10n.Get("S356311F048CB"), IsChecked = current.FoldersFirst };
        var height = new NumberBox
        {
            Header = L10n.Get("SE3918D3115F7"), Minimum = 24, Maximum = 52, Value = current.RowHeight,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var folder = new TextBox { Header = L10n.Get("SA860597F3906"), PlaceholderText = L10n.Get("S91F41061DB40"), Text = current.DownloadFolder ?? "" };
        var error = ErrorText();
        var choose = new Button { Content = L10n.Get("SE5FE24CA742F") };
        choose.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                if (await picker.PickSingleFolderAsync() is { } selected)
                    folder.Text = selected.Path;
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        var panel = new StackPanel { Spacing = 20, MinWidth = 285, Children = {
            SettingsPages.Section(L10n.Get("FileStyle"), style, columns, height),
            SettingsPages.Section(L10n.Get("S473BEEA442E4"), sort, order, foldersFirst),
            SettingsPages.Section(L10n.Get("SA860597F3906"), folder, choose, error) } };
        tabs.Add(L10n.Get("S4E25377BA960"), panel, () =>
        {
            try
            {
                if (double.IsNaN(height.Value))
                    throw new ArgumentException(L10n.Get("S6F64D8E8A8D9"));
                var download = folder.Text.Trim();
                if (download.Length > 0 && (!Path.IsPathFullyQualified(download) || !Directory.Exists(download)))
                    throw new ArgumentException(L10n.Get("SDC2815019A77"));
                var next = new FilePanelPreferences((FileColumnLayout)columns.SelectedIndex, (FileSortOrder)sort.SelectedIndex,
                    order.SelectedIndex == 1, foldersFirst.IsChecked == true, (int)Math.Clamp(height.Value, 24, 52),
                    download.Length == 0 ? null : Path.GetFullPath(download), style.SelectedIndex == 0);
                Directory.CreateDirectory(WorkspaceStore.CustomFolder);
                var temporary = Path.Combine(WorkspaceStore.CustomFolder, $".files-{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(next, FilePreferencesJson));
                    File.Move(temporary, FilePreferencesPath, overwrite: true);
                }
                finally { TryDelete(temporary); }

                filePanelPreferences = next;
                foreach (var pane in panes.Values)
                    pane.ApplyFilePreferences(next);
                Status.Text = L10n.Get("S35DF318217F4");
            }
            catch (Exception ex) { error.Text = ex.Message; throw; }
            return Task.CompletedTask;
        });

        static ComboBox Choice(string header, int selected, params string[] options)
        {
            var box = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var option in options)
                box.Items.Add(option);
            box.SelectedIndex = selected;
            return box;
        }
    }
}
