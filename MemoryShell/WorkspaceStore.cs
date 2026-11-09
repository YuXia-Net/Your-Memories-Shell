using System.Security.Cryptography;
using System.Text.Json;
using MemoryShell.Services;

namespace MemoryShell;

internal sealed record SavedConnection(ConnectionProfile Profile, string? Secret, byte[]? PrivateKey);
internal sealed record DeletedConnection(SavedConnection Connection, DateTimeOffset DeletedAt);
internal sealed record SavedHostKey(string Host, int Port, string Algorithm, string Fingerprint);
internal sealed record QuickCommand(string Name, string Text, string Group = "");
internal sealed record ConnectionEvent(DateTimeOffset Timestamp, string Name, string Target, bool Connected, string Protocol = "SSH", Guid? ProfileId = null);
internal sealed record CommandEvent(Guid ProfileId, string Text);
internal sealed record RemotePathEntry(Guid ProfileId, string Path);
internal sealed record TransferEntry(Guid ProfileId, DateTimeOffset Timestamp, string Items, string Result, string Detail);
internal sealed record SavedJumpPreset(Guid Id, string Name, SshJumpSettings Settings);
internal sealed record SavedProxyPreset(Guid Id, string Name, SshProxySettings Settings);

internal sealed class WorkspaceStore
{
    private static readonly string WorkspaceFolder = Path.Combine(GetApplicationFolder(), "Workspace");
    internal static readonly string Folder = Path.Combine(WorkspaceFolder, "data");
    internal static readonly string CustomFolder = Path.Combine(WorkspaceFolder, "Custom");
    private static string DatabaseLocationFile => Path.Combine(CustomFolder, "DatabaseLocation.json");
    internal static string VaultPath
    {
        get
        {
            try
            {
                if (File.Exists(DatabaseLocationFile) && new FileInfo(DatabaseLocationFile).Length < 4096)
                {
                    var folder = JsonSerializer.Deserialize<string>(File.ReadAllText(DatabaseLocationFile));
                    if (folder is not null && Path.IsPathFullyQualified(folder)) return Path.Combine(folder, "workspace.sqlite");
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { }
            return Path.Combine(Folder, "workspace.sqlite");
        }
    }
    internal static void SaveDatabaseFolder(string folder)
    {
        if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException("Invalid database folder.");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(CustomFolder);
        var stage = DatabaseLocationFile + ".tmp";
        File.WriteAllText(stage, JsonSerializer.Serialize(Path.GetFullPath(folder)));
        File.Move(stage, DatabaseLocationFile, true);
    }

    public List<SavedConnection> Connections { get; init; } = [];
    public List<string> DeviceGroups { get; init; } = [];
    public List<DeletedConnection> DeletedConnections { get; init; } = [];
    public List<SavedHostKey> HostKeys { get; init; } = [];
    public List<QuickCommand> Commands { get; init; } = [];
    public List<ConnectionEvent> ConnectionHistory { get; init; } = [];
    public List<CommandEvent> CommandHistory { get; init; } = [];
    public List<RemotePathEntry> RemotePaths { get; init; } = [];
    public List<TransferEntry> Transfers { get; init; } = [];
    public bool HideDeviceAddress { get; set; }
    public bool ClearCommandAfterSend { get; set; } = true;
    public bool ShowHiddenFiles { get; set; } = true;
    public int DirectoryCacheSeconds { get; set; }
    public CloudSettings? Cloud { get; set; }
    public ProbeLayoutPreferences ProbeLayout { get; set; } = new();
    public TerminalAppearance? Appearance { get; set; }
    public List<RdpProfile> RemoteDesktops { get; init; } = [];
    public List<SavedProxyPreset> ProxyPresets { get; init; } = [];
    public List<SavedJumpPreset> JumpPresets { get; init; } = [];

    private static string GetApplicationFolder()
    {
        var runtime = new DirectoryInfo(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory);
        if (string.Equals(runtime.Name, "dll", StringComparison.OrdinalIgnoreCase) && runtime.Parent is { } launcher &&
            (File.Exists(Path.Combine(launcher.FullName, "Your-Memories-Shell.exe")) || File.Exists(Path.Combine(launcher.FullName, "MemoryShell.exe"))))
            return launcher.FullName;
        return runtime.FullName;
    }

    public static FileStream Claim()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Directory.CreateDirectory(CustomFolder);
            return new FileStream(Path.Combine(Folder, ".workspace.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.Read);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        {
            throw new IOException(L10n.Get("S7F80F62E55A9"), ex);
        }
    }

    public static (WorkspaceStore Store, VaultDatabase Database)? TryUnlockForCurrentUser()
    {
        var database = VaultDatabase.OpenForCurrentUser(VaultPath);
        if (database is null)
            return null;
        try { return (Read(database), database); }
        catch { database.Dispose(); throw; }
    }

    public static (WorkspaceStore Store, VaultDatabase Database) Unlock(string? passphrase)
    {
        var database = VaultDatabase.Open(VaultPath, passphrase ?? throw new InvalidOperationException(L10n.Get("S7B1696E0F62A")), remember: true);
        try { return (Read(database), database); }
        catch { database.Dispose(); throw; }
    }

    public static (WorkspaceStore Store, VaultDatabase Database) Create(string? passphrase, bool randomOnly = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VaultPath)!);
        var store = new WorkspaceStore();
        var payload = JsonSerializer.SerializeToUtf8Bytes(store);
        try
        {
            return (store, VaultDatabase.Create(VaultPath, passphrase, payload, randomOnly));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static WorkspaceStore Read(VaultDatabase database)
    {
        var payload = database.Read();
        try
        {
            var result = JsonSerializer.Deserialize<WorkspaceStore>(payload);
            if (result is null || result.Connections is null || result.DeletedConnections is null || result.HostKeys is null ||
                result.Commands is null || result.ConnectionHistory is null || result.CommandHistory is null || result.RemotePaths is null || result.RemoteDesktops is null || result.Transfers is null)
                throw new InvalidDataException(L10n.Get("SBD07EC1746D4"));
            if (result.JumpPresets is null || result.JumpPresets.Any(preset => preset is null || string.IsNullOrWhiteSpace(preset.Name) || preset.Settings is null))
                throw new InvalidDataException(L10n.Get("SB2F53F8F7AD2"));
            if (result.ProxyPresets is null || result.ProxyPresets.Any(preset =>
                preset is null || string.IsNullOrWhiteSpace(preset.Name) || preset.Settings is null))
                throw new InvalidDataException(L10n.Get("SB2F53F8F7AD2"));
            if (result.DeviceGroups is null || result.DeviceGroups.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException(L10n.Get("SEDBFFB7A0430"));
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public void Save(VaultDatabase database)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(this);
        try { database.Save(payload); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    public void ClearSensitiveData()
    {
        foreach (var saved in Connections.Concat(DeletedConnections.Select(item => item.Connection)))
        {
            if (saved.PrivateKey is not null)
                CryptographicOperations.ZeroMemory(saved.PrivateKey);
            if (saved.Profile.Jump?.PrivateKey is { } jumpKey)
                CryptographicOperations.ZeroMemory(jumpKey);
        }
        Connections.Clear();
        DeviceGroups.Clear();
        DeletedConnections.Clear();
        HostKeys.Clear();
        Commands.Clear();
        ConnectionHistory.Clear();
        CommandHistory.Clear();
        RemotePaths.Clear();
        Transfers.Clear();
        Cloud = null;
        Appearance = null;
        RemoteDesktops.Clear();
        ProxyPresets.Clear();
        foreach (var preset in JumpPresets)
            if (preset.Settings.PrivateKey is { } key) CryptographicOperations.ZeroMemory(key);
        JumpPresets.Clear();
    }

    public static byte[] ImportPrivateKey(string keyPath)
    {
        const int maxBytes = 1024 * 1024;
        using var stream = File.OpenRead(keyPath);
        if (stream.Length is <= 0 or > maxBytes)
            throw new IOException(L10n.Get("SC1AA9DD1DF4B"));
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

}









