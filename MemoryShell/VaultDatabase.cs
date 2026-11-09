using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MemoryShell;

internal sealed class VaultDatabase : IDisposable
{
    private const int ApplicationId = 0x594D5348; // YMSH
    private const int FormatVersion = 1;
    private const int KdfIterations = 600_000;
    private const int KeyLength = 32;
    private const int SaltLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaximumPayloadLength = 64 * 1024 * 1024;
    private const int RandomOnlyVersion = 1;

    // The first two values match device-key files written by earlier releases.
    private enum DeviceKeyMode : byte { Enrollable, Master, RandomOnly }

    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private byte[] salt;
    private byte[] key;
    private bool disposed;
    private DeviceKeyMode mode;

    private sealed record Envelope(byte[] Salt, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

    private VaultDatabase(string path, SqliteConnection connection, byte[] salt, byte[] key, DeviceKeyMode mode)
    {
        FilePath = path;
        this.connection = connection;
        this.salt = salt;
        this.key = key;
        this.mode = mode;
    }

    public string FilePath { get; }
    public bool HasMaster { get { lock (gate) return mode == DeviceKeyMode.Master; } }
    public bool IsRandomOnly { get { lock (gate) return mode == DeviceKeyMode.RandomOnly; } }

    public static VaultDatabase Create(string path, string? passphrase, byte[] plaintext, bool randomOnly = false)
    {
        if (randomOnly && passphrase is not null) throw new ArgumentException(L10n.Get("S2DA2D989385F"));
        if (passphrase is not null) ValidateNewPassphrase(passphrase);
        ValidatePayload(plaintext);

        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
            throw new IOException(L10n.Get("S43F9B01C06C6"));

        var temporary = CreateTemporaryFile(destination);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[]? key = null;
        SqliteConnection? active = null;
        try
        {
            var mode = randomOnly ? DeviceKeyMode.RandomOnly : passphrase is null ? DeviceKeyMode.Enrollable : DeviceKeyMode.Master;
            key = passphrase is null ? CreateRandomKey() : DeriveKey(passphrase, salt);
            using (var database = Connect(temporary))
            {
                ConfigureWrites(database);
                Execute(database, "PRAGMA auto_vacuum = FULL;");
                using var transaction = database.BeginTransaction();
                using var schema = database.CreateCommand();
                schema.Transaction = transaction;
                schema.CommandText = $"""
                    PRAGMA application_id = {ApplicationId};
                    PRAGMA user_version = {(randomOnly ? RandomOnlyVersion : 0)};
                    CREATE TABLE vault (
                        id INTEGER PRIMARY KEY CHECK(id = 1),
                        format INTEGER NOT NULL CHECK(format = {FormatVersion}),
                        iterations INTEGER NOT NULL CHECK(iterations = {KdfIterations}),
                        salt BLOB NOT NULL CHECK(length(salt) = {SaltLength}),
                        nonce BLOB NOT NULL CHECK(length(nonce) = {NonceLength}),
                        ciphertext BLOB NOT NULL CHECK(length(ciphertext) BETWEEN 1 AND {MaximumPayloadLength}),
                        tag BLOB NOT NULL CHECK(length(tag) = {TagLength})
                    ) STRICT;
                    """;
                schema.ExecuteNonQuery();

                var payload = Encrypt(plaintext, salt, key);
                using var insert = database.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO vault(id, format, iterations, salt, nonce, ciphertext, tag)
                    VALUES (1, $format, $iterations, $salt, $nonce, $ciphertext, $tag);
                    """;
                insert.Parameters.AddWithValue("$format", FormatVersion);
                insert.Parameters.AddWithValue("$iterations", KdfIterations);
                BindPayload(insert, payload);
                insert.ExecuteNonQuery();
                transaction.Commit();
            }

            WriteDeviceKey(destination, salt, key, mode);
            File.Move(temporary, destination, overwrite: false);
            active = Connect(destination);
            ConfigureWrites(active);
            return new VaultDatabase(destination, active, salt, key, mode);
        }
        catch
        {
            active?.Dispose();
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            throw;
        }
        finally { DeleteTemporaryFile(temporary); }
    }

    public static VaultDatabase Open(string path, string passphrase, bool remember = false) =>
        OpenCore(path, passphrase, remember) ?? throw new CryptographicException(L10n.Get("SE424F2C2A179"));

    public static VaultDatabase? OpenForCurrentUser(string path) => OpenCore(path, null, false);

    private static VaultDatabase? OpenCore(string path, string? passphrase, bool remember)
    {
        var destination = Path.GetFullPath(path);
        var database = Connect(destination);
        byte[]? key = null;
        try
        {
            using var identity = database.CreateCommand();
            identity.CommandText = "PRAGMA main.application_id;";
            if (Convert.ToInt64(identity.ExecuteScalar()) != ApplicationId)
                throw new InvalidDataException(L10n.Get("S805C1FF04164"));

            using var version = database.CreateCommand();
            version.CommandText = "PRAGMA main.user_version;";
            var databaseMode = Convert.ToInt64(version.ExecuteScalar());
            if (databaseMode is not (0 or RandomOnlyVersion))
                throw new InvalidDataException(L10n.Get("SA69CC39AEC8C"));
            var randomOnly = databaseMode == RandomOnlyVersion;
            if (randomOnly && passphrase is not null)
                throw new InvalidOperationException(L10n.Get("SB16E0FC8C436"));

            var payload = ReadEnvelope(database);
            var mode = DeviceKeyMode.Master;
            if (passphrase is null)
            {
                var wrapped = ReadDeviceKey(destination, payload.Salt);
                if (wrapped is null)
                {
                    if (randomOnly)
                        throw new CryptographicException(L10n.Get("S98E99E83A14D"));
                    database.Dispose();
                    return null;
                }
                (key, mode) = wrapped.Value;
                if ((mode == DeviceKeyMode.RandomOnly) != randomOnly)
                    throw new CryptographicException(L10n.Get("S40CED8667EA6"));
            }
            else
            {
                key = DeriveKey(passphrase, payload.Salt);
            }
            var plaintext = Decrypt(payload, key);
            CryptographicOperations.ZeroMemory(plaintext);
            ConfigureWrites(database);
            if (remember)
                WriteDeviceKey(destination, payload.Salt, key, mode);
            return new VaultDatabase(destination, database, payload.Salt, key, mode);
        }
        catch
        {
            database.Dispose();
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    public byte[] Read()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return Decrypt(ReadCurrentEnvelope(), key);
        }
    }

    public void Save(byte[] plaintext)
    {
        ValidatePayload(plaintext);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ReplacePayload(Encrypt(plaintext, salt, key));
        }
    }

    public void VerifyPassphrase(string passphrase)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RequireMasterMode();
            VerifyCurrentPassphrase(passphrase);
        }
    }

    public void ChangePassphrase(string currentPassphrase, string newPassphrase)
    {
        ValidateNewPassphrase(newPassphrase);
        if (string.Equals(currentPassphrase, newPassphrase, StringComparison.Ordinal))
            throw new ArgumentException(L10n.Get("SB13AC3DFCC70"), nameof(newPassphrase));

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RequireMasterMode();
            VerifyCurrentPassphrase(currentPassphrase);
            RotateKey(newPassphrase, DeviceKeyMode.Master);
        }
    }

    public void EnrollMaster(string passphrase)
    {
        ValidateNewPassphrase(passphrase);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (mode == DeviceKeyMode.RandomOnly)
                throw new InvalidOperationException(L10n.Get("S127EFE9F2EFB"));
            if (mode == DeviceKeyMode.Master)
                throw new InvalidOperationException(L10n.Get("S25CDEBF9A52A"));
            RotateKey(passphrase, DeviceKeyMode.Master);
        }
    }

    public void SwitchToRandomOnly(string? currentPassphrase)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (mode == DeviceKeyMode.RandomOnly)
                throw new InvalidOperationException(L10n.Get("SC546F30C07A8"));
            if (mode == DeviceKeyMode.Master)
                VerifyCurrentPassphrase(currentPassphrase ?? "");
            RotateKey(null, DeviceKeyMode.RandomOnly);
        }
    }

    private void RotateKey(string? passphrase, DeviceKeyMode replacementMode)
    {
        var plaintext = Decrypt(ReadCurrentEnvelope(), key);
        byte[]? replacementKey = null;
        try
        {
            var replacementSalt = RandomNumberGenerator.GetBytes(SaltLength);
            replacementKey = passphrase is null ? CreateRandomKey() : DeriveKey(passphrase, replacementSalt);
            // Keep the old key wrapper until the transaction commits.
            WriteDeviceKey(FilePath, replacementSalt, replacementKey, replacementMode);
            ReplacePayload(Encrypt(plaintext, replacementSalt, replacementKey),
                markRandomOnly: replacementMode == DeviceKeyMode.RandomOnly);

            var oldKey = key;
            var oldSalt = salt;
            key = replacementKey;
            salt = replacementSalt;
            mode = replacementMode;
            replacementKey = null;
            CryptographicOperations.ZeroMemory(oldKey);
            DeleteTemporaryFile(DeviceKeyPath(FilePath, oldSalt));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (replacementKey is not null)
                CryptographicOperations.ZeroMemory(replacementKey);
        }
    }

    internal VaultDatabase CopyToFolder(string folder)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var destination = Path.Combine(Path.GetFullPath(folder), "workspace.sqlite");
            Directory.CreateDirectory(folder);
            if (File.Exists(destination)) throw new IOException(L10n.Get("S43F9B01C06C6"));
            var temporary = CreateTemporaryFile(destination);
            var installed = false;
            try
            {
                using (var snapshot = Connect(temporary)) connection.BackupDatabase(snapshot);
                File.Move(temporary, destination, false);
                installed = true;
                WriteDeviceKey(destination, salt, key, mode);
                return OpenForCurrentUser(destination) ?? throw new CryptographicException(L10n.Get("SE424F2C2A179"));
            }
            catch
            {
                if (installed)
                {
                    DeleteTemporaryFile(destination);
                    DeleteTemporaryFile(DeviceKeyPath(destination, salt));
                }
                throw;
            }
            finally { DeleteTemporaryFile(temporary); }
        }
    }

    public byte[] ExportSnapshot()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RequireMasterMode();
            var temporary = CreateTemporaryFile(FilePath);
            try
            {
                using (var snapshot = Connect(temporary))
                    connection.BackupDatabase(snapshot);
                return File.ReadAllBytes(temporary);
            }
            finally { DeleteTemporaryFile(temporary); }
        }
    }

    private void ReplacePayload(Envelope payload, bool markRandomOnly = false)
    {
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE vault SET format = $format, iterations = $iterations, salt = $salt,
                nonce = $nonce, ciphertext = $ciphertext, tag = $tag
            WHERE id = 1 AND salt = $currentSalt AND format = $format AND iterations = $iterations;
            """;
        update.Parameters.AddWithValue("$format", FormatVersion);
        update.Parameters.AddWithValue("$iterations", KdfIterations);
        update.Parameters.Add("$currentSalt", SqliteType.Blob).Value = salt;
        BindPayload(update, payload);
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidDataException(L10n.Get("S9D87C73CF1E5"));
        if (markRandomOnly)
        {
            using var version = connection.CreateCommand();
            version.Transaction = transaction;
            version.CommandText = $"PRAGMA main.user_version = {RandomOnlyVersion};";
            version.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private Envelope ReadCurrentEnvelope()
    {
        var payload = ReadEnvelope(connection);
        if (!payload.Salt.AsSpan().SequenceEqual(salt))
            throw new CryptographicException(L10n.Get("S199B73627DBC"));
        return payload;
    }

    private static Envelope ReadEnvelope(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT salt, nonce, ciphertext, tag FROM main.vault
            WHERE id = 1 AND format = {FormatVersion} AND iterations = {KdfIterations}
              AND typeof(salt) = 'blob' AND length(salt) = {SaltLength}
              AND typeof(nonce) = 'blob' AND length(nonce) = {NonceLength}
              AND typeof(tag) = 'blob' AND length(tag) = {TagLength}
              AND typeof(ciphertext) = 'blob' AND length(ciphertext) BETWEEN 1 AND {MaximumPayloadLength};
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidDataException(L10n.Get("SCB7C4C55A863"));
        return new Envelope(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1),
            reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3));
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        var result = GC.AllocateUninitializedArray<byte>(KeyLength, pinned: true);
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(passphrase.AsSpan(), salt, result, KdfIterations, HashAlgorithmName.SHA256);
            return result;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(result);
            throw;
        }
    }

    private static Envelope Encrypt(byte[] plaintext, byte[] salt, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using var cipher = new AesGcm(key, TagLength);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(salt));
        return new Envelope(salt, nonce, ciphertext, tag);
    }

    private static byte[] Decrypt(Envelope payload, byte[] key)
    {
        var plaintext = new byte[payload.Ciphertext.Length];
        try
        {
            using var cipher = new AesGcm(key, TagLength);
            cipher.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plaintext, AssociatedData(payload.Salt));
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException(L10n.Get("S288522DA0B78"), ex);
        }
    }

