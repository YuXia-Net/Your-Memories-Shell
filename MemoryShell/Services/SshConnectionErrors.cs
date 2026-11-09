using System.Net.Sockets;
using System.Security.Cryptography;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;

namespace MemoryShell.Services;

internal enum SshRetryAction
{
    None,
    Reconnect,
    AskCredentials,
    EditConnection
}

internal sealed record SshConnectionFailure(string Title, string Message, SshRetryAction Retry);

internal enum SshFailureStage
{
    TargetPrivateKey,
    JumpPrivateKey,
    JumpConnection,
    ProxyRoute,
    TargetThroughJump,
    Shell
}

internal sealed class SshConnectionStageException(SshFailureStage stage, Exception inner)
    : Exception("SSH connection stage failed.", inner)
{
    public SshFailureStage Stage { get; } = stage;
}

internal enum SshHostKeyFailure
{
    Refused,
    Changed,
    VerificationFailed
}

internal sealed class SshHostKeyException(SshHostKeyFailure reason, Exception? inner = null)
    : Exception("SSH host key verification failed.", inner)
{
    public SshHostKeyFailure Reason { get; } = reason;
}

internal static class SshConnectionErrors
{
    public static SshConnectionFailure Classify(Exception exception, ConnectionProfile profile, bool usesPrivateKey = false)
    {
        var errors = new List<Exception>();
        for (Exception? current = exception; current is not null && errors.Count < 16; current = current.InnerException)
            errors.Add(current);

        // SSH.NET wraps its own connection deadline in a timeout exception with a canceled inner operation.
        if (exception is OperationCanceledException)
            return new(L10n.Get("S703ED122EEA2"), L10n.Get("S9EBC3A183329"), SshRetryAction.None);

        var hostKey = errors.OfType<SshHostKeyException>().ToArray();
        if (hostKey.Any(error => error.Reason == SshHostKeyFailure.Changed) ||
            errors.Any(error => error is InvalidOperationException && error.Message.StartsWith(L10n.Get("SC691BEECC3DA"), StringComparison.Ordinal)))
            return new(L10n.Get("S47A4526D49CA"), L10n.Get("S247D24B78488"), SshRetryAction.None);
        if (hostKey.Any(error => error.Reason == SshHostKeyFailure.Refused))
            return new(L10n.Get("SD74982744332"), L10n.Get("S6E29F8E5585C"), SshRetryAction.None);
        if (hostKey.Length > 0 || errors.OfType<SshConnectionException>().Any(error => error.DisconnectReason == DisconnectReason.HostKeyNotVerifiable))
            return new(L10n.Get("S26BEEF514D18"), L10n.Get("S621658F08266"), SshRetryAction.None);

        var stage = errors.OfType<SshConnectionStageException>().FirstOrDefault()?.Stage;
        var jump = stage is SshFailureStage.JumpPrivateKey or SshFailureStage.JumpConnection;
        var route = stage switch
        {
            SshFailureStage.JumpConnection => profile.Proxy is null ? L10n.Get("S542579D62B1C") : L10n.Get("S0D87EC735EE8"),
            SshFailureStage.ProxyRoute => L10n.Get("S439FFC4A645D"),
            SshFailureStage.TargetThroughJump => L10n.Get("S3A115632B637"),
            _ => L10n.Get("S9D39D0195C94")
        };
        var editOrAsk = jump ? SshRetryAction.EditConnection : SshRetryAction.AskCredentials;

        if (stage is SshFailureStage.TargetPrivateKey or SshFailureStage.JumpPrivateKey)
        {
            var subject = jump ? L10n.Get("SAF00F94E050C") : L10n.Get("S2637E8166D92");
            if (errors.Any(error => error is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException))
                return new(L10n.Format("SD314A8394D7C", subject), L10n.Get("SEEBEAF193B9D"), SshRetryAction.EditConnection);
            if (errors.Any(error => error is SshPassPhraseNullOrEmptyException))
                return new(L10n.Format("S3CDEA7F79FFB", subject), L10n.Get("S157E4CC729B9"), editOrAsk);
            if (errors.Any(error => error is CryptographicException) ||
                Includes(errors, "bad password", "check password", "incorrect pass", "invalid pass", "wrong pass", "random check bytes",
                    "MAC verification", "checksum", "pad block corrupted", "mac check"))
                return new(L10n.Format("S43D98EA5D757", subject), L10n.Get("S75FB688027DB"), editOrAsk);
            return new(L10n.Format("S918EB4500ED1", subject), L10n.Get("S1BDCA60A32D7"), SshRetryAction.EditConnection);
        }

        if (errors.OfType<ProxyException>().Any())
        {
            if (Includes(errors, "ProxyAuthenticationRequired", "407", "authentication", "username/password"))
                return new(L10n.Get("SB66A72BC9A1D"), L10n.Get("S45AA8899C267"), SshRetryAction.EditConnection);
            if (Includes(errors, "Forbidden", "403", "not allowed", "not permitted"))
                return new(L10n.Get("S03A37D5A0AA9"), L10n.Get("S10D091DFE53D"), SshRetryAction.EditConnection);
            if (Includes(errors, "unreachable", "refused by destination", "TTL expired"))
                return new(L10n.Get("SFE3B41C1C500"), L10n.Get("S9473C4F380C1"), SshRetryAction.EditConnection);
            return new(L10n.Get("SF13D2CCBEEE7"), L10n.Get("S38B1DECD2EA0"), SshRetryAction.EditConnection);
        }

        if (errors.OfType<SshAuthenticationException>().Any())
        {
            var subject = jump ? L10n.Get("SE7988FBD7AAB") : "SSH";
            if (Includes(errors, "No suitable authentication method", "No authentication methods", "PartialSuccess"))
                return new(L10n.Format("S77D63725503B", subject), L10n.Get("S8C3ECA002E2A"), SshRetryAction.EditConnection);
            var keyAuthentication = jump ? profile.Jump?.PrivateKey is not null : usesPrivateKey || !string.IsNullOrWhiteSpace(profile.KeyPath);
            return keyAuthentication
                ? new(L10n.Format("S784B999B1121", subject), L10n.Get("SE485A33D346F"), SshRetryAction.EditConnection)
                : new(L10n.Format("S839E01DF6C93", subject), L10n.Get("S950A5FF17249"), editOrAsk);
        }

        if (errors.OfType<SocketException>().FirstOrDefault() is { } socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData => new(L10n.Get("S9AB4962C4A65"), L10n.Format("S5BFBE4938CD0", route), SshRetryAction.EditConnection),
                SocketError.TryAgain => new(L10n.Get("S355BF29DBF40"), L10n.Format("SED4750AF57F5", route), SshRetryAction.Reconnect),
                SocketError.ConnectionRefused => new(L10n.Get("S3E13B80BEB4B"), L10n.Format("SBEFA1780D377", route), SshRetryAction.EditConnection),
                SocketError.TimedOut => Timeout(route),
                SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.HostDown =>
                    new(L10n.Get("S314FABD4FDC3"), L10n.Format("SFCCDA1228BDD", route), SshRetryAction.Reconnect),
                SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.NetworkReset or SocketError.Shutdown =>
                    new(L10n.Get("SDDA206707B80"), L10n.Format("SE4CDFB4FD2BC", route), SshRetryAction.Reconnect),
                SocketError.AccessDenied => new(L10n.Get("S120B304B3F9C"), L10n.Get("SD0EB865AE923"), SshRetryAction.EditConnection),
                _ => new(L10n.Get("S156C4A73D3C5"), L10n.Format("SCDDEA6964BD6", route), SshRetryAction.Reconnect)
            };
        }

