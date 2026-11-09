using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed partial class SessionPane
{
    private DispatcherQueueTimer? directoryTimer;
    private string? pendingShellDirectory;
    private bool followDirectory = true;

    private const string BashDirectoryHook = """
        if [ -n "${BASH_VERSION-}" ]; then if ! declare -F __memoryshell_cwd >/dev/null; then __memoryshell_cwd() { local rc=$? p=$PWD; p=${p//\%/%25}; p=${p//\#/%23}; p=${p//\?/%3F}; if [[ $p != *[[:cntrl:]]* ]]; then printf '\033]7;file://localhost%s\007' "$p"; fi; return "$rc"; }; if [[ $(declare -p PROMPT_COMMAND 2>/dev/null) == 'declare -a '* ]]; then PROMPT_COMMAND+=(__memoryshell_cwd); else PROMPT_COMMAND="${PROMPT_COMMAND:+$PROMPT_COMMAND; }__memoryshell_cwd"; fi; fi; else printf '%s\n' 'MemoryShell: Bash is required for this setup.'; fi
        """;

    private DropDownButton BuildDirectoryFollowButton()
    {
        var follow = new ToggleMenuFlyoutItem { Text = L10n.Get("S4D8FB3674168"), IsChecked = true };
        follow.Click += (_, _) =>
        {
            followDirectory = follow.IsChecked;
            if (followDirectory)
                ScheduleDirectoryFollow();
            else
                directoryTimer?.Stop();
        };
        var enable = new MenuFlyoutItem { Text = L10n.Get("S4BB9BAF363FE") };
        enable.Click += async (_, _) =>
        {
            if (session is null)
                return;
            try
            {
                if (await ConfirmAsync(L10n.Get("S2EC75594E7C2"), L10n.Get("S1E9C349129E1"), L10n.Get("SF4F0EAD1116B")))
                {
                    follow.IsChecked = followDirectory = true;
                    await SendTerminalAsync(BashDirectoryHook + "\r");
                }
            }
            catch (Exception ex) { if (!disposed) fileStatus.Text = ex.Message; }
        };
        var menu = new MenuFlyout();
        menu.Items.Add(follow);
        menu.Items.Add(enable);
        return new DropDownButton { Content = L10n.Get("S5061A51DEA0D"), Flyout = menu };
    }

    private void AttachDirectoryFollow()
    {
        terminal.WorkingDirectoryChanged += value =>
        {
            pendingShellDirectory = value;
            ScheduleDirectoryFollow();
        };
    }

    private void ScheduleDirectoryFollow()
    {
        if (!followDirectory || !IsFilePaneVisible || pendingShellDirectory is null || session is null || busy || disposed)
            return;
        if (directoryTimer is null)
        {
            directoryTimer = DispatcherQueue.CreateTimer();
            directoryTimer.Interval = TimeSpan.FromMilliseconds(500);
            directoryTimer.IsRepeating = false;
            directoryTimer.Tick += async (_, _) =>
            {
                if (!followDirectory || !IsFilePaneVisible || busy || pendingShellDirectory is not { } pending)
                    return;
                pendingShellDirectory = null;
                if (directory != pending)
                    await FileActionAsync(() => RefreshFilesAsync(pending, useCache: true));
            };
        }
        directoryTimer.Stop();
        directoryTimer.Start();
    }

    public void SetTerminalAppearance(TerminalAppearance appearance) => terminal.SetAppearance(appearance);
}
