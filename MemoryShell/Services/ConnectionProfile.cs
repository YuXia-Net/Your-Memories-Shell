namespace MemoryShell.Services;

public sealed record ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public string KeyPath { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public bool RememberCredentials { get; init; } = true;
    public bool UsesPrivateKey { get; init; }
    public bool IncludeInMonitor { get; init; }
    public string MonitorTags { get; init; } = "";
    public int MonitorColumnSpan { get; init; } = 1;
    public int MonitorRowSpan { get; init; } = 1;
    public SshProxySettings? Proxy { get; init; }
    public SshJumpSettings? Jump { get; init; }
}

public sealed record SshJumpSettings
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public string Username { get; init; } = string.Empty;
    public string? Password { get; init; }
    public bool UsesPrivateKey { get; init; }
    public byte[]? PrivateKey { get; init; }
}

public enum SshProxyKind
{
    Http,
    Socks4,
    Socks5
}

public sealed record SshProxySettings
{
    public SshProxyKind Kind { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 1080;
    public string? Username { get; init; }
    public string? Password { get; init; }
}

public sealed record HostKeyIdentity(string Host, int Port, string Algorithm, string Fingerprint);

public sealed record RemoteEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    bool IsSymbolicLink,
    long Size,
    DateTime LastWriteTime,
    int? UnixMode = null,
    int? UserId = null,
    int? GroupId = null);

public sealed class RemoteTextFile
{
    internal RemoteTextFile(string fullPath, string text, string contentHash, bool hasByteOrderMark)
    {
        FullPath = fullPath;
        Text = text;
        ContentHash = contentHash;
        HasByteOrderMark = hasByteOrderMark;
    }

    public string FullPath { get; }
    public string Text { get; }
    internal string ContentHash { get; }
    internal bool HasByteOrderMark { get; }
}

