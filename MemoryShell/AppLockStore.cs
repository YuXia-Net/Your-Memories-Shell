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

internal static class AppLockStore
{
    private const int SaltSize = 32;
    private const int KeySize = 32;
    private const int Iterations = 600_000;
    private const int MaximumProtectedSize = 16 * 1024;
    private static readonly byte[] Entropy = "MemoryShell.DeviceLock.v1"u8.ToArray();
    private static readonly string PathName = Path.Combine(WorkspaceStore.Folder, "app-lock.dat");

    public static AppLockState? Load()
    {
        byte[] protectedBytes;
        try
        {
            using var file = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
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
            if (state is null || !Enum.IsDefined(state.Kind) || state.Salt?.Length != SaltSize ||
                state.Hash?.Length != KeySize || !IsValidLength(state.Kind, state.CredentialLength) ||
                state.Failures is < 0 or > 9 || state.RetryAfter < 0)
                throw InvalidSettings();
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

    public static void Set(AppLockKind? kind, string credential)
    {
        if (kind is null)
        {
            File.Delete(PathName);
            return;
        }

        if (!IsValid(kind.Value, credential))
            throw new ArgumentException(Requirement(kind.Value));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(credential, salt);
        try { Save(new AppLockState(kind.Value, salt, hash, credential.Length)); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    public static (bool Accepted, long RetryAfter) Verify(string credential)
    {
        var state = Load() ?? throw InvalidSettings();
        try
        {
            if (credential.Length != state.CredentialLength || !IsValid(state.Kind, credential))
                return default;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (state.RetryAfter > now)
                return (false, state.RetryAfter);

            var candidate = Derive(credential, state.Salt);
            try
            {
                if (CryptographicOperations.FixedTimeEquals(candidate, state.Hash))
                {
                    if (state.Failures != 0 || state.RetryAfter != 0)
                        Save(state with { Failures = 0, RetryAfter = 0 });
                    return (true, 0);
                }
            }
            finally { CryptographicOperations.ZeroMemory(candidate); }

            var failures = Math.Min(state.Failures + 1, 9);
            var delay = failures < 3 ? 0 : Math.Min(60, 1 << (failures - 2));
            var retryAfter = delay == 0 ? 0 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + delay * 1000;
            Save(state with { Failures = failures, RetryAfter = retryAfter });
            return default;
        }
        finally { CryptographicOperations.ZeroMemory(state.Hash); }
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

    private static void Save(AppLockState state)
    {
        var temporary = Path.Combine(WorkspaceStore.Folder, $".app-lock-{Guid.NewGuid():N}.tmp");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state);
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(encrypted);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, PathName, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static InvalidDataException InvalidSettings() =>
        new(L10n.Get("SC0F447D56D15"));
}
