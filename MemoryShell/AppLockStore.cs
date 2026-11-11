using System.Security.Cryptography;
using System.Text.Json;

namespace MemoryShell;

internal enum AppLockKind
{
    Pin = 1,
    Pattern = 2
}

internal sealed record AppLockState(
    AppLockKind Kind,
    byte[] Salt,
    byte[] Hash,
    int CredentialLength,
    int Failures = 0,
    long RetryAfter = 0);

internal sealed class AppLockStore(WorkspaceStore workspace, VaultDatabase database)
{
    private const int SaltSize = 32;
    private const int KeySize = 32;
    private const int Iterations = 600_000;
    private const int MaximumProtectedSize = 16 * 1024;
    private static readonly byte[] Entropy = "MemoryShell.DeviceLock.v1"u8.ToArray();
    private static readonly string LegacyPath = Path.Combine(WorkspaceStore.Folder, "app-lock.dat");

    public AppLockState? Load()
    {
        var state = workspace.StartupLock;
        Validate(state);
        return state is null ? null : state with { Salt = state.Salt.ToArray(), Hash = state.Hash.ToArray() };
    }

    internal static void ImportLegacy(WorkspaceStore workspace, VaultDatabase database)
    {
        if (workspace.LockSettingsVersion != 0) return;
        workspace.StartupLock = ReadLegacy();
        workspace.LockSettingsVersion = 1;
        workspace.Save(database);
        File.Delete(LegacyPath);
    }

    private static AppLockState? ReadLegacy()
    {
        byte[] protectedBytes;
        try
        {
            using var file = new FileStream(LegacyPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length is <= 0 or > MaximumProtectedSize)
                throw new InvalidDataException();
            protectedBytes = new byte[(int)file.Length];
            file.ReadExactly(protectedBytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            throw InvalidSettings();
        }

        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var state = JsonSerializer.Deserialize<AppLockState>(plaintext);
            if (state is null) throw InvalidSettings();
            Validate(state);
            return state;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException)
        {
            throw InvalidSettings();
        }
        finally
        {
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static void Validate(AppLockState? state)
    {
        if (state is not null && (!Enum.IsDefined(state.Kind) || state.Salt?.Length != SaltSize ||
            state.Hash?.Length != KeySize || !IsValidLength(state.Kind, state.CredentialLength) ||
            state.Failures is < 0 or > 9 || state.RetryAfter < 0)) throw InvalidSettings();
    }

    public static AppLockState? Create(AppLockKind? kind, string credential)
    {
        if (kind is null) return null;
        if (!IsValid(kind.Value, credential))
            throw new ArgumentException(Requirement(kind.Value));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(credential, salt);
        return new AppLockState(kind.Value, salt, hash, credential.Length);
    }

    public void Set(AppLockKind? kind, string credential)
    {
        var state = Create(kind, credential);
        try { Save(state); }
        finally { if (state is not null) CryptographicOperations.ZeroMemory(state.Hash); }
    }

    public async Task<(bool Accepted, long RetryAfter, bool Deferred)> VerifyAsync(string credential)
    {
        var state = Load() ?? throw InvalidSettings();
        try
        {
            if (credential.Length != state.CredentialLength || !IsValid(state.Kind, credential))
                return default;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (state.RetryAfter > now)
                return (false, state.RetryAfter, true);

            var candidate = await Task.Run(() => Derive(credential, state.Salt));
            try
            {
                if (workspace.StartupLock is not { } current || !current.Salt.AsSpan().SequenceEqual(state.Salt))
                    return default;
                if (CryptographicOperations.FixedTimeEquals(candidate, state.Hash))
                {
                    if (state.Failures != 0 || state.RetryAfter != 0)
                        Save(state with { Failures = 0, RetryAfter = 0 });
                    return (true, 0, false);
                }
            }
            finally { CryptographicOperations.ZeroMemory(candidate); }

            var failures = Math.Min(state.Failures + 1, 9);
            var delay = failures < 3 ? 0 : Math.Min(60, 1 << (failures - 2));
            var retryAfter = delay == 0 ? 0 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + delay * 1000;
            Save(state with { Failures = failures, RetryAfter = retryAfter });
            return (false, retryAfter, false);
        }
        finally { CryptographicOperations.ZeroMemory(state.Hash); }
    }

    public async Task SetAsync(AppLockKind? kind, string credential)
    {
        var state = await Task.Run(() => Create(kind, credential));
        try { Save(state); }
        finally { if (state is not null) CryptographicOperations.ZeroMemory(state.Hash); }
    }

    private static bool IsValidLength(AppLockKind kind, int length) => kind switch
    {
        AppLockKind.Pin => length is >= 6 and <= 12,
        AppLockKind.Pattern => length is >= 4 and <= 9,
        _ => false
    };

    public static bool IsValid(AppLockKind kind, string value) => IsValidLength(kind, value.Length) &&
        (kind == AppLockKind.Pin ? value.All(c => c is >= '0' and <= '9') :
            value.All(c => c is >= '1' and <= '9') && value.Distinct().Count() == value.Length);

    public static string Requirement(AppLockKind kind) =>
        kind == AppLockKind.Pin ? L10n.Get("SB3CCAAE14196") : L10n.Get("SE0F33A818534");

    private static byte[] Derive(string credential, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(credential, salt, Iterations, HashAlgorithmName.SHA256, KeySize);

    private void Save(AppLockState? state)
    {
        var previous = workspace.StartupLock;
        workspace.StartupLock = state is null ? null : state with { Salt = state.Salt.ToArray(), Hash = state.Hash.ToArray() };
        try
        {
            workspace.Save(database);
            if (previous is not null) CryptographicOperations.ZeroMemory(previous.Hash);
        }
        catch
        {
            if (workspace.StartupLock is { } failed) CryptographicOperations.ZeroMemory(failed.Hash);
            workspace.StartupLock = previous;
            throw;
        }
    }

    private static InvalidDataException InvalidSettings() =>
        new(L10n.Get("SC0F447D56D15"));
}
