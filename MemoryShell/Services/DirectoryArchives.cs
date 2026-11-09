using System.Diagnostics;
using System.Text;

namespace MemoryShell.Services;

public sealed partial class SshSession
{
    private const int ArchiveBufferSize = 64 * 1024;
    private const long MaximumPendingArchiveBytes = 64L * 1024 * 1024;

    public Task DownloadDirectoryArchiveAsync(RemoteEntry entry, string localPath, IProgress<long>? progress = null,
        CancellationToken ct = default) => WithSftpAsync(async (client, token) =>
    {
        if (!entry.IsDirectory || entry.IsSymbolicLink)
            throw new InvalidOperationException(L10n.Get("S8D659ED7C2C6"));

        var destination = Path.GetFullPath(localPath);
        ValidateLocalName(Path.GetFileName(destination));
        EnsureLocalDestinationAvailable(destination);
        var parent = Directory.GetParent(destination) ?? throw new IOException(L10n.Get("SFF4537DA495E"));
        if (!parent.Exists)
            throw new DirectoryNotFoundException(L10n.Get("SDBB70F8418A8"));
        EnsureNotReparsePoint(parent);

        var current = await FindRemoteFileAsync(client, entry.FullPath, token).ConfigureAwait(false);
        if (!current.IsDirectory || current.IsSymbolicLink)
            throw new IOException(L10n.Get("SE566A7E2878F"));
        var path = current.FullName.TrimEnd('/');
        var separator = path.LastIndexOf('/');
        if (!path.StartsWith('/') || separator < 0 || path.Split('/').Any(part => part is "." or ".."))
            throw new IOException(L10n.Get("S8781C6D95ACF"));
        var remoteParent = separator == 0 ? "/" : path[..separator];
        var name = path[(separator + 1)..];
        var command = $"env -u TAR_OPTIONS -u GZIP tar --one-file-system -czf - -C {QuoteArchiveArgument(remoteParent)} -- {QuoteArchiveArgument(name)}";

        var stage = Path.Combine(parent.FullName, $".memoryshell-archive-{Guid.NewGuid():N}.partial");
        var created = false;
        try
        {
            await using (var local = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             ArchiveBufferSize, FileOptions.Asynchronous))
            {
                created = true;
                await WriteArchiveAsync(command, local, progress, token).ConfigureAwait(false);
                local.Flush(flushToDisk: true);
            }

            token.ThrowIfCancellationRequested();
            File.Move(stage, destination, overwrite: false);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(stage); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }, ct);

    private async Task WriteArchiveAsync(string command, FileStream output, IProgress<long>? progress,
        CancellationToken token)
    {
        using var remoteCancellation = new CancellationTokenSource();
        using var request = _ssh.CreateCommand(command);
        request.CommandTimeout = TimeSpan.FromHours(2);
        var execution = request.ExecuteAsync(remoteCancellation.Token);
        var copying = CopyArchiveAsync(request.OutputStream, output, progress, token);
        var diagnostics = ReadArchiveDiagnosticsAsync(request.ExtendedOutputStream, token);

        try
        {
            // A local write error must interrupt tar rather than wait while SSH.NET buffers its output.
            var first = await Task.WhenAny(execution, copying, diagnostics).WaitAsync(token).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            await execution.WaitAsync(token).ConfigureAwait(false);
            var received = await copying.WaitAsync(token).ConfigureAwait(false);
            var error = await diagnostics.WaitAsync(token).ConfigureAwait(false);
            if (request.ExitStatus != 0)
                throw new IOException(error.Length == 0
                    ? L10n.Get("S9FFCFF59B387")
                    : L10n.Format("SD1CA83BAEDAD", error));
            if (received == 0)
                throw new IOException(L10n.Get("S3F367416CA07"));
        }
        catch
        {
            await remoteCancellation.CancelAsync().ConfigureAwait(false);
            request.OutputStream.Dispose();
            request.ExtendedOutputStream.Dispose();
            try { await Task.WhenAll(execution, copying, diagnostics).ConfigureAwait(false); }
            catch (Exception) { /* Preserve the first error from the transfer. */ }
            throw;
        }
    }

    private static async Task<long> CopyArchiveAsync(Stream source, FileStream output, IProgress<long>? progress,
        CancellationToken token)
    {
        var header = new byte[3];
        var count = await source.ReadAtLeastAsync(header, header.Length, false, token).ConfigureAwait(false);
        if (count == 0)
            return 0;
        if (count != header.Length || header[0] != 0x1f || header[1] != 0x8b || header[2] != 0x08)
            throw new IOException(L10n.Get("S2228C22D15BB"));
        await output.WriteAsync(header, token).ConfigureAwait(false);

        var buffer = new byte[ArchiveBufferSize];
        var reportedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            // SSH.NET uses a non-seekable pipe whose Length reports unread bytes.
            if (source.Length > MaximumPendingArchiveBytes)
                throw new IOException(L10n.Get("S5B4EB5E1C58E"));
            count = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (progress is not null && Stopwatch.GetElapsedTime(reportedAt) >= TimeSpan.FromMilliseconds(200))
            {
                progress.Report(output.Position);
                reportedAt = Stopwatch.GetTimestamp();
            }
        }
        progress?.Report(output.Position);
        return output.Position;
    }

    private static async Task<string> ReadArchiveDiagnosticsAsync(Stream source, CancellationToken token)
    {
        var buffer = new byte[4096];
        var captured = new byte[buffer.Length];
        var length = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0)
                break;
            var copy = Math.Min(count, captured.Length - length);
            buffer.AsSpan(0, copy).CopyTo(captured.AsSpan(length));
            length += copy;
        }
        return Encoding.UTF8.GetString(captured, 0, length).Trim();
    }

    private static string QuoteArchiveArgument(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
