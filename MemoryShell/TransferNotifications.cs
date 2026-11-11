using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace MemoryShell;

internal static class TransferNotifications
{
    private static bool registered, closed;
    internal static string? LastError { get; private set; }
    internal static uint LastNotificationId { get; private set; }

    internal static async Task ShowCompletedAsync()
    {
        await Task.Delay(1200);
        if (closed) return;
        try
        {
            var manager = AppNotificationManager.Default;
            if (!registered) { manager.Register(); registered = true; }
            var notification = new AppNotificationBuilder()
                .AddText("Your Memories Shell")
                .AddText(L10n.Get("S305EEF7A84A9"))
                .BuildNotification();
            manager.Show(notification);
            LastNotificationId = notification.Id;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: 0x{ex.HResult:X8}";
            System.Diagnostics.Debug.WriteLine(LastError);
        }
    }

    internal static void Close()
    {
        closed = true;
        if (!registered) return;
        try { AppNotificationManager.Default.Unregister(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.HResult); }
        registered = false;
    }
}
