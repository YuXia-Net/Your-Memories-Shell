using Renci.SshNet;
using Renci.SshNet.Common;

namespace MemoryShell.Services;

public sealed partial class SshSession
{
    internal static Task CheckRouteAsync(ConnectionProfile profile,
        Func<HostKeyIdentity, CancellationToken, Task<bool>> verifyJump, CancellationToken token) => Task.Run(async () =>
    {
        JumpTunnel? jump = null;
        try
        {
            if (profile.Jump is { } settings)
            {
                try { jump = await JumpTunnel.OpenAsync(settings, profile.Proxy, profile.Host, profile.Port, verifyJump, token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not (OperationCanceledException or SshConnectionStageException or SshHostKeyException))
                { throw new SshConnectionStageException(SshFailureStage.JumpConnection, ex); }
            }
            using var client = new SshClient(CreateConnectionInfo(jump is null ? profile.Host : "127.0.0.1",
                jump?.BoundPort ?? profile.Port, "route-check", null, null, jump is null ? profile.Proxy : null));
            bool received = false;
            client.HostKeyReceived += (_, args) => { args.CanTrust = false; Volatile.Write(ref received, true); };
            try { await client.ConnectAsync(token).ConfigureAwait(false); }
            catch (SshException) when (Volatile.Read(ref received)) { token.ThrowIfCancellationRequested(); return; }
            catch (Exception ex) when (ex is not OperationCanceledException && (jump is not null || profile.Proxy is not null))
            { throw new SshConnectionStageException(jump is null ? SshFailureStage.ProxyRoute : SshFailureStage.TargetThroughJump, ex); }
        }
        finally { jump?.Dispose(); }
    }, token);
}
