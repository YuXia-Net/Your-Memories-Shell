using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MemoryShell.Media;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace MemoryShell;

internal sealed class MemoryTerminalView : Grid, IDisposable
{
    private const int MaxLyricBytes = 1024 * 1024;
    private const int MaxLyricCues = 4096;
    private const int MaxLineCharacters = 600;

    private static readonly Regex TimeTag = new(
        @"\G\[(?<minute>[0-9]{1,4}):(?<second>[0-9]{1,2})(?:[.:](?<fraction>[0-9]{1,3}))?\]",
        RegexOptions.CultureInvariant);
    private static readonly Regex OffsetTag = new(
        @"^\[offset:\s*(?<value>[+-]?[0-9]{1,8})\s*\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly DispatcherQueueTimer timer;
    private readonly bool motion = new UISettings().AnimationsEnabled;
    private readonly TerminalView console = new();
    private readonly List<LyricCue> lyrics = [];
    private readonly CancellationTokenSource lifetime = new();

    private MediaPlayer? player;
    private MediaSource? media;
    private IRandomAccessStream? mediaStream;
    private bool initialized;
    private bool attached;
    private bool disposed;
    private bool ready;
    private bool ended;
    private bool stopped;
    private int printedCue = -1;
    private int printedCharacters;
    private bool endPromptPrinted;
    private bool progressVisible;
    private string? lastProgress;

    private static readonly string BootLog =
        "\x1b[2J\x1b[3J\x1b[H\x1b[38;2;98;231;247mmemory@unknown-x\x1b[0m:~$ memoryd --play bundled\r\n" +
        L10n.Get("SF1A03DD20D5A") +
        L10n.Get("S3ED01225CB8C") +
        L10n.Get("SB3080955506B");

    private sealed record LyricCue(double Seconds, string Text)
    {
        public int[] Glyphs { get; } = StringInfo.ParseCombiningCharacters(Text);
    }

    public MemoryTerminalView(TerminalAppearance? appearance)
    {
        UseLayoutRounding = true;

        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(80);
        timer.Tick += (_, _) => RefreshPosition();
        AutomationProperties.SetName(console, L10n.Get("S109536831C44"));
        Children.Add(console);
        console.InputReceived += HandleInput;
        SetAppearance(appearance ?? TerminalAppearance.Default);
        ResetTranscript();
        Loaded += async (_, _) =>
        {
            attached = true;
            console.FocusTerminal();
            RefreshPosition();
            UpdateTimer();
            if (initialized || disposed)
                return;
            initialized = true;
            try
            {
                await LoadLyricsAsync();
                if (!disposed)
                    await LoadAudioAsync();
            }
            catch (OperationCanceledException) when (disposed) { }
            catch (Exception error) { if (!disposed) WriteMediaError(error.Message); }
        };
        Unloaded += (_, _) =>
        {
            attached = false;
            timer.Stop();
        };
    }

    public void SetAppearance(TerminalAppearance appearance) =>
        console.SetAppearance(appearance with { CursorBlink = motion && appearance.CursorBlink });

    internal void ApplyWorkspaceAppearance(WorkspaceAppearance settings, BitmapImage? wallpaper, TerminalAppearance? previous)
    {
        if (!motion)
            settings = settings with { CursorBlink = false };
        console.ApplyWorkspaceAppearance(settings, wallpaper, previous);
    }

    private async Task LoadAudioAsync()
    {
        var input = await ReadBundledAudioAsync(lifetime.Token);
        if (disposed)
        {
            input.Dispose();
            return;
        }

        DisposePlayer();
        mediaStream = input;
        try
        {
            media = MediaSource.CreateFromStream(input, MemoryAssets.AudioMediaType);
            var next = new MediaPlayer { AutoPlay = true, Volume = .75 };
            player = next;
            next.CommandManager.IsEnabled = false;
            next.MediaOpened += (sender, _) => PostPlayer(sender, () =>
            {
                ready = true;
                RefreshPosition();
                UpdateTimer();
            });
            next.PlaybackSession.PlaybackStateChanged += (_, _) => PostPlayer(next, () =>
            {
                RefreshPosition();
                UpdateTimer();
            });
            next.MediaEnded += (sender, _) => PostPlayer(sender, () =>
            {
                ended = true;
                RefreshPosition();
                UpdateTimer();
            });
            next.MediaFailed += (sender, args) =>
            {
                var message = args.ErrorMessage;
                PostPlayer(sender, () =>
                {
                    WriteMediaError(message);
                    DisposePlayer();
                });
            };

            next.Source = media;
        }
        catch
        {
            DisposePlayer();
            throw;
        }
    }

    private static async Task<InMemoryRandomAccessStream> ReadBundledAudioAsync(CancellationToken token)
    {
        using var audio = MemoryAssets.OpenAudio();
        var memory = new InMemoryRandomAccessStream();
        try
        {
            using (var output = memory.GetOutputStreamAt(0).AsStreamForWrite())
            {
                await audio.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            memory.Seek(0);
            return memory;
        }
        catch
        {
            memory.Dispose();
            throw;
        }
    }

    private async Task LoadLyricsAsync()
    {
        var ct = lifetime.Token;
        var parsed = await Task.Run(async () =>
        {
            using var stream = MemoryAssets.OpenLyrics();
            if (stream.Length is <= 0 or > MaxLyricBytes)
                throw new InvalidDataException(L10n.Get("S4A3FCEC80A79"));
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return ParseLyrics(await reader.ReadToEndAsync(ct).ConfigureAwait(false));
        }, ct);

        if (disposed)
            return;
        if (parsed.Count == 0)
            throw new InvalidDataException(L10n.Get("S7BE280BCA90C"));
        lyrics.Clear();
        lyrics.AddRange(parsed);
        ResetTranscript();
    }

    private static List<LyricCue> ParseLyrics(string content)
    {
        var cues = new List<LyricCue>();
        var offset = 0;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] != '[')
                continue;
            var offsetMatch = OffsetTag.Match(line);
            if (offsetMatch.Success)
            {
                offset = int.Parse(offsetMatch.Groups["value"].Value, CultureInfo.InvariantCulture);
                continue;
            }

            var seconds = new List<double>();
            var end = 0;
            for (var stamp = TimeTag.Match(line); stamp.Success && seconds.Count < 32; stamp = stamp.NextMatch())
            {
                end = stamp.Index + stamp.Length;
                var minute = int.Parse(stamp.Groups["minute"].Value, CultureInfo.InvariantCulture);
                var second = int.Parse(stamp.Groups["second"].Value, CultureInfo.InvariantCulture);
                if (second >= 60)
                    continue;
                var fraction = stamp.Groups["fraction"].Value;
                var subsecond = fraction.Length == 0 ? 0 : int.Parse(fraction, CultureInfo.InvariantCulture) / Math.Pow(10, fraction.Length);
                seconds.Add(minute * 60 + second + subsecond);
            }
            if (seconds.Count == 0)
                continue;
            var text = new string(line[end..].Where(c => !char.IsControl(c)).Take(MaxLineCharacters).ToArray()).Trim();
            if (text.Length == 0)
                continue;
            foreach (var at in seconds)
            {
                if (cues.Count == MaxLyricCues)
                    throw new InvalidDataException(L10n.Get("SCB312FADBB53"));
                cues.Add(new LyricCue(at, text));
            }
        }
        return cues.GroupBy(cue => Math.Max(0, cue.Seconds + offset / 1000d)).OrderBy(group => group.Key)
            .Select(group => new LyricCue(group.Key, string.Join(" / ", group.Select(cue => cue.Text).Distinct().Take(3)))).ToList();
    }

    private void PostPlayer(MediaPlayer sender, Action update)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!disposed && ReferenceEquals(sender, player))
                update();
        });
    }

    private void HandleInput(string text)
    {
        if (!ready || player is not { } current)
            return;

        try
        {
            if (text == "\u0003")
            {
                if (stopped || ended)
                    return;
                stopped = true;
                current.Pause();
                if (current.PlaybackSession.CanSeek)
                    current.PlaybackSession.Position = TimeSpan.Zero;
                console.Write((progressVisible ? "\r\x1b[2K" : "") + L10n.Get("SE952DF364566") +
                    "\x1b[38;2;98;231;247mmemory@unknown-x\x1b[0m:~$ ");
                progressVisible = false;
                lastProgress = null;
            }
            else if (text.Equals("r", StringComparison.OrdinalIgnoreCase) || (text == " " && (ended || stopped)))
            {
                if (!current.PlaybackSession.CanSeek)
                    return;
                current.PlaybackSession.Position = TimeSpan.Zero;
                ended = stopped = false;
                ResetTranscript();
                current.Play();
            }
            else if (text == " ")
            {
                if (current.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                    current.Pause();
                else
                    current.Play();
            }
        }
        catch (Exception error) { WriteMediaError(error.Message); }
    }

    private void UpdateTimer()
    {
        if (!disposed && ready && attached && !ended && !stopped && player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            if (!timer.IsRunning)
                timer.Start();
        }
        else
        {
            timer.Stop();
        }
    }

    private void RefreshPosition()
    {
        if (!ready || player is not { } current || !attached || stopped)
            return;

        var playback = current.PlaybackSession;
        var total = Math.Max(0, playback.NaturalDuration.TotalSeconds);
        var position = Math.Clamp(ended ? total : playback.Position.TotalSeconds, 0, total);
        RefreshLyrics(position);
        if (!ended && (printedCue < 0 || printedCharacters == lyrics[printedCue].Glyphs.Length))
            PrintProgress(total > 0 ? (int)(position * 100 / total) : 0,
                playback.PlaybackState == MediaPlaybackState.Playing ? L10n.Get("SC3396195E91C") : L10n.Get("S8D12FC0D4EB2"));
    }

    private void PrintProgress(int percent, string state)
    {
        var line = FormatProgress(percent, state);
        if (line == lastProgress)
            return;

        console.Write("\r\x1b[2K\x1b[38;2;94;202;234m" + line + "\x1b[0m");
        progressVisible = true;
        lastProgress = line;
    }

    private string FormatProgress(int percent, string state)
    {
        percent = Math.Clamp(percent, 0, 100);
        var columns = Math.Clamp(console.Columns - 16, 0, 34);
        var done = columns * percent / 100;
        var meter = columns >= 6
            ? "[" + new string('=', done) + (done < columns ? ">" + new string('.', columns - done - 1) : "") + "] "
            : "";
        return $"{meter}{percent,3}% {state}";
    }

    private void WriteMediaError(string? message)
    {
        var detail = string.IsNullOrWhiteSpace(message)
            ? "" : " · " + new string(message.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        console.Write(L10n.Get("S0C161627E697") + detail + "\r\n");
        progressVisible = false;
        lastProgress = null;
    }

    private void ResetTranscript()
    {
        printedCue = -1;
        printedCharacters = 0;
        endPromptPrinted = false;
        progressVisible = false;
        lastProgress = null;
        console.Write(BootLog);
    }

    private void RefreshLyrics(double position)
    {
        var low = 0;
        var high = lyrics.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (lyrics[middle].Seconds <= position)
                low = middle + 1;
            else
                high = middle;
        }
        var index = low - 1;
        if (index < printedCue || index - printedCue > 160)
        {
            ResetTranscript();
            printedCue = Math.Max(-1, index - 160);
            if (printedCue >= 0)
                printedCharacters = lyrics[printedCue].Glyphs.Length;
        }

        var output = new StringBuilder();
        for (var i = Math.Max(0, printedCue); i <= index; i++)
        {
            var cue = lyrics[i];
            if (printedCue != i)
            {
                printedCue = i;
                printedCharacters = 0;
                var stamp = TimeSpan.FromSeconds(cue.Seconds).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
                output.Append("\x1b[38;2;113;143;177m[").Append(stamp)
                    .Append("] \x1b[38;2;86;220;232mlyric\x1b[0m  ");
            }

            var glyphs = cue.Glyphs;
            var count = ended || !motion || i < index
                ? glyphs.Length
                : Math.Min(glyphs.Length, Math.Max(1, (int)((position - cue.Seconds) * 43) + 1));
            if (count <= printedCharacters)
                continue;
            var start = glyphs[printedCharacters];
            var end = count == glyphs.Length ? cue.Text.Length : glyphs[count];
            output.Append(cue.Text, start, end - start);
            printedCharacters = count;
            if (count == glyphs.Length)
                output.Append("\r\n");
        }

        if (ended && !endPromptPrinted)
        {
            endPromptPrinted = true;
            output.Append("\x1b[38;2;94;202;234m").Append(FormatProgress(100, L10n.Get("SC0B3FBFF51CC"))).Append("\x1b[0m\r\n")
                .Append(L10n.Get("SB669612A6720"))
                .Append("\x1b[38;2;98;231;247mmemory@unknown-x\x1b[0m:~$ ");
        }
        if (output.Length > 0)
        {
            if (progressVisible)
            {
                output.Insert(0, "\r\x1b[2K");
                progressVisible = false;
                lastProgress = null;
            }
            console.Write(output.ToString());
        }
    }

    private void DisposePlayer()
    {
        ready = ended = stopped = false;
        timer.Stop();
        var old = player;
        player = null;
        old?.Dispose();
        media?.Dispose();
        media = null;
        mediaStream?.Dispose();
        mediaStream = null;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        lifetime.Cancel();
        DisposePlayer();
        console.Dispose();
        lifetime.Dispose();
    }
}







