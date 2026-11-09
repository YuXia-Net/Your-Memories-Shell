using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace MemoryShell.Services;

public sealed record DirectoryTransferProgress(string RelativePath, long BytesTransferred, int FilesTransferred);

public sealed partial class SshSession
{
    private const int MaxTransferDepth = 64;

    public Task UploadDirectoryAsync(string localDirectory, string remotePath,
        IProgress<DirectoryTransferProgress>? progress = null, CancellationToken ct = default) =>
        WithSftpAsync(async (client, token) =>
        {
            var source = new DirectoryInfo(localDirectory);
            if (!source.Exists)
                throw new DirectoryNotFoundException(L10n.Get("S5399945A5032"));
            EnsureNotReparsePoint(source);

            var destination = AbsoluteRemotePath(client, remotePath).TrimEnd('/');
            if (destination.Length == 0 || destination.Split('/').Any(part => part is "." or ".."))
                throw new ArgumentException(L10n.Get("S27B21F341BAD"), nameof(remotePath));
            await EnsureRemoteDestinationAvailableAsync(client, destination, token).ConfigureAwait(false);

            // Stage in the destination's parent so the server can publish the tree with one rename.
            var stage = TemporaryRemotePath(destination);
            var created = false;
            try
            {
                await client.CreateDirectoryAsync(stage, token).ConfigureAwait(false);
                created = true;
                var directory = await FindRemoteFileAsync(client, stage, token).ConfigureAwait(false);
                await Task.Run(() => directory.SetPermissions(700), token).ConfigureAwait(false);
                var privateAttributes = await client.GetAttributesAsync(stage, token).ConfigureAwait(false);
                if (GetUnixMode(privateAttributes) != 0x1C0)
                    throw new IOException(L10n.Get("S2544DDF4C231"));
                await UploadTreeAsync(client, source, stage, "", 0, new DirectoryProgress(progress), token)
                    .ConfigureAwait(false);
                await EnsureRemoteDestinationAvailableAsync(client, destination, token).ConfigureAwait(false);
                // SFTP rename without the POSIX overwrite extension fails if the destination exists.
                await Task.Run(() => directory.MoveTo(destination), token).ConfigureAwait(false);
                created = false;
            }
            finally
            {
                if (created)
                    await RemoveStagedTreeAsync(client, stage).ConfigureAwait(false);
            }
        }, ct);

    public Task DownloadDirectoryAsync(RemoteEntry entry, string localDirectory,
        IProgress<DirectoryTransferProgress>? progress = null, CancellationToken ct = default) =>
        WithSftpAsync(async (client, token) =>
        {
            if (!entry.IsDirectory || entry.IsSymbolicLink)
                throw new InvalidOperationException(L10n.Get("S9BA040F59815"));
            var source = await FindRemoteFileAsync(client, entry.FullPath, token).ConfigureAwait(false);
            if (!source.IsDirectory || source.IsSymbolicLink)
                throw new InvalidOperationException(L10n.Get("SE566A7E2878F"));

            var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localDirectory));
            var parent = Path.GetDirectoryName(destination);
            if (parent is null)
                throw new ArgumentException(L10n.Get("S27B21F341BAD"), nameof(localDirectory));
            ValidateLocalName(Path.GetFileName(destination));
            EnsureLocalDestinationAvailable(destination);
            var parentDirectory = new DirectoryInfo(parent);
            if (!parentDirectory.Exists)
                throw new DirectoryNotFoundException(L10n.Get("S8F4ED9FF27A6"));
            EnsureNotReparsePoint(parentDirectory);

