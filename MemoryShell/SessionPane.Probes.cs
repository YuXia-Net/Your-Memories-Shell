using System.Globalization;
using MemoryShell.Services;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private const string ProbeMarker = "MEMORYSHELL-TCP";
    private const string ProbeScript = """
        export LC_ALL=C
        if ! command -v timeout >/dev/null 2>&1; then
            printf 'MEMORYSHELL-TCP unavailable\n'
            exit 0
        fi
        TIMEFORMAT=%R
        elapsed=$({ time timeout -k 1s 4s bash -c 'exec 3<>"/dev/tcp/$1/$2"' -- "$1" "$2" 2>/dev/null; } 2>&1)
        status=$?
        printf 'MEMORYSHELL-TCP %s %s\n' "$status" "$elapsed"
        """;

    public NetworkProbePoint? CreateNetworkProbePoint() => disposed || session is not { } connected
        ? null
        : new NetworkProbePoint(store.HideDeviceAddress ? MainWindow.MaskDeviceAddress(profile, profile.Name, true) : $"{profile.Name}  ·  {profile.Host}",
            (host, port, token) => ProbeRemoteAsync(connected, host, port, token));

    private static async Task<NetworkProbeResult> ProbeRemoteAsync(SshSession connected, string host, int port,
        CancellationToken token)
    {
        var target = MultiLocationDiagnosticsDialog.NormalizeHost(host);
        if (target is null || port is < 1 or > 65535)
            throw new ArgumentException(L10n.Get("S921067E5ADEA"));
        var command = $"bash -c {QuoteShellArgument(ProbeScript.ReplaceLineEndings("\n"))} -- {QuoteShellArgument(target)} {port.ToString(CultureInfo.InvariantCulture)}";
        var output = await connected.ExecuteAsync(command, token);
        var status = output.Split('\n').LastOrDefault(line => line.StartsWith(ProbeMarker + " ", StringComparison.Ordinal));
        if (status is null)
            return new NetworkProbeResult(L10n.Get("SABBE344B7B6E"));

        var fields = status.TrimEnd().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
            return new NetworkProbeResult(L10n.Get("SABBE344B7B6E"));
        double? elapsed = fields.Length >= 3 &&
            double.TryParse(fields[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) &&
            seconds is >= 0 and <= 5.5 ? seconds * 1000 : null;
        return fields[1] switch
        {
            "0" => new NetworkProbeResult(L10n.Get("S6043B97C2EC8"), elapsed),
            "124" or "137" => new NetworkProbeResult(L10n.Get("SAE5EAD4D8907")),
            "unavailable" or "125" or "126" or "127" => new NetworkProbeResult(L10n.Get("SD8A075150A88")),
            _ => new NetworkProbeResult(L10n.Get("S8B0A4943D249"))
        };
    }

    private static string QuoteShellArgument(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}

