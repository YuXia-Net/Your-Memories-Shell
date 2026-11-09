using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Renci.SshNet.Sftp;

namespace MemoryShell.Services;

public sealed partial class SshSession
{
    private const int ResumeBufferSize = 64 * 1024;
    private readonly HostKeyIdentity _targetIdentity;

    public Task DownloadResumableAsync(RemoteEntry entry, string localPath, IProgress<long>? progress = null,
        CancellationToken ct = default) => WithSftpAsync(async (client, token) =>
    {
        if (entry.IsDirectory || entry.IsSymbolicLink)
            throw new InvalidOperationException(L10n.Get("S6088C8A46B08"));

        var destination = Path.GetFullPath(localPath);
        ValidateLocalName(Path.GetFileName(destination));
        EnsureLocalDestinationAvailable(destination);
        var parent = Directory.GetParent(destination) ?? throw new IOException(L10n.Get("SFF4537DA495E"));
        if (!parent.Exists)
            throw new DirectoryNotFoundException(L10n.Get("SDBB70F8418A8"));
        EnsureNotReparsePoint(parent);

        var current = await FindRemoteFileAsync(client, entry.FullPath, token).ConfigureAwait(false);
        if (!current.IsRegularFile || current.IsSymbolicLink || current.Length < 0 ||
            current.Length != entry.Size || current.LastWriteTime != entry.LastWriteTime)
            throw new IOException(L10n.Get("S4BE13B4A17F7"));

        var stage = GetResumeStage(parent.FullName, destination, current);
        EnsureResumeStageIsRegular(stage);
        await using (var remote = await client.OpenAsync(current.FullName, FileMode.Open, FileAccess.Read, token)
                         .ConfigureAwait(false))
        {
            if (remote.Length != current.Length)
                throw new IOException(L10n.Get("S2A8361A47D3E"));

            await using var local = new FileStream(stage, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                ResumeBufferSize, FileOptions.Asynchronous);
            EnsureResumeStageHasNoLinks(local.SafeFileHandle);
            var offset = local.Length;
            if (offset > current.Length || !await MatchesResumeSamplesAsync(local, remote, offset, token).ConfigureAwait(false))
            {
                local.SetLength(0);
                offset = 0;
            }

            local.Position = remote.Position = offset;
            await CopyRemainingAsync(remote, local, current.Length, progress, token).ConfigureAwait(false);
            var latest = await FindRemoteEntryAsync(client, current.FullName, token).ConfigureAwait(false);
            if (latest is not { IsRegularFile: true, IsSymbolicLink: false } ||
                latest.Length != current.Length || latest.LastWriteTimeUtc != current.LastWriteTimeUtc)
            {
                local.SetLength(0);
                throw new IOException(L10n.Get("S14B9E8A056CC"));
            }

            token.ThrowIfCancellationRequested();
            local.Flush(flushToDisk: true);
        }

        token.ThrowIfCancellationRequested();
        File.Move(stage, destination, overwrite: false);
        progress?.Report(current.Length);
    }, ct);

    private string GetResumeStage(string parent, string destination, ISftpFile remote)
    {
        // Include the target's actual pinned identity even when SSH uses a loopback jump tunnel.
        // The name on disk contains no address, username, or remote filename in plaintext.
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Host = _targetIdentity.Host.ToUpperInvariant(),
            _targetIdentity.Port,
            _targetIdentity.Algorithm,
            _targetIdentity.Fingerprint,
            _ssh.ConnectionInfo.Username,
            RemotePath = remote.FullName,
            remote.Length,
            Modified = remote.LastWriteTimeUtc.Ticks,
            Destination = Path.GetFileName(destination).ToUpperInvariant()
        });
        var hash = Convert.ToHexString(SHA256.HashData(identity));
        return Path.Combine(parent, $".memoryshell-download-{hash}.partial");
    }

    private static void EnsureResumeStageIsRegular(string stage)
    {
        try
        {
            var attributes = File.GetAttributes(stage);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException(L10n.Get("S7767474EF167"));
        }
        catch (FileNotFoundException)
        {
            // The first attempt creates it in the destination folder.
        }
    }

    private static void EnsureResumeStageHasNoLinks(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info) ||
            info.LinkCount != 1 || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException(L10n.Get("S6522DC97E0B1"));
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ResumeFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ResumeFileInformation
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME AccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME WriteTime;
        public uint VolumeSerial;
        public uint SizeHigh;
        public uint SizeLow;
        public uint LinkCount;
        public uint IndexHigh;
        public uint IndexLow;
    }

    private static async Task<bool> MatchesResumeSamplesAsync(FileStream local, Stream remote, long length,
        CancellationToken token)
    {
        if (length == 0)
            return true;

        var sampleLength = (int)Math.Min(length, ResumeBufferSize);
        var localSample = new byte[sampleLength];
        var remoteSample = new byte[sampleLength];

        async Task<bool> MatchesAtAsync(long offset)
        {
            local.Position = remote.Position = offset;
            await local.ReadExactlyAsync(localSample, token).ConfigureAwait(false);
            try { await remote.ReadExactlyAsync(remoteSample, token).ConfigureAwait(false); }
            catch (EndOfStreamException) { return false; }
            return localSample.AsSpan().SequenceEqual(remoteSample);
        }

        // Compare both boundaries of the saved prefix before trusting its offset.
        return await MatchesAtAsync(0).ConfigureAwait(false) &&
               (length == sampleLength || await MatchesAtAsync(length - sampleLength).ConfigureAwait(false));
    }

    private static async Task CopyRemainingAsync(Stream remote, FileStream local, long length,
        IProgress<long>? progress, CancellationToken token)
    {
        var buffer = new byte[ResumeBufferSize];
        var reportedAt = Stopwatch.GetTimestamp();
        progress?.Report(local.Position);
        while (local.Position < length)
        {
            var requested = (int)Math.Min(buffer.Length, length - local.Position);
            var read = await remote.ReadAsync(buffer.AsMemory(0, requested), token).ConfigureAwait(false);
            if (read == 0)
                throw new IOException(L10n.Get("S143C54A3DEE1"));
            await local.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            if (progress is not null && Stopwatch.GetElapsedTime(reportedAt) >= TimeSpan.FromMilliseconds(200))
            {
                progress.Report(local.Position);
                reportedAt = Stopwatch.GetTimestamp();
            }
        }
    }
}
