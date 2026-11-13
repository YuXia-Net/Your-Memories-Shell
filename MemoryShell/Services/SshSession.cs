using System.Text;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace MemoryShell.Services;

public sealed partial class SshSession : IDisposable
{
    private const int MaxTextFileBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SshClient _ssh;
    private readonly SftpClient _sftp;
    private readonly ShellStream _shell;
    private readonly PrivateKeyFile? _privateKey;
    private readonly JumpTunnel? _jump;
    private readonly SemaphoreSlim _fileGate = new(1);
    private readonly SemaphoreSlim _writeGate = new(1);
    private readonly object _forwardLock = new();
    private readonly Dictionary<(ForwardKind Kind, int Port), ForwardedPort> _forwards = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private int _reading;
    private int _disposed;

    private SshSession(SshClient ssh, SftpClient sftp, ShellStream shell, PrivateKeyFile? privateKey, JumpTunnel? jump,
        HostKeyIdentity targetIdentity)
    {
        _ssh = ssh;
        _sftp = sftp;
        _shell = shell;
        _privateKey = privateKey;
        _jump = jump;
        _targetIdentity = targetIdentity;
        _lifetimeToken = _lifetime.Token;
    }

    internal bool IsTransportConnected
    {
        get
        {
            try { return Volatile.Read(ref _disposed) == 0 && _ssh.IsConnected; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    public static Task<SshSession> ConnectAsync(
        ConnectionProfile profile,
        string? secret,
        Func<HostKeyIdentity, CancellationToken, Task<bool>> trustHost,
        CancellationToken cancellationToken = default,
        byte[]? privateKeyBytes = null) => Task.Run(async () =>
    {
        PrivateKeyFile? privateKey = null;
        SshClient? ssh = null;
        SftpClient? sftp = null;
        ShellStream? shell = null;
        JumpTunnel? jump = null;
        try
        {
            privateKey = OpenPrivateKey(privateKeyBytes, secret, SshFailureStage.TargetPrivateKey, profile.KeyPath);

            if (profile.Jump is { } jumpSettings)
            {
                try
                {
                    jump = await JumpTunnel.OpenAsync(jumpSettings, profile.Proxy, profile.Host, profile.Port, trustHost,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or SshConnectionStageException or SshHostKeyException))
                {
                    throw new SshConnectionStageException(SshFailureStage.JumpConnection, ex);
                }
            }

            ConnectionInfo TargetConnectionInfo() => CreateConnectionInfo(
                jump is null ? profile.Host : "127.0.0.1", jump?.BoundPort ?? profile.Port,
                profile.Username, secret, privateKey, jump is null ? profile.Proxy : null);

            ssh = new SshClient(TargetConnectionInfo()) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            // Always present the original SSH target identity, including through the loopback jump tunnel.
            HostKeyIdentity targetIdentity;
            try
            {
                targetIdentity = await ConnectPinnedAsync(ssh, profile.Host, profile.Port, trustHost, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when ((jump is not null || profile.Proxy is not null) &&
                ex is not (OperationCanceledException or SshHostKeyException))
            {
                throw new SshConnectionStageException(jump is null ? SshFailureStage.ProxyRoute : SshFailureStage.TargetThroughJump, ex);
            }
            sftp = new SftpClient(TargetConnectionInfo())
            {
                OperationTimeout = TimeSpan.FromSeconds(30),
                KeepAliveInterval = TimeSpan.FromSeconds(30)
            };
            PinHostKey(sftp, targetIdentity);

            cancellationToken.ThrowIfCancellationRequested();
            try { shell = ssh.CreateShellStream("xterm-256color", 100, 28, 0, 0, 16 * 1024); }
            catch (SshException ex) { throw new SshConnectionStageException(SshFailureStage.Shell, ex); }
            cancellationToken.ThrowIfCancellationRequested();
            return new SshSession(ssh, sftp, shell, privateKey, jump, targetIdentity);
        }
        catch
        {
            shell?.Dispose();
            sftp?.Dispose();
            ssh?.Dispose();
            privateKey?.Dispose();
            jump?.Dispose();
            throw;
        }
    }, cancellationToken);

    private static PrivateKeyFile? OpenPrivateKey(byte[]? bytes, string? secret, SshFailureStage stage, string? keyPath = null)
    {
        try
        {
            if (bytes is not null)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return string.IsNullOrEmpty(secret) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, secret);
            }

            if (string.IsNullOrWhiteSpace(keyPath))
                return null;
            return string.IsNullOrEmpty(secret) ? new PrivateKeyFile(keyPath) : new PrivateKeyFile(keyPath, secret);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SshConnectionStageException(stage, ex);
        }
    }

    private static ConnectionInfo CreateConnectionInfo(string host, int port, string username, string? secret,
        PrivateKeyFile? privateKey, SshProxySettings? proxy)
    {
        AuthenticationMethod authentication = privateKey is null
            ? new PasswordAuthenticationMethod(username, secret ?? string.Empty)
            : new PrivateKeyAuthenticationMethod(username, privateKey);
        var connection = proxy is null
            ? new ConnectionInfo(host, port, username, authentication)
            : new ConnectionInfo(host, port, username, GetProxyType(proxy.Kind),
                proxy.Host, proxy.Port, proxy.Username, proxy.Password, authentication);
        connection.Timeout = TimeSpan.FromSeconds(20);
        return connection;
    }

    private static HostKeyIdentity HostIdentity(string host, int port, HostKeyEventArgs key) =>
        new(host, port, key.HostKeyName, $"SHA256:{key.FingerPrintSHA256}");

    private static void PinHostKey(BaseClient client, HostKeyIdentity identity) =>
        client.HostKeyReceived += (_, key) => key.CanTrust = HostIdentity(identity.Host, identity.Port, key) == identity;

    private static async Task<HostKeyIdentity> ConnectPinnedAsync(SshClient client, string host, int port,
        Func<HostKeyIdentity, CancellationToken, Task<bool>> trustHost, CancellationToken token)
    {
        HostKeyIdentity? acceptedIdentity = null;
        HostKeyIdentity? rejectedIdentity = null;
        Exception? trustError = null;
        EventHandler<HostKeyEventArgs> verifyHost = (_, key) =>
        {
            var identity = HostIdentity(host, port, key);
            key.CanTrust = false;
            try
            {
                // Subsequent keys must match the first even during authentication/rekey.
                key.CanTrust = acceptedIdentity is not null ? acceptedIdentity == identity
                    : trustHost(identity, token).WaitAsync(token).GetAwaiter().GetResult();
                if (key.CanTrust)
                    acceptedIdentity = identity;
                else
                    rejectedIdentity = identity;
            }
            catch (Exception ex)
            {
                trustError = ex;
            }
        };

        client.HostKeyReceived += verifyHost;
        try
        {
            await client.ConnectAsync(token).ConfigureAwait(false);
            var identity = acceptedIdentity ?? throw new SshHostKeyException(SshHostKeyFailure.VerificationFailed);
            PinHostKey(client, identity);
            return identity;
        }
        catch (SshException ex)
        {
            token.ThrowIfCancellationRequested();
            if (trustError is not null)
                throw new SshHostKeyException(SshHostKeyFailure.VerificationFailed, trustError);
            if (rejectedIdentity is not null)
                throw new SshHostKeyException(acceptedIdentity is null ? SshHostKeyFailure.Refused : SshHostKeyFailure.Changed, ex);
            throw;
        }
        finally
        {
            client.HostKeyReceived -= verifyHost;
        }
    }

    private static ProxyTypes GetProxyType(SshProxyKind kind) => kind switch
    {
        SshProxyKind.Http => ProxyTypes.Http,
        SshProxyKind.Socks4 => ProxyTypes.Socks4,
        SshProxyKind.Socks5 => ProxyTypes.Socks5,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public async Task ReadTerminalAsync(Action<string> output, CancellationToken ct = default)
    {
        var chars = new char[Encoding.UTF8.GetMaxCharCount(8192)];
        var decoder = Encoding.UTF8.GetDecoder();
        await ReadTerminalBytesAsync((data, _) =>
        {
            var count = decoder.GetChars(data.Span, chars, flush: false);
            if (count > 0)
                output(new string(chars, 0, count));
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        var remaining = decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
        if (remaining > 0)
            output(new string(chars, 0, remaining));
    }

    public async Task ReadTerminalBytesAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> output,
        CancellationToken ct = default)
    {
        using var cancellation = Link(ct);
        if (Interlocked.Exchange(ref _reading, 1) != 0)
            throw new InvalidOperationException(L10n.Get("S68A48E0526CF"));

        // Wake on ShellStream data events instead of blocking a worker.
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        Exception? streamFailure = null;
        EventHandler<ShellDataEventArgs> onData = (_, _) => signal.Writer.TryWrite(true);
        EventHandler<EventArgs> onClosed = (_, _) => signal.Writer.TryComplete();
        EventHandler<ExceptionEventArgs> onError = (_, error) =>
        {
            Interlocked.CompareExchange(ref streamFailure, error.Exception, null);
            signal.Writer.TryComplete(error.Exception);
        };
        _shell.DataReceived += onData;
        _shell.Closed += onClosed;
        _shell.ErrorOccurred += onError;
        try
        {
            var bytes = new byte[8192];
            do
            {
                cancellation.Token.ThrowIfCancellationRequested();
                while (_shell.DataAvailable)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var count = _shell.Read(bytes, 0, bytes.Length);
                    if (count > 0)
                        await output(bytes.AsMemory(0, count), cancellation.Token).ConfigureAwait(false);
                }
                if (!_shell.CanWrite)
                    break;
            }
            while (await signal.Reader.WaitToReadAsync(cancellation.Token).ConfigureAwait(false) && signal.Reader.TryRead(out _));

            if (Volatile.Read(ref streamFailure) is { } error)
                ExceptionDispatchInfo.Throw(error);
        }
        finally
        {
            _shell.DataReceived -= onData;
            _shell.Closed -= onClosed;
            _shell.ErrorOccurred -= onError;
            Volatile.Write(ref _reading, 0);
        }
    }

    public Task WriteAsync(string data, CancellationToken ct = default) =>
        WriteTerminalBytesAsync(Encoding.UTF8.GetBytes(data), ct);

    public async Task WriteTerminalBytesAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        using var cancellation = Link(ct);
        await _writeGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            await _shell.WriteAsync(data, cancellation.Token).ConfigureAwait(false);
            await _shell.FlushAsync(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Resize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _shell.ChangeWindowSize((uint)Math.Clamp(columns, 1, 1024), (uint)Math.Clamp(rows, 1, 1024), 0, 0);
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken ct = default)
    {
        using var cancellation = Link(ct);
        using var request = _ssh.CreateCommand(command);
        request.CommandTimeout = TimeSpan.FromSeconds(15);
        await request.ExecuteAsync(cancellation.Token).ConfigureAwait(false);
        if (request.ExitStatus is not null and not 0)
            throw new InvalidOperationException(L10n.Format("SFEF5E64694D5", request.ExitStatus, request.Error.Trim()));
        return request.Result;
    }

    public Task<int> StartLocalForwardAsync(int localPort, string destination, int destinationPort, CancellationToken ct = default)
    {
        ValidateForwardDestination(destination, destinationPort);
        return StartForwardAsync(ForwardKind.Local, localPort,
            port => new ForwardedPortLocal("127.0.0.1", port, destination, (uint)destinationPort), ct);
    }

    public Task<int> StartDynamicForwardAsync(int localPort, CancellationToken ct = default) =>
        StartForwardAsync(ForwardKind.Dynamic, localPort, port => new ForwardedPortDynamic("127.0.0.1", port), ct);

    public Task<int> StartRemoteForwardAsync(int remotePort, string destination, int destinationPort, CancellationToken ct = default)
    {
        ValidateForwardDestination(destination, destinationPort);
        return StartForwardAsync(ForwardKind.Remote, remotePort,
            port => new ForwardedPortRemote("127.0.0.1", port, destination, (uint)destinationPort), ct);
    }

    public Task StopLocalForwardAsync(int localPort, CancellationToken ct = default) =>
        StopForwardAsync(ForwardKind.Local, localPort, ct);

    public Task StopDynamicForwardAsync(int localPort, CancellationToken ct = default) =>
        StopForwardAsync(ForwardKind.Dynamic, localPort, ct);

    public Task StopRemoteForwardAsync(int remotePort, CancellationToken ct = default) =>
        StopForwardAsync(ForwardKind.Remote, remotePort, ct);

    private static void ValidateForwardDestination(string destination, int destinationPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(destinationPort, 65535);
    }

    private Task<int> StartForwardAsync(ForwardKind kind, int requestedPort, Func<uint, ForwardedPort> createPort,
        CancellationToken ct) => Task.Run(() =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requestedPort, 65535);
        lock (_forwardLock)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_forwards.ContainsKey((kind, requestedPort)))
                throw new InvalidOperationException(L10n.Format("SC86670BF947F", requestedPort));
            // SSH.NET forwards global request replies to every active remote listener in the same session.
            if (kind == ForwardKind.Remote && _forwards.Keys.Any(key => key.Kind == ForwardKind.Remote))
                throw new InvalidOperationException(L10n.Get("S96C0EBEBC0A8"));

            var port = createPort((uint)requestedPort);
            var attached = false;
            try
            {
                _ssh.AddForwardedPort(port);
                attached = true;
                port.Start();
                ct.ThrowIfCancellationRequested();
                var assignedPort = port switch
                {
                    ForwardedPortLocal local => (int)local.BoundPort,
                    ForwardedPortDynamic dynamicPort => (int)dynamicPort.BoundPort,
                    ForwardedPortRemote remote => (int)remote.BoundPort,
                    _ => throw new InvalidOperationException(L10n.Get("S384C99DA0744"))
                };
                if (assignedPort is < 1 or > 65535)
                    throw new IOException(L10n.Get("S30093BEFC800"));
                _forwards.Add((kind, assignedPort), port);
                return assignedPort;
            }
            catch
            {
                try
                {
                    if (attached)
                        _ssh.RemoveForwardedPort(port);
                }
                finally
                {
                    port.Dispose();
                }
                throw;
            }
        }
    }, ct);