    private static byte[] AssociatedData(byte[] salt)
    {
        var associated = new byte[12 + SaltLength];
        BinaryPrimitives.WriteInt32BigEndian(associated, ApplicationId);
        BinaryPrimitives.WriteInt32BigEndian(associated.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteInt32BigEndian(associated.AsSpan(8), KdfIterations);
        salt.AsSpan().CopyTo(associated.AsSpan(12));
        return associated;
    }

    private static string DeviceKeyPath(string path, byte[] salt) =>
        path + "." + Convert.ToHexString(salt) + ".device-key";

    private static void WriteDeviceKey(string path, byte[] salt, byte[] key, DeviceKeyMode mode)
    {
        var data = new byte[KeyLength + 1];
        data[0] = (byte)mode;
        key.CopyTo(data, 1);
        var target = DeviceKeyPath(path, salt);
        var temporary = CreateTemporaryFile(path);
        try
        {
            var wrapped = ProtectedData.Protect(data, salt, DataProtectionScope.CurrentUser);
            using (var output = new FileStream(temporary, FileMode.Truncate, FileAccess.Write, FileShare.None))
            {
                output.Write(wrapped);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            DeleteTemporaryFile(temporary);
        }
    }

    private static (byte[] Key, DeviceKeyMode Mode)? ReadDeviceKey(string path, byte[] salt)
    {
        var target = DeviceKeyPath(path, salt);
        if (!File.Exists(target))
            return null;
        byte[]? data = null;
        try
        {
            using var input = File.OpenRead(target);
            if (input.Length is <= 0 or > 16384)
                return null;
            var wrapped = new byte[(int)input.Length];
            input.ReadExactly(wrapped);
            data = ProtectedData.Unprotect(wrapped, salt, DataProtectionScope.CurrentUser);
            if (data.Length != KeyLength + 1 || data[0] > (byte)DeviceKeyMode.RandomOnly)
                return null;
            var recovered = GC.AllocateUninitializedArray<byte>(KeyLength, pinned: true);
            data.AsSpan(1).CopyTo(recovered);
            return (recovered, (DeviceKeyMode)data[0]);
        }
        catch (CryptographicException) { return null; }
        finally
        {
            if (data is not null) CryptographicOperations.ZeroMemory(data);
        }
    }

    private static void ValidatePayload(byte[] plaintext)
    {
        if (plaintext.Length is 0 or > MaximumPayloadLength)
            throw new InvalidDataException(L10n.Get("S10BB519F91E4"));
    }

    private static void ValidateNewPassphrase(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException(L10n.Get("SA956538FB4EE"), nameof(passphrase));
    }

    private static byte[] CreateRandomKey()
    {
        var result = GC.AllocateUninitializedArray<byte>(KeyLength, pinned: true);
        RandomNumberGenerator.Fill(result);
        return result;
    }

    private void RequireMasterMode()
    {
        if (mode == DeviceKeyMode.RandomOnly)
            throw new InvalidOperationException(L10n.Get("S706C7F448B75"));
        if (mode != DeviceKeyMode.Master)
            throw new InvalidOperationException(L10n.Get("SE6E373E695A0"));
    }

    private void VerifyCurrentPassphrase(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new CryptographicException(L10n.Get("S37652FBE58C0"));
        var candidate = DeriveKey(passphrase, salt);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(candidate, key))
                throw new CryptographicException(L10n.Get("S6A697EDEA290"));
        }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }

