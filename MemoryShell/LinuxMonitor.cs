using System.Diagnostics;
using System.Globalization;
using MemoryShell.Services;

namespace MemoryShell;

internal sealed record LinuxMetrics(
    string Uptime,
    string Load,
    double? CpuPercent,
    double? MemoryPercent,
    double? SwapPercent,
    string Memory,
    string Swap,
    LinuxNetwork Network,
    IReadOnlyList<LinuxDisk> Disks,
    IReadOnlyList<LinuxProcess> Processes,
    string System = "—", int Cores = 0, string TotalMemory = "—");

internal sealed record LinuxDisk(string Mount, string Used, string Total, double? Percent);
internal sealed record LinuxProcess(string Pid, string Name, double? Cpu, double? Memory);
internal sealed record LinuxNetwork(IReadOnlyList<LinuxInterfaceRate> Interfaces);
internal sealed record LinuxInterfaceRate(string Name, double? Received, double? Sent, long ReceivedBytes = 0, long SentBytes = 0);

internal sealed class LinuxMonitor(Func<string, CancellationToken, Task<string>> execute, bool includeProcesses = true)
{
    public LinuxMonitor(SshSession session) : this(session.ExecuteAsync) { }
    private const string SampleCommand = "sh -c '"
        + "export LC_ALL=C; "
        + "printf \"\\n[system]\\n\"; cat /etc/os-release 2>/dev/null; uname -sr; "
        + "printf \"\\n[cores]\\n\"; grep -c ^processor /proc/cpuinfo 2>/dev/null; "
        + "printf \"\\n[uptime]\\n\"; cat /proc/uptime 2>/dev/null; "
        + "printf \"\\n[load]\\n\"; cat /proc/loadavg 2>/dev/null; "
        + "printf \"\\n[cpu]\\n\"; head -n 1 /proc/stat 2>/dev/null; "
        + "printf \"\\n[memory]\\n\"; cat /proc/meminfo 2>/dev/null; "
        + "printf \"\\n[network]\\n\"; cat /proc/net/dev 2>/dev/null; "
        + "printf \"\\n[disk]\\n\"; df -kP 2>/dev/null; ";
    private const string ProcessCommand = "snapshot=$(ps -eo pid=,pcpu=,pmem=,comm= 2>/dev/null); "
        + "printf \"\\n[processes]\\n\"; printf \"%s\\n\" \"$snapshot\" | sort -k2,2nr | head -n 10; "
        + "printf \"\\n[memory_processes]\\n\"; printf \"%s\\n\" \"$snapshot\" | sort -k3,3nr | head -n 10; ";

    private (double Total, double Idle)? _previousCpu;
    private Dictionary<string, (long Received, long Sent)> _previousInterfaces = new(StringComparer.Ordinal);
    private long _previousNetworkSample;