    private Task StopForwardAsync(ForwardKind kind, int portNumber, CancellationToken ct) => Task.Run(() =>
    {
        lock (_forwardLock)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_forwards.TryGetValue((kind, portNumber), out var port))
                return;
            _ssh.RemoveForwardedPort(port);
            _forwards.Remove((kind, portNumber));
            port.Dispose();
        }
    }, ct);

    public Task<string> GetWorkingDirectoryAsync(CancellationToken ct = default) =>
        WithSftpAsync((client, _) => Task.FromResult(client.WorkingDirectory), ct);

    public Task<IReadOnlyList<RemoteEntry>> ListDirectoryAsync(string path, CancellationToken ct = default) =>
        WithSftpAsync<IReadOnlyList<RemoteEntry>>(async (client, token) =>
        {
            var entries = new List<RemoteEntry>();
            await foreach (var file in client.ListDirectoryAsync(path, token).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                    continue;
                entries.Add(new RemoteEntry(file.Name, file.FullName, file.IsDirectory,
                    file.IsSymbolicLink, file.Length, file.LastWriteTime,
                    GetUnixMode(file.Attributes), GetOwnerId(file.UserId), GetOwnerId(file.GroupId)));
            }
            return entries.OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }, ct);

    public Task<RemoteTextFile> ReadTextFileAsync(RemoteEntry entry, CancellationToken ct = default) =>
        WithSftpAsync(async (client, token) =>
        {
            if (entry.IsDirectory || entry.IsSymbolicLink)
                throw new InvalidOperationException(L10n.Get("SF4506E952014"));
            var file = await FindRemoteFileAsync(client, entry.FullPath, token).ConfigureAwait(false);
            var bytes = await ReadEditableFileAsync(client, file, token).ConfigureAwait(false);
            try
            {
                var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
                var offset = hasBom ? Encoding.UTF8.Preamble.Length : 0;
                var text = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
                if (text.Contains('\0'))
                    throw new InvalidDataException(L10n.Get("S2B4CDB30D953"));
                return new RemoteTextFile(entry.FullPath, text, ContentHash(bytes), hasBom);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException(L10n.Get("SADC68242CAEB"), ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }, ct);

    public Task SaveTextFileAsync(RemoteTextFile original, string text, CancellationToken ct = default) =>
        WithSftpAsync(async (client, token) =>
        {
            ArgumentNullException.ThrowIfNull(original);
            ArgumentNullException.ThrowIfNull(text);
            var bom = original.HasByteOrderMark ? Encoding.UTF8.GetPreamble() : [];
            var length = StrictUtf8.GetByteCount(text);
            if (text.Contains('\0') || length > MaxTextFileBytes - bom.Length)
                throw new InvalidDataException(L10n.Get("SCECB9FFA632B"));
            var bytes = new byte[bom.Length + length];
            bom.CopyTo(bytes, 0);
            StrictUtf8.GetBytes(text, 0, text.Length, bytes, bom.Length);

            var fullPath = original.FullPath;
            var partial = TemporaryRemotePath(fullPath);
            var created = false;
            try
            {
                var current = await FindRemoteFileAsync(client, fullPath, token).ConfigureAwait(false);
                await EnsureUnchangedAsync(client, current, original, token).ConfigureAwait(false);
                var mode = GetUnixMode(current.Attributes);
                if (mode is null || (mode.Value & 0xC00) != 0)
                    throw new InvalidOperationException(L10n.Get("SDC156F7CC5A2"));

                await using (var stream = await client.OpenAsync(partial, FileMode.CreateNew, FileAccess.Write, token)
                                 .ConfigureAwait(false))
                {
                    created = true;
                    var attributes = await client.GetAttributesAsync(partial, token).ConfigureAwait(false);
                    attributes.SetPermissions(600);
                    await Task.Run(() => client.SetAttributes(partial, attributes), token).ConfigureAwait(false);
                    var privateAttributes = await client.GetAttributesAsync(partial, token).ConfigureAwait(false);
                    if (GetUnixMode(privateAttributes) != 0x180)
                        throw new IOException(L10n.Get("S2C0A02F798C9"));
                    await stream.WriteAsync(bytes.AsMemory(), token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                }

                var staged = await client.GetAttributesAsync(partial, token).ConfigureAwait(false);
                if (current.UserId != -1)
                    staged.UserId = current.UserId;
                if (current.GroupId != -1)
                    staged.GroupId = current.GroupId;
                staged.SetPermissions(ToOctalDigits(mode.Value));
                await Task.Run(() => client.SetAttributes(partial, staged), token).ConfigureAwait(false);
                var stagedResult = await client.GetAttributesAsync(partial, token).ConfigureAwait(false);
                if ((current.UserId != -1 && stagedResult.UserId != current.UserId) ||
                    (current.GroupId != -1 && stagedResult.GroupId != current.GroupId) ||
                    GetUnixMode(stagedResult) != mode)
                    throw new InvalidOperationException(L10n.Get("S9A20C9623A64"));

                var latest = await FindRemoteFileAsync(client, fullPath, token).ConfigureAwait(false);
                if (latest.UserId != current.UserId || latest.GroupId != current.GroupId ||
                    GetUnixMode(latest.Attributes) != mode)
                    throw new IOException(L10n.Get("S10F24557304F"));
                await EnsureUnchangedAsync(client, latest, original, token).ConfigureAwait(false);
                // A server without atomic POSIX rename cannot safely replace the original.
                await Task.Run(() => client.RenameFile(partial, fullPath, isPosix: true), token).ConfigureAwait(false);
                created = false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                if (created)
                    await RemovePartialAsync(client, partial).ConfigureAwait(false);
            }
        }, ct);

    public Task ChangePermissionsAsync(RemoteEntry entry, int mode, CancellationToken ct = default) =>
        WithSftpAsync(async (client, token) =>
        {
            ArgumentOutOfRangeException.ThrowIfNegative(mode);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(mode, 0xFFF);
            var file = await FindRemoteFileAsync(client, entry.FullPath, token).ConfigureAwait(false);
            if (file.IsSymbolicLink || (!file.IsRegularFile && !file.IsDirectory))
                throw new InvalidOperationException(L10n.Get("SB6CBACD50D01"));
            await Task.Run(() => file.SetPermissions(ToOctalDigits(mode)), token).ConfigureAwait(false);
        }, ct);

    private static async Task<ISftpFile> FindRemoteFileAsync(SftpClient client, string path, CancellationToken token) =>
        await FindRemoteEntryAsync(client, path, token).ConfigureAwait(false)
        ?? throw new FileNotFoundException(L10n.Get("S89FCBB55DE4F"), path);

    private static async Task<ISftpFile?> FindRemoteEntryAsync(SftpClient client, string path, CancellationToken token)
    {
        // The client resolves the final component before stat. Inspect its directory entry to detect symlinks.
        path = AbsoluteRemotePath(client, path);
        var separator = path.LastIndexOf('/');
        if (separator < 0 || separator == path.Length - 1)
            throw new ArgumentException(L10n.Get("S1758BCA149C6"), nameof(path));
        var parent = separator == 0 ? "/" : path[..separator];
        var name = path[(separator + 1)..];
        await foreach (var file in client.ListDirectoryAsync(parent, token).ConfigureAwait(false))
        {
            if (file.Name == name)
                return file;
        }
        return null;
    }

    private static async Task<byte[]> ReadEditableFileAsync(SftpClient client, ISftpFile file, CancellationToken token)
    {
        if (!file.IsRegularFile || file.IsSymbolicLink)
            throw new InvalidOperationException(L10n.Get("SF4506E952014"));
        if (file.Length is < 0 or > MaxTextFileBytes)
            throw new InvalidDataException(L10n.Get("SCECB9FFA632B"));
        var bytes = new byte[(int)file.Length + 1];
        try
        {
            await using var stream = await client.OpenAsync(file.FullName, FileMode.Open, FileAccess.Read, token)
                .ConfigureAwait(false);
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, token)
                .ConfigureAwait(false);
            if (count != file.Length)
                throw new IOException(L10n.Get("S3E5F310EA619"));
            return bytes.AsSpan(0, count).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task EnsureUnchangedAsync(SftpClient client, ISftpFile file, RemoteTextFile original, CancellationToken token)
    {
        var bytes = await ReadEditableFileAsync(client, file, token).ConfigureAwait(false);
        try
        {
            if (ContentHash(bytes) != original.ContentHash)
                throw new IOException(L10n.Get("SE86E53BE9071"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string ContentHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static int? GetOwnerId(int id) => id == -1 ? null : id;

    private static int? GetUnixMode(SftpFileAttributes attributes)
    {
        if (!(attributes.IsRegularFile || attributes.IsDirectory || attributes.IsSymbolicLink ||
              attributes.IsSocket || attributes.IsNamedPipe || attributes.IsBlockDevice || attributes.IsCharacterDevice))
            return null;
        return (attributes.IsUIDBitSet ? 0x800 : 0) | (attributes.IsGroupIDBitSet ? 0x400 : 0)
            | (attributes.IsStickyBitSet ? 0x200 : 0) | (attributes.OwnerCanRead ? 0x100 : 0)
            | (attributes.OwnerCanWrite ? 0x80 : 0) | (attributes.OwnerCanExecute ? 0x40 : 0)
            | (attributes.GroupCanRead ? 0x20 : 0) | (attributes.GroupCanWrite ? 0x10 : 0)
            | (attributes.GroupCanExecute ? 0x8 : 0) | (attributes.OthersCanRead ? 0x4 : 0)
            | (attributes.OthersCanWrite ? 0x2 : 0) | (attributes.OthersCanExecute ? 0x1 : 0);
    }

    private static short ToOctalDigits(int mode) => Convert.ToInt16(Convert.ToString(mode, 8));

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) =>
        WithSftpAsync((client, token) => client.CreateDirectoryAsync(path, token), ct);

    public Task RenameAsync(string source, string destination, CancellationToken ct = default) =>
        WithSftpAsync(async (client, token) =>
        {
            var file = await FindRemoteFileAsync(client, source, token).ConfigureAwait(false);
            var target = AbsoluteRemotePath(client, destination);
            await Task.Run(() => file.MoveTo(target), token).ConfigureAwait(false);
        }, ct);

    public Task DeleteAsync(RemoteEntry entry, CancellationToken ct = default) =>
        WithSftpAsync(async (client, token) =>
        {
            var file = await FindRemoteFileAsync(client, entry.FullPath, token).ConfigureAwait(false);
            if (file.IsDirectory != entry.IsDirectory || file.IsSymbolicLink != entry.IsSymbolicLink)
                throw new IOException(L10n.Get("SAF9D02F59443"));
            await file.DeleteAsync(token).ConfigureAwait(false);
        }, ct);

    private static string AbsoluteRemotePath(SftpClient client, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return path.StartsWith('/') ? path : client.WorkingDirectory.TrimEnd('/') + "/" + path;
    }

    public Task DownloadAsync(string path, string localPath, IProgress<long>? progress = null, CancellationToken ct = default, bool overwrite = false) =>
        WithSftpAsync(async (client, token) =>
        {
            var destination = Path.GetFullPath(localPath);
            var partial = Path.Combine(Path.GetDirectoryName(destination)!, $".ymsh-{Guid.NewGuid():N}.partial");
            try
            {
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var reporter = progress is null ? null : new TransferProgress<DownloadFileProgressReport>(
                        report => progress.Report(checked((long)report.TotalBytesDownloaded)));
                    await client.DownloadFileAsync(path, output, reporter, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                File.Move(partial, destination, overwrite);
            }
            catch
            {
                try { File.Delete(partial); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }, ct);

    public Task<RemoteEntry?> FindEntryAsync(string path, CancellationToken ct = default) =>
        WithSftpAsync<RemoteEntry?>(async (client, token) =>
        {
            var file = await FindRemoteEntryAsync(client, path, token);
            return file is null ? null : new RemoteEntry(file.Name, file.FullName, file.IsDirectory, file.IsSymbolicLink,
                file.Length, file.LastWriteTime, GetUnixMode(file.Attributes), GetOwnerId(file.UserId), GetOwnerId(file.GroupId));
        }, ct);

    public Task UploadAsync(string localPath, string path, IProgress<long>? progress = null, CancellationToken ct = default, bool overwrite = false) =>
        WithSftpAsync(async (client, token) =>
        {
            // Stage transfers beside the destination to avoid exposing an incomplete upload at the final path.
            var partial = TemporaryRemotePath(path);
            var original = await FindRemoteEntryAsync(client, path, token);
            if (original is not null && (!overwrite || !original.IsRegularFile || original.IsSymbolicLink))
                throw new IOException(L10n.Get("TransferDestinationChanged"));
            await using var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            try
            {
                var reporter = progress is null ? null : new TransferProgress<UploadFileProgressReport>(
                    report => progress.Report(checked((long)report.TotalBytesUploaded)));
                await client.UploadFileAsync(input, partial, canOverride: false, reporter, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (original is not null)
                {
                    var latest = await FindRemoteEntryAsync(client, path, token);
                    if (latest is not { IsRegularFile: true, IsSymbolicLink: false } || latest.Length != original.Length || latest.LastWriteTimeUtc != original.LastWriteTimeUtc)
                        throw new IOException(L10n.Get("TransferDestinationChanged"));
                    try { await Task.Run(() => client.RenameFile(partial, path, isPosix: true), token).ConfigureAwait(false); }
                    catch (NotSupportedException) { await ReplaceRemoteFileAsync(client, partial, path, token).ConfigureAwait(false); }
                }
                else await client.RenameFileAsync(partial, path, token).ConfigureAwait(false);
            }
            catch
            {
                await RemovePartialAsync(client, partial).ConfigureAwait(false);
                throw;
            }
        }, ct);

    private static string TemporaryRemotePath(string path) =>
        $"{path[..(path.LastIndexOf('/') + 1)]}.ymsh-{Guid.NewGuid():N}.partial";

    private static async Task ReplaceRemoteFileAsync(SftpClient client, string staged, string destination, CancellationToken token)
    {
        var backup = destination + $".ymsh-{Guid.NewGuid():N}.backup";
        await client.RenameFileAsync(destination, backup, token).ConfigureAwait(false);
        try { await client.RenameFileAsync(staged, destination, token).ConfigureAwait(false); }
        catch (Exception failure)
        {
            using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await client.RenameFileAsync(backup, destination, rollback.Token).ConfigureAwait(false); }
            catch (Exception restoreFailure)
            {
                throw new IOException(L10n.Format("TransferRollbackFailed", backup), new AggregateException(failure, restoreFailure));
            }
            throw;
        }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await client.DeleteFileAsync(backup, cleanup.Token).ConfigureAwait(false); }
        catch (Exception ex) { throw new IOException(L10n.Format("TransferBackupCleanup", backup), ex); }
    }

    private static async Task RemovePartialAsync(SftpClient client, string path)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var entry = await FindRemoteEntryAsync(client, path, cleanup.Token).ConfigureAwait(false);
            if (entry is { IsDirectory: false })
                await entry.DeleteAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (SshException) { }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private Task WithSftpAsync(Func<SftpClient, CancellationToken, Task> operation, CancellationToken ct) =>
        WithSftpAsync(async (client, token) =>
        {
            await operation(client, token).ConfigureAwait(false);
            return true;
        }, ct);

    private async Task<T> WithSftpAsync<T>(Func<SftpClient, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var cancellation = Link(ct);
        await _fileGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            if (!_sftp.IsConnected)
                await _sftp.ConnectAsync(cancellation.Token).ConfigureAwait(false);
            return await operation(_sftp, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private CancellationTokenSource Link(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetime.Cancel();
        _shell.Dispose();
        _sftp.Dispose();
        lock (_forwardLock)
        {
            _ssh.Dispose();
            foreach (var port in _forwards.Values)
                port.Dispose();
            _forwards.Clear();
        }
        _privateKey?.Dispose();
        _jump?.Dispose();
        _lifetime.Dispose();
    }

    private sealed class JumpTunnel(SshClient client, ForwardedPortLocal port, PrivateKeyFile? privateKey) : IDisposable
    {
        public int BoundPort => (int)port.BoundPort;

        public static async Task<JumpTunnel> OpenAsync(SshJumpSettings settings, SshProxySettings? proxy, string targetHost,
            int targetPort, Func<HostKeyIdentity, CancellationToken, Task<bool>> trustHost, CancellationToken token)
        {
            ValidateForwardDestination(targetHost, targetPort);
            PrivateKeyFile? key = null;
            SshClient? jump = null;
            ForwardedPortLocal? forward = null;
            try
            {
                key = OpenPrivateKey(settings.PrivateKey, settings.Password, SshFailureStage.JumpPrivateKey);
                jump = new SshClient(CreateConnectionInfo(settings.Host, settings.Port, settings.Username,
                    settings.Password, key, proxy)) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
                await ConnectPinnedAsync(jump, settings.Host, settings.Port, trustHost, token).ConfigureAwait(false);

                forward = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
                jump.AddForwardedPort(forward);
                forward.Start();
                token.ThrowIfCancellationRequested();
                return new JumpTunnel(jump, forward, key);
            }
            catch
            {
                jump?.Dispose();
                forward?.Dispose();
                key?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try { client.Dispose(); }
            finally
            {
                try { port.Dispose(); }
                finally { privateKey?.Dispose(); }
            }
        }
    }

    private enum ForwardKind { Local, Dynamic, Remote }

    private sealed class TransferProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