    private static void BindPayload(SqliteCommand command, Envelope payload)
    {
        command.Parameters.Add("$salt", SqliteType.Blob).Value = payload.Salt;
        command.Parameters.Add("$nonce", SqliteType.Blob).Value = payload.Nonce;
        command.Parameters.Add("$ciphertext", SqliteType.Blob).Value = payload.Ciphertext;
        command.Parameters.Add("$tag", SqliteType.Blob).Value = payload.Tag;
    }

    private static SqliteConnection Connect(string path)
    {
        var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            database.Open();
            Execute(database, "PRAGMA trusted_schema = OFF;");
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private static void ConfigureWrites(SqliteConnection database)
    {
        using var journal = database.CreateCommand();
        journal.CommandText = "PRAGMA main.journal_mode = DELETE;";
        if (journal.ExecuteScalar() as string != "delete")
            throw new IOException(L10n.Get("SC91AFC4B2518"));

        Execute(database, "PRAGMA main.synchronous = FULL; PRAGMA main.secure_delete = ON;");
    }

    private static void Execute(SqliteConnection database, string sql)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string CreateTemporaryFile(string destination)
    {
        var name = Path.Combine(Path.GetDirectoryName(destination)!, $".vault-{Guid.NewGuid():N}.tmp");
        using var handle = new FileStream(name, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return name;
    }

    private static void DeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            CryptographicOperations.ZeroMemory(key);
            connection.Dispose();
        }
    }
}



