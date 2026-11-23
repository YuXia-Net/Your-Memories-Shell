using MemoryShell.Services;
using System.Security.Cryptography;

namespace MemoryShell;

internal static class JumpReferences
{
    internal static ConnectionProfile Resolve(ConnectionProfile profile, IEnumerable<SavedConnection> connections)
    {
        var saved = connections.ToDictionary(c => c.Profile.Id);
        var visited = new HashSet<Guid> { profile.Id };
        return profile with { Jump = ResolveJump(profile.Jump) };

        SshJumpSettings? ResolveJump(SshJumpSettings? jump)
        {
            if (jump?.ConnectionId is not { } id)
                return jump is null ? null : jump with { PrivateKey = jump.PrivateKey?.ToArray() };
            if (!visited.Add(id)) throw new InvalidOperationException(L10n.Get("JumpReferenceCycle"));
            if (!saved.TryGetValue(id, out var entry)) throw new InvalidOperationException(L10n.Get("JumpReferenceMissing"));
            var p = entry.Profile;
            var parent = ResolveJump(p.Jump);
            return new SshJumpSettings
            {
                Host = p.Host, Port = p.Port, Username = p.Username, UsesPrivateKey = p.UsesPrivateKey,
                Password = entry.Secret, PrivateKey = entry.PrivateKey?.ToArray(), KeyPath = p.KeyPath,
                Proxy = p.Proxy, Jump = parent
            };
        }
    }

    internal static void ClearKeys(SshJumpSettings? jump)
    {
        for (; jump is not null; jump = jump.Jump)
            if (jump.PrivateKey is { } key) CryptographicOperations.ZeroMemory(key);
    }
}