            var stage = Path.Combine(parent, $".ymsh-{Guid.NewGuid():N}.partial");
            var created = false;
            try
            {
                Directory.CreateDirectory(stage);
                created = true;
                await DownloadTreeAsync(client, source.FullName, stage, "", 0, new DirectoryProgress(progress), token)
                    .ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Directory.Move(stage, destination);
                created = false;
            }
            finally
            {
                if (created)
                {
                    try { Directory.Delete(stage, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }, ct);

    private static async Task UploadTreeAsync(SftpClient client, DirectoryInfo source, string destination,
        string relative, int depth, DirectoryProgress progress, CancellationToken token)
    {
        EnsureTransferDepth(depth);
        foreach (var item in source.EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            EnsureNotReparsePoint(item);
            var remote = AppendRemotePath(destination, item.Name);
            var relativePath = AppendRelativePath(relative, item.Name);
            if (item is DirectoryInfo child)
            {
                EnsureTransferDepth(depth + 1);
                await client.CreateDirectoryAsync(remote, token).ConfigureAwait(false);
                await UploadTreeAsync(client, child, remote, relativePath, depth + 1, progress, token)
                    .ConfigureAwait(false);
            }
            else
            {
                await using var input = new FileStream(item.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                var reporter = new TransferProgress<UploadFileProgressReport>(report =>
                    progress.Current(relativePath, checked((long)report.TotalBytesUploaded)));
                await client.UploadFileAsync(input, remote, canOverride: false, reporter, token).ConfigureAwait(false);
                progress.Complete(relativePath, input.Length);
            }
        }
    }

    private static async Task DownloadTreeAsync(SftpClient client, string source, string destination,
        string relative, int depth, DirectoryProgress progress, CancellationToken token)
    {
        EnsureTransferDepth(depth);
        await foreach (var item in client.ListDirectoryAsync(source, token).ConfigureAwait(false))
        {
            if (item.Name is "." or "..")
                continue;
            ValidateLocalName(item.Name);
            if (item.IsSymbolicLink || (!item.IsDirectory && !item.IsRegularFile))
                throw new IOException(L10n.Format("S5E292BA5F9AB", item.Name));

            var local = Path.Combine(destination, item.Name);
            var relativePath = AppendRelativePath(relative, item.Name);
            EnsureLocalDestinationAvailable(local);
            if (item.IsDirectory)
            {
                EnsureTransferDepth(depth + 1);
                Directory.CreateDirectory(local);
                await DownloadTreeAsync(client, item.FullName, local, relativePath, depth + 1, progress, token)
                    .ConfigureAwait(false);
            }
            else
            {
                await using var output = new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var reporter = new TransferProgress<DownloadFileProgressReport>(report =>
                    progress.Current(relativePath, checked((long)report.TotalBytesDownloaded)));
                await client.DownloadFileAsync(item.FullName, output, reporter, token).ConfigureAwait(false);
                progress.Complete(relativePath, output.Position);
            }
        }
    }

    private static void EnsureTransferDepth(int depth)
    {
        if (depth > MaxTransferDepth)
            throw new IOException(L10n.Format("S9480E1636DCF", MaxTransferDepth));
    }

    private static void EnsureNotReparsePoint(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException(L10n.Format("S108A1FAB6571", item.Name));
    }

    private static void ValidateLocalName(string name)
    {
        var stem = name.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        var deviceNumber = stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
            stem.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789¹²³".Contains(stem[3]);
        if (name.Length == 0 || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || deviceNumber ||
            stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            throw new IOException(L10n.Format("SCC829C14F659", name));
    }

    private static void EnsureLocalDestinationAvailable(string path)
    {
        if (Path.Exists(path))
            throw new IOException(L10n.Format("S73456446DB28", Path.GetFileName(path)));
    }

    private static async Task EnsureRemoteDestinationAvailableAsync(SftpClient client, string path, CancellationToken token)
    {
        if (await FindRemoteEntryAsync(client, path, token).ConfigureAwait(false) is not null)
            throw new IOException(L10n.Get("SE7A4045B9319"));
    }

    private static async Task RemoveStagedTreeAsync(SftpClient client, string path)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var item = await FindRemoteEntryAsync(client, path, cleanup.Token).ConfigureAwait(false);
            if (item is not null)
                await RemoveStagedItemAsync(client, item, 0, cleanup.Token).ConfigureAwait(false);
        }
        catch (SshException) { }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task RemoveStagedItemAsync(SftpClient client, ISftpFile item, int depth, CancellationToken token)
    {
        EnsureTransferDepth(depth);
        if (item.IsDirectory && !item.IsSymbolicLink)
        {
            await foreach (var child in client.ListDirectoryAsync(item.FullName, token).ConfigureAwait(false))
            {
                if (child.Name is not ("." or ".."))
                    await RemoveStagedItemAsync(client, child, depth + 1, token).ConfigureAwait(false);
            }
        }
        await item.DeleteAsync(token).ConfigureAwait(false);
    }

    private static string AppendRemotePath(string directory, string name) => directory.TrimEnd('/') + "/" + name;
    private static string AppendRelativePath(string directory, string name) =>
        directory.Length == 0 ? name : directory + "/" + name;

    private sealed class DirectoryProgress(IProgress<DirectoryTransferProgress>? reporter)
    {
        private long bytes;
        private int files;

        public void Current(string path, long currentFileBytes) =>
            reporter?.Report(new DirectoryTransferProgress(path, bytes + currentFileBytes, files));

        public void Complete(string path, long currentFileBytes)
        {
            bytes += currentFileBytes;
            files++;
            Current(path, 0);
        }
    }
}
