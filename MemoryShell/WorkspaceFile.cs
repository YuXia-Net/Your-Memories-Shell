using System.Text.Json;

namespace MemoryShell;

internal static class WorkspaceFile
{
    private static readonly object SaveGate = new();

    public static void WriteJson<T>(string destination, T value, JsonSerializerOptions? options = null)
    {
        lock (SaveGate) WriteJsonCore(destination, value, options);
    }

    private static void WriteJsonCore<T>(string destination, T value, JsonSerializerOptions? options)
    {
        destination = Path.GetFullPath(destination);
        var folder = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(destination)}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }

    public static string ReadDatabasePath(string configuration, string defaultPath)
    {
        FileStream stream;
        try { stream = new FileStream(configuration, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return defaultPath; }
        catch (DirectoryNotFoundException) { return defaultPath; }
        using (stream)
        {
            try
            {
                if (stream.Length is <= 0 or > 4096) throw new InvalidDataException();
                var folder = JsonSerializer.Deserialize<string>(stream);
                if (folder is null || !Path.IsPathFullyQualified(folder)) throw new InvalidDataException();
                var resolved = Path.GetFullPath(folder);
                var database = File.Exists(resolved) || !Directory.Exists(resolved) && Path.GetExtension(resolved).ToLowerInvariant() is ".sqlite" or ".sqlite3"
                    ? resolved : Path.Combine(resolved, "workspace.sqlite");
                if ((File.GetAttributes(database) & FileAttributes.Directory) != 0) throw new InvalidDataException();
                return database;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                throw new DatabaseLocationException(L10n.Format("DatabaseLocationUnavailable", configuration), ex);
            }
        }
    }
}

internal sealed class DatabaseLocationException(string message, Exception inner) : IOException(message, inner);
