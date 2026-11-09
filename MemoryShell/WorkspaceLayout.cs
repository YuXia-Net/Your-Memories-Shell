using System.Text.Json;

namespace MemoryShell;

internal sealed record WorkspaceLayout
{
    public double SidebarWidth { get; init; } = 252;
    public bool SidebarVisible { get; init; } = true;
    public double TerminalFraction { get; init; } = .70;
    public bool LowerCollapsed { get; init; }
    public int LowerTab { get; init; }
    public bool DirectoryTreeVisible { get; init; } = true;
    private static string FilePath => Path.Combine(WorkspaceStore.CustomFolder, "Layout.json");

    public static WorkspaceLayout Load()
    {
        try
        {
            var file = new FileInfo(FilePath);
            if (!file.Exists || file.Length > 8192) return new();
            var value = JsonSerializer.Deserialize<WorkspaceLayout>(File.ReadAllText(FilePath));
            if (value is null || !double.IsFinite(value.SidebarWidth) || !double.IsFinite(value.TerminalFraction)) return new();
            return value with { SidebarWidth = Math.Clamp(value.SidebarWidth, 166, 640), TerminalFraction = Math.Clamp(value.TerminalFraction, .25, .85), LowerTab = Math.Clamp(value.LowerTab, 0, 3) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(WorkspaceStore.CustomFolder);
        var temporary = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