        if (errors.Any(error => error is SshOperationTimeoutException or TimeoutException))
            return Timeout(route);

        if (errors.OfType<SshConnectionException>().FirstOrDefault() is { } ssh)
        {
            switch (ssh.DisconnectReason)
            {
                case DisconnectReason.KeyExchangeFailed:
                case DisconnectReason.ProtocolVersionNotSupported:
                case DisconnectReason.ProtocolError:
                case DisconnectReason.MacError:
                case DisconnectReason.CompressionError:
                    return new(L10n.Get("S97A699232757"), L10n.Get("S0E1B492D5EF3"), SshRetryAction.None);
                case DisconnectReason.IllegalUserName:
                    return new(L10n.Get("S6B886A8BED53"), L10n.Get("SE691EACECF97"), SshRetryAction.EditConnection);
                case DisconnectReason.AuthenticationCanceledByUser:
                    return new(L10n.Get("SBB05CD6DFCCC"), L10n.Get("S37FAE7BEB3E5"), SshRetryAction.None);
                case DisconnectReason.NoMoreAuthenticationMethodsAvailable:
                    return new(L10n.Get("S3A558038FF65"), L10n.Get("SB0A92F643509"), SshRetryAction.EditConnection);
                case DisconnectReason.HostNotAllowedToConnect:
                    return new(L10n.Get("S4D2DD4CEFD9D"), L10n.Get("S2F4D42DCDA21"), SshRetryAction.None);
                case DisconnectReason.TooManyConnections:
                    return new(L10n.Get("S1B8FE04F64D7"), L10n.Get("S82259E2D3C44"), SshRetryAction.Reconnect);
                case DisconnectReason.ServiceNotAvailable:
                    return new(L10n.Get("S71F7C2BC816F"), L10n.Get("S751A1048D56A"), SshRetryAction.Reconnect);
            }
        }

        if (Includes(errors, "identification string", "protocol identification", "SSH identification", "protocol version"))
            return new(L10n.Get("S480D01BCD39C"), L10n.Get("S76ABCA4557A0"), SshRetryAction.EditConnection);
        if (stage == SshFailureStage.Shell || Includes(errors, "pseudo-terminal", "start a shell", "channel could not be opened"))
            return new(L10n.Get("SBC6C1A980E7C"), L10n.Get("S75E06AF36E99"), SshRetryAction.None);
        if (errors.OfType<SshConnectionException>().Any())
            return new(L10n.Get("SFBB2F67E670C"), L10n.Format("S1834075E3350", route), SshRetryAction.Reconnect);

        return stage is SshFailureStage.JumpConnection or SshFailureStage.ProxyRoute or SshFailureStage.TargetThroughJump
            ? new(L10n.Get("S2BDD52B67175"), L10n.Format("S2BFA2E103E5A", route), SshRetryAction.EditConnection)
            : new(L10n.Get("SE7FFC54B2729"), L10n.Get("S60048AD08420"), SshRetryAction.EditConnection);
    }

    private static SshConnectionFailure Timeout(string route) =>
        new(L10n.Get("SAE5EAD4D8907"), L10n.Format("S964484C293FD", route), SshRetryAction.Reconnect);

    private static bool Includes(IEnumerable<Exception> errors, params string[] fragments) =>
        errors.Any(error => fragments.Any(fragment => error.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
}