    public async Task<LinuxMetrics> ReadAsync(CancellationToken cancellationToken = default)
    {
        string output = await execute(SampleCommand + (includeProcesses ? ProcessCommand : "") + "true'", cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(output);
    }

    internal LinuxMetrics Parse(string output)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line is "[system]" or "[cores]" or "[uptime]" or "[load]" or "[cpu]" or "[memory]" or "[network]" or "[disk]" or "[processes]" or "[memory_processes]")
            {
                current = [];
                sections[line] = current;
            }
            else if (line.Length > 0)
            {
                current?.Add(line);
            }
        }

        List<string> ReadSection(string name) => sections.GetValueOrDefault(name) ?? [];

        var memoryValues = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string line in ReadSection("[memory]"))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && ReadInteger(Fields(line[(colon + 1)..]).FirstOrDefault(), out long value))
                memoryValues[line[..colon]] = value;
        }

        long memoryTotal = memoryValues.GetValueOrDefault("MemTotal");
        long memoryAvailable = memoryValues.TryGetValue("MemAvailable", out long available)
            ? available
            : memoryValues.GetValueOrDefault("MemFree") + memoryValues.GetValueOrDefault("Buffers") + memoryValues.GetValueOrDefault("Cached");
        long swapTotal = memoryValues.GetValueOrDefault("SwapTotal");
        long memoryUsed = memoryTotal - Math.Clamp(memoryAvailable, 0, memoryTotal);
        long swapUsed = swapTotal - Math.Clamp(memoryValues.GetValueOrDefault("SwapFree"), 0, swapTotal);

        var disks = new List<LinuxDisk>();
        foreach (string line in ReadSection("[disk]").Skip(1))
        {
            string[] fields = Fields(line, 6);
            if (fields.Length == 6 && ReadInteger(fields[1], out long total) && ReadInteger(fields[2], out long used))
                disks.Add(new LinuxDisk(fields[5], FormatBytes(used * 1024d), FormatBytes(total * 1024d),
                    ReadPercentage(fields[4].TrimEnd('%'))));
        }

        var processes = new List<LinuxProcess>();
        var pids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in ReadSection("[processes]").Concat(ReadSection("[memory_processes]")))
        {
            string[] fields = Fields(line, 4);
            if (fields.Length == 4 && ReadInteger(fields[0], out _) && pids.Add(fields[0]))
                processes.Add(new LinuxProcess(fields[0], fields[3], ReadPercentage(fields[1]), ReadPercentage(fields[2])));
        }

        return new LinuxMetrics(
            FormatUptime(Fields(ReadSection("[uptime]").FirstOrDefault()).FirstOrDefault()),
            string.Join(" / ", Fields(ReadSection("[load]").FirstOrDefault()).Take(3)),
            ReadCpu(ReadSection("[cpu]").FirstOrDefault()),
            Percent(memoryUsed, memoryTotal),
            memoryValues.ContainsKey("SwapTotal") && swapTotal == 0 ? 0 : Percent(swapUsed, swapTotal),
            FormatCapacity(memoryUsed, memoryTotal),
            FormatCapacity(swapUsed, swapTotal),
            ReadNetwork(ReadSection("[network]")),
            disks,
            processes,
            ReadSection("[system]").FirstOrDefault(line => line.StartsWith("PRETTY_NAME="))?[12..].Trim('"')
                ?? ReadSection("[system]").LastOrDefault() ?? "—",
            int.TryParse(ReadSection("[cores]").FirstOrDefault(), out var cores) ? cores : 0,
            memoryTotal > 0 ? FormatBytes(memoryTotal * 1024d) : "—");
    }

    private double? ReadCpu(string? line)
    {
        string[] fields = Fields(line);
        if (fields.Length < 5 || fields[0] != "cpu")
            return null;

        double total = 0;
        double idle = 0;
        for (int i = 1; i < fields.Length && i <= 8; i++)
        {
            if (!ReadInteger(fields[i], out long counter))
                return null;

            total += counter;
            if (i is 4 or 5)
                idle += counter;
        }

        var previous = _previousCpu;
        _previousCpu = (total, idle);
        if (previous is null || total <= previous.Value.Total || idle < previous.Value.Idle)
            return null;

        return Math.Clamp((1 - (idle - previous.Value.Idle) / (total - previous.Value.Total)) * 100, 0, 100);
    }

    private LinuxNetwork ReadNetwork(IEnumerable<string> lines)
    {
        var interfaces = new Dictionary<string, (long Received, long Sent)>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            int separator = line.LastIndexOf(':');
            if (separator < 1)
                continue;

            string name = line[..separator].Trim();
            string[] fields = Fields(line[(separator + 1)..]);
            if (name != "lo" && fields.Length >= 9 && ReadInteger(fields[0], out long received) && ReadInteger(fields[8], out long sent))
                interfaces[name] = (received, sent);
        }

        long now = Stopwatch.GetTimestamp();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(_previousNetworkSample, now);
        var rates = new List<LinuxInterfaceRate>();
        foreach (var (name, current) in interfaces.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!_previousInterfaces.TryGetValue(name, out var previous) || elapsed.TotalSeconds <= 0 ||
                current.Received < previous.Received || current.Sent < previous.Sent)
            {
                rates.Add(new LinuxInterfaceRate(name, null, null, current.Received, current.Sent));
                continue;
            }

            rates.Add(new LinuxInterfaceRate(name, (current.Received - previous.Received) / elapsed.TotalSeconds,
                (current.Sent - previous.Sent) / elapsed.TotalSeconds, current.Received, current.Sent));
        }

        _previousInterfaces = interfaces;
        _previousNetworkSample = now;
        return new LinuxNetwork(rates);
    }

    private static double? ReadPercentage(string text) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value) && value >= 0 ? value : null;

    private static string[] Fields(string? value, int count = int.MaxValue) =>
        value?.Split((char[]?)null, count, StringSplitOptions.RemoveEmptyEntries) ?? [];

    private static bool ReadInteger(string? text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static double? Percent(long used, long total) => total > 0 ? 100d * used / total : null;

    private static string FormatUptime(string? seconds)
    {
        if (!double.TryParse(seconds, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value) || value >= TimeSpan.MaxValue.TotalSeconds)
            return "—";

        TimeSpan uptime = TimeSpan.FromSeconds(value);
        if (uptime.Days > 0)
            return L10n.Format("S295F367BB72A", uptime.Days, uptime.Hours);
        if (uptime.Hours > 0)
            return L10n.Format("S3CDE3F0AE16C", uptime.Hours, uptime.Minutes);
        return uptime.Minutes > 0 ? L10n.Format("SC65627EAD130", uptime.Minutes) : L10n.Format("S523FB6184D48", uptime.Seconds);
    }

    private static string FormatCapacity(long used, long total) =>
        total > 0 ? $"{FormatBytes(used * 1024d)} / {FormatBytes(total * 1024d)}" : "—";

    internal static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return $"{bytes:0.#} {units[unit]}";
    }
}

