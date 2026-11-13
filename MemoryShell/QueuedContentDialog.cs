using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed class QueuedContentDialog : ContentDialog
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static TaskCompletionSource? unlocked;
    private static QueuedContentDialog? current;
    private bool dismissed;
    public bool IsLockDialog { get; init; }
    public CancellationToken Cancellation { get; init; }
    public Func<bool>? CanShow { get; set; }

    public static void SetLocked(bool locked)
    {
        if (locked)
        {
            unlocked ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (current is { IsLockDialog: false }) current.Hide();
        }
        else { var wait = unlocked; unlocked = null; wait?.TrySetResult(); }
    }

    public new void Hide() { dismissed = true; base.Hide(); }

    public new async Task<ContentDialogResult> ShowAsync()
    {
        while (true)
        {
            if (!IsLockDialog && unlocked is { } wait) await wait.Task.WaitAsync(Cancellation);
            await Gate.WaitAsync(Cancellation);
            if (!IsLockDialog && unlocked is not null) { Gate.Release(); continue; }
            try
            {
                if (dismissed || CanShow?.Invoke() == false) return ContentDialogResult.None;
                Cancellation.ThrowIfCancellationRequested();
                current = this;
                using var cancel = Cancellation.Register(() => DispatcherQueue.TryEnqueue(() => Hide()));
                return await base.ShowAsync();
            }
            finally { if (ReferenceEquals(current, this)) current = null; Gate.Release(); }
        }
    }
}
