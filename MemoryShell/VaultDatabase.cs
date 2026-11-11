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
    private const int StorageVersion = 2;

    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private byte[] salt;
    private byte[] key;
    private byte[] lastNonce;
    private bool disposed;

    private sealed record Envelope(byte[] Salt, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

    private VaultDatabase(string path, SqliteConnection connection, byte[] salt, byte[] key)
    {
        FilePath = path;
        this.connection = connection;
        this.salt = salt;
        this.key = key;
        lastNonce = ReadEnvelope(connection).Nonce;
    }

    public string FilePath { get; }
    public static VaultDatabase Create(string path, string passphrase, byte[] plaintext)
    {
        ValidateNewPassphrase(passphrase);
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
            key = DeriveKey(passphrase, salt);
            using (var database = Connect(temporary))
            {
                ConfigureWrites(database);
                Execute(database, "PRAGMA auto_vacuum = FULL;");
                using var transaction = database.BeginTransaction();
                using var schema = database.CreateCommand();
                schema.Transaction = transaction;
                schema.CommandText = $"""
                    PRAGMA application_id = {ApplicationId};
                    PRAGMA user_version = {StorageVersion};
                    CREATE TABLE local_access (id INTEGER PRIMARY KEY CHECK(id = 1), wrapped_key BLOB NOT NULL) STRICT;
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
                WriteLocalKey(database, transaction, salt, key);
                transaction.Commit();
            }

            File.Move(temporary, destination, overwrite: false);
            active = Connect(destination);
            ConfigureWrites(active);
            return new VaultDatabase(destination, active, salt, key);
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
            var storageVersion = Convert.ToInt64(version.ExecuteScalar());
            if (storageVersion is not (0 or StorageVersion))
                throw new InvalidDataException(L10n.Get("UnsupportedVaultFormat"));

            var payload = ReadEnvelope(database);
            key = passphrase is null ? ReadLocalKey(database, destination, payload.Salt, storageVersion) : DeriveKey(passphrase, payload.Salt);
            if (key is null)
            {
                database.Dispose();
                return null;
            }
            try
            {
                var plaintext = Decrypt(payload, key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
            catch (CryptographicException) when (passphrase is null)
            {
                CryptographicOperations.ZeroMemory(key);
                database.Dispose();
                return null;
            }
            ConfigureWrites(database);
            if (remember || storageVersion == 0)
            {
                using var transaction = database.BeginTransaction();
                using var schema = database.CreateCommand();
                schema.Transaction = transaction;
                schema.CommandText = $"CREATE TABLE IF NOT EXISTS local_access (id INTEGER PRIMARY KEY CHECK(id = 1), wrapped_key BLOB NOT NULL) STRICT; PRAGMA user_version = {StorageVersion};";
                schema.ExecuteNonQuery();
                if (remember || passphrase is null)
                    WriteLocalKey(database, transaction, payload.Salt, key);
                transaction.Commit();
                if (remember || passphrase is null) DeleteTemporaryFile(LegacyKeyPath(destination, payload.Salt));
            }
            return new VaultDatabase(destination, database, payload.Salt, key);
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
            VerifyCurrentPassphrase(currentPassphrase);
            RotateKey(newPassphrase);
        }
    }

    private void RotateKey(string passphrase)
    {
        var plaintext = Decrypt(ReadCurrentEnvelope(), key);
        byte[]? replacementKey = null;
        try
        {
            var replacementSalt = RandomNumberGenerator.GetBytes(SaltLength);
            replacementKey = DeriveKey(passphrase, replacementSalt);
            ReplacePayload(Encrypt(plaintext, replacementSalt, replacementKey), replacementKey);

            var oldKey = key;
            key = replacementKey;
            salt = replacementSalt;
            replacementKey = null;
            CryptographicOperations.ZeroMemory(oldKey);
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
                return OpenForCurrentUser(destination) ?? throw new CryptographicException(L10n.Get("SE424F2C2A179"));
            }
            catch
            {
                if (installed) DeleteTemporaryFile(destination);
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
            var temporary = CreateTemporaryFile(FilePath);
            try
            {
                using (var snapshot = Connect(temporary))
                {
                    connection.BackupDatabase(snapshot);
                    ConfigureWrites(snapshot);
                    Execute(snapshot, "DELETE FROM local_access; VACUUM;");
                }
                return File.ReadAllBytes(temporary);
            }
            finally { DeleteTemporaryFile(temporary); }
        }
    }

    internal static T InstallSnapshot<T>(string stagedPath, string destination, Func<T> open)
    {
        var recovery = Path.Combine(Path.GetDirectoryName(destination)!, $"workspace-before-restore-{Guid.NewGuid():N}.sqlite");
        var installed = false;
        try
        {
            if (File.Exists(destination)) File.Replace(stagedPath, destination, recovery);
            else File.Move(stagedPath, destination);
            installed = true;
            return open();
        }
        catch (Exception failure)
        {
            if (installed)
            {
                try
                {
                    if (File.Exists(recovery)) File.Replace(recovery, destination, stagedPath);
                    else File.Move(destination, stagedPath);
                }
                catch (Exception rollbackFailure)
                {
                    throw new RestoreRollbackException(recovery, failure, rollbackFailure);
                }
            }
            throw;
        }
    }

    internal sealed class RestoreRollbackException(string recovery, Exception failure, Exception rollbackFailure)
        : IOException(L10n.Format("RestoreRollbackFailed", recovery), new AggregateException(failure, rollbackFailure));

    private void ReplacePayload(Envelope payload, byte[]? replacementKey = null)
    {
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE vault SET format = $format, iterations = $iterations, salt = $salt,
                nonce = $nonce, ciphertext = $ciphertext, tag = $tag
            WHERE id = 1 AND salt = $currentSalt AND nonce = $currentNonce AND format = $format AND iterations = $iterations;
            """;
        update.Parameters.AddWithValue("$format", FormatVersion);
        update.Parameters.AddWithValue("$iterations", KdfIterations);
        update.Parameters.Add("$currentSalt", SqliteType.Blob).Value = salt;
        update.Parameters.Add("$currentNonce", SqliteType.Blob).Value = lastNonce;
        BindPayload(update, payload);
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidDataException(L10n.Get("S9D87C73CF1E5"));
        if (replacementKey is not null)
            WriteLocalKey(connection, transaction, payload.Salt, replacementKey);
        transaction.Commit();
        lastNonce = payload.Nonce;
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

    private static string LegacyKeyPath(string path, byte[] salt) =>
        path + "." + Convert.ToHexString(salt) + ".device-key";

    private static void WriteLocalKey(SqliteConnection database, SqliteTransaction transaction, byte[] salt, byte[] key)
    {
        var wrapped = ProtectedData.Protect(key, salt, DataProtectionScope.CurrentUser);
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO local_access(id, wrapped_key) VALUES(1, $key);";
        command.Parameters.Add("$key", SqliteType.Blob).Value = wrapped;
        command.ExecuteNonQuery();
    }

    private static byte[]? ReadLocalKey(SqliteConnection database, string path, byte[] salt, long storageVersion)
    {
        byte[]? data = null;
        try
        {
            byte[] wrapped;
            if (storageVersion == 0)
            {
                var target = LegacyKeyPath(path, salt);
                if (!File.Exists(target)) return null;
                using var input = File.OpenRead(target);
                if (input.Length is <= 0 or > 16384) return null;
                wrapped = new byte[(int)input.Length];
                input.ReadExactly(wrapped);
            }
            else
            {
                using var command = database.CreateCommand();
                command.CommandText = "SELECT wrapped_key FROM local_access WHERE id = 1 AND typeof(wrapped_key) = 'blob' AND length(wrapped_key) BETWEEN 1 AND 16384;";
                if (command.ExecuteScalar() is not byte[] bytes) return null;
                wrapped = bytes;
            }
            data = ProtectedData.Unprotect(wrapped, salt, DataProtectionScope.CurrentUser);
            var offset = storageVersion == 0 ? 1 : 0;
            if (data.Length != KeyLength + offset || (offset == 1 && data[0] != 1)) return null;
            var recovered = GC.AllocateUninitializedArray<byte>(KeyLength, pinned: true);
            data.AsSpan(offset).CopyTo(recovered);
            return recovered;
        }
        catch (CryptographicException) { return null; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { return null; }
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



