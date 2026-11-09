using Renci.SshNet;

namespace MemoryShell.Services;

public sealed partial class SshSession
{
    internal sealed class MonitorConnection : IDisposable
    {
        private readonly SshClient client;
        private readonly PrivateKeyFile? key;
        private readonly JumpTunnel? jump;
        private MonitorConnection(SshClient client, PrivateKeyFile? key, JumpTunnel? jump)
        { this.client = client; this.key = key; this.jump = jump; }

        public static Task<MonitorConnection> OpenAsync(SavedConnection saved,
            Func<HostKeyIdentity, CancellationToken, Task<bool>> trust, CancellationToken token) => Task.Run(async () =>
        {
            PrivateKeyFile? key = null; JumpTunnel? jump = null; SshClient? client = null;
            var profile = saved.Profile;
            try
            {
                key = OpenPrivateKey(saved.PrivateKey, saved.Secret, SshFailureStage.TargetPrivateKey, profile.KeyPath);
                if (profile.Jump is { } settings)
                    jump = await JumpTunnel.OpenAsync(settings, profile.Proxy, profile.Host, profile.Port, trust, token).ConfigureAwait(false);
                client = new SshClient(CreateConnectionInfo(jump is null ? profile.Host : "127.0.0.1",
                    jump?.BoundPort ?? profile.Port, profile.Username, saved.Secret, key, jump is null ? profile.Proxy : null))
                    { KeepAliveInterval = TimeSpan.FromSeconds(30) };
                await ConnectPinnedAsync(client, profile.Host, profile.Port, trust, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new MonitorConnection(client, key, jump);
            }
            catch { client?.Dispose(); key?.Dispose(); jump?.Dispose(); throw; }
        }, token);

        public async Task<string> ExecuteAsync(string text, CancellationToken token)
        {
            using var command = client.CreateCommand(text);
            command.CommandTimeout = TimeSpan.FromSeconds(12);
            await command.ExecuteAsync(token).ConfigureAwait(false);
            return command.Result;
        }

        public void Dispose() { client.Dispose(); key?.Dispose(); jump?.Dispose(); }
    }
}
