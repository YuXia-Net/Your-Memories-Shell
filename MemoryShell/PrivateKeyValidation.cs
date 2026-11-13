using Renci.SshNet;

namespace MemoryShell;

internal static class PrivateKeyValidation
{
    public static void Validate(byte[] bytes, string? passphrase)
    {
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var key = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(input) : new PrivateKeyFile(input, passphrase);
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ArgumentException or System.Security.Cryptography.CryptographicException or NotSupportedException)
        {
            throw new ArgumentException(L10n.Get("KeyInvalid"), ex);
        }
    }
}
