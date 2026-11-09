using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MemoryShell;

internal static class AppLockDialog
{
    public static bool IsEnabled => AppLockStore.Load() is not null;

    public static async Task<bool> RequireAsync(XamlRoot root)
    {
        if (AppLockStore.Load() is not { } state)
            return true;

        var length = state.CredentialLength;
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(state.Hash);
        var input = new CredentialInput(state.Kind);
        var dialog = Create(root, L10n.Get("S1CBB056E69EA"), input, "", L10n.Get("S2CD0F3BE8738"));
        dialog.DefaultButton = ContentDialogButton.None;
        var timer = input.DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        var active = false;
        var accepted = false;
        var verifying = false;
        var revision = 0;
        var retryAfter = state.RetryAfter;
        Task? pending = null;

        void Schedule()
        {
            timer.Stop();
            if (!active || verifying || !input.CanSubmit || input.Value.Length != length)
                return;
            var remaining = retryAfter - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(remaining, 250, 60_000));
            timer.Start();
        }

        async Task VerifyAsync()
        {
            if (!active || verifying || !input.CanSubmit)
                return;
            var value = input.Value;
            if (value.Length != length)
                return;

            var submitted = revision;
            verifying = true;
            retryAfter = 0;
            try
            {
                var result = await Task.Run(() => AppLockStore.Verify(value));
                if (!active)
                    return;
                if (result.Accepted && input.CanSubmit && input.Value == value)
                {
                    accepted = true;
                    dialog.Hide();
                }
                else
                {
                    retryAfter = result.RetryAfter;
                }
            }
            catch (Exception)
            {
                if (active)
                    input.Error.Text = L10n.Get("S388A88F59C81");
            }
            finally
            {
                verifying = false;
                if (revision != submitted || retryAfter > 0)
                    Schedule();
            }
        }

        input.SubmissionChanged += () => { revision++; Schedule(); };
        timer.Tick += (_, _) => pending = VerifyAsync();
        dialog.Opened += (_, _) =>
        {
            active = true;
            input.FocusPin();
        };
        dialog.Closing += (_, _) =>
        {
            active = false;
            timer.Stop();
        };

        try
        {
            await dialog.ShowAsync();
            return accepted;
        }
        finally
        {
            active = false;
            timer.Stop();
            if (pending is not null)
                await pending;
            input.Clear();
        }
    }

    public static async Task ShowSettingsAsync(XamlRoot root)
    {
        if (!await RequireAsync(root))
            return;

        var state = AppLockStore.Load();
        var editor = new AppLockSetupEditor(state?.Kind);
        if (state is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(state.Hash);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = WorkspaceTheme.Brush("ShellErrorBrush") };
        var panel = new StackPanel { Spacing = 16, Children = { editor, error } };
        var width = Math.Clamp(root.Size.Width - 80, 300, 700);
        panel.Width = width - 48;
        var dialog = Create(root, L10n.Get("S70E06BF53BF0"), new ScrollViewer
        {
            Content = panel, MaxHeight = Math.Max(180, root.Size.Height - 220),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        }, L10n.Get("SA3030BF8F16D"), L10n.Get("S2CD0F3BE8738"));
        dialog.Resources["ContentDialogMaxWidth"] = width;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (editor.ValidationError() is { } message) { args.Cancel = true; error.Text = message; return; }
            var deferral = args.GetDeferral();
            dialog.IsEnabled = false;
            try { await editor.SaveAsync(); }
            catch (Exception ex) { args.Cancel = true; error.Text = ex.Message; }
            finally { dialog.IsEnabled = true; deferral.Complete(); }
        };
        try { await dialog.ShowAsync(); }
        finally { editor.Clear(); }
    }

    private static ContentDialog Create(XamlRoot root, string title, UIElement content, string primary, string close) => new()
    {
        XamlRoot = root, RequestedTheme = (root.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default, Title = title,
        Content = content, PrimaryButtonText = primary, CloseButtonText = close, DefaultButton = ContentDialogButton.Primary
    };

    internal sealed class CredentialInput : StackPanel
    {
        private readonly PasswordBox? pin;
        private readonly PatternGrid? pattern;
        public TextBlock Error { get; } = new() { TextWrapping = TextWrapping.Wrap };
        public string Value => pin?.Password ?? pattern?.Value ?? "";
        public bool CanSubmit => pattern?.IsDrawing != true;
        public event Action? SubmissionChanged;

        public CredentialInput(AppLockKind mode)
        {
            Spacing = 5;
            Children.Add(new TextBlock { Text = AppLockStore.Requirement(mode), TextWrapping = TextWrapping.Wrap });
            if (mode == AppLockKind.Pin)
            {
                pin = new PasswordBox { Header = "PIN", MaxLength = 12, PasswordRevealMode = PasswordRevealMode.Hidden };
                pin.InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.NumericPin) } };
                pin.PasswordChanged += (_, _) => SubmissionChanged?.Invoke();
                Children.Add(pin);
            }
            else
            {
                pattern = new PatternGrid();
                pattern.SubmissionChanged += () => SubmissionChanged?.Invoke();
                Children.Add(pattern);
                var reset = new Button { Content = L10n.Get("SFD5E6CFF3447") };
                reset.Click += (_, _) => pattern.Clear();
                Children.Add(reset);
            }
            Children.Add(Error);
        }

        public void FocusPin() => pin?.Focus(FocusState.Programmatic);

        public void Clear()
        {
            if (pin is not null) pin.Password = "";
            pattern?.Clear();
        }
    }

    private sealed class PatternGrid : Grid
    {
        private const int Cell = 60;
        private readonly List<int> nodes = [];
        private readonly Button[] dots = new Button[9];
        private readonly Canvas trail = new() { IsHitTestVisible = false };
        private readonly Microsoft.UI.Xaml.Shapes.Line guide = new() { Opacity = .55, StrokeThickness = 2 };
        private uint? pointer;
        private Windows.Foundation.Point last;
        private readonly SolidColorBrush blue = WorkspaceTheme.Brush("ShellAccentBrush");
        private readonly SolidColorBrush gray = WorkspaceTheme.Brush("ShellMutedBrush");

        public string Value => new(nodes.Select(index => (char)('1' + index)).ToArray());
        public bool IsDrawing => pointer.HasValue;
        public event Action? SubmissionChanged;

        public PatternGrid()
        {
            Width = Height = Cell * 3;
            HorizontalAlignment = HorizontalAlignment.Center;
            Background = WorkspaceTheme.Brush("ShellPaneBrush");
            CornerRadius = new CornerRadius(8);
            for (var i = 0; i < 3; i++)
            {
                RowDefinitions.Add(new RowDefinition());
                ColumnDefinitions.Add(new ColumnDefinition());
            }
            SetRowSpan(trail, 3);
            SetColumnSpan(trail, 3);
            Children.Add(trail);
            guide.Stroke = blue;
            trail.Children.Add(guide);
            guide.Visibility = Visibility.Collapsed;
            for (var i = 0; i < dots.Length; i++)
            {
                var index = i;
                var dot = new Button
                {
                    Content = "●", Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                    BorderThickness = new Thickness(0), Width = 44, Height = 44, MinWidth = 0, MinHeight = 0,
                    Foreground = gray, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dot, L10n.Format("SC8DE24094ACF", i + 1));
                dot.Click += (_, _) =>
                {
                    if (!IsDrawing && Add(index))
                        SubmissionChanged?.Invoke();
                };
                dots[i] = dot;
                SetRow(dot, i / 3);
                SetColumn(dot, i % 3);
                Children.Add(dot);
            }

            // Capture drags on the overlay; keep buttons keyboard-accessible.
            var gesture = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                ManipulationMode = ManipulationModes.None
            };
            SetRowSpan(gesture, 3);
            SetColumnSpan(gesture, 3);
            Children.Add(gesture);
            gesture.PointerPressed += (_, e) =>
            {
                var point = e.GetCurrentPoint(this);
                var isMouse = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse;
                if (pointer is not null || (isMouse ? !point.Properties.IsLeftButtonPressed : !point.IsInContact))
                    return;
                gesture.CancelDirectManipulations();
                if (!gesture.CapturePointer(e.Pointer))
                    return;
                pointer = e.Pointer.PointerId;
                Clear();
                last = point.Position;
                AddAt(last);
                UpdateGuide();
                e.Handled = true;
            };
            gesture.PointerMoved += (_, e) =>
            {
                if (pointer != e.Pointer.PointerId)
                    return;
                ExtendStroke(e.GetCurrentPoint(this).Position);
                e.Handled = true;
            };
            gesture.PointerReleased += (_, e) =>
            {
                if (pointer != e.Pointer.PointerId)
                    return;
                ExtendStroke(e.GetCurrentPoint(this).Position);
                pointer = null;
                guide.Visibility = Visibility.Collapsed;
                gesture.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
                SubmissionChanged?.Invoke();
            };
            gesture.PointerCanceled += (_, e) =>
            {
                if (pointer != e.Pointer.PointerId)
                    return;
                pointer = null;
                Clear();
            };
            gesture.PointerCaptureLost += (_, e) =>
            {
                if (pointer == e.Pointer.PointerId)
                {
                    pointer = null;
                    Clear();
                }
            };
        }

        private void ExtendStroke(Windows.Foundation.Point point)
        {
            var steps = Math.Clamp((int)Math.Ceiling(Math.Max(Math.Abs(point.X - last.X), Math.Abs(point.Y - last.Y)) / 8), 1, 64);
            for (var i = 1; i <= steps; i++)
                AddAt(new Windows.Foundation.Point(last.X + (point.X - last.X) * i / steps, last.Y + (point.Y - last.Y) * i / steps));
            last = point;
            UpdateGuide();
        }

        private void UpdateGuide()
        {
            if (nodes.Count == 0)
                return;
            var from = nodes[^1];
            guide.X1 = (from % 3 + .5) * Cell;
            guide.Y1 = (from / 3 + .5) * Cell;
            guide.X2 = Math.Clamp(last.X, 0, Width);
            guide.Y2 = Math.Clamp(last.Y, 0, Height);
            guide.Visibility = Visibility.Visible;
        }

        private void AddAt(Windows.Foundation.Point point)
        {
            if (point.X < 0 || point.Y < 0 || point.X >= Width || point.Y >= Height)
                return;
            var col = (int)(point.X / Cell);
            var row = (int)(point.Y / Cell);
            var dx = point.X - (col + .5) * Cell;
            var dy = point.Y - (row + .5) * Cell;
            if (dx * dx + dy * dy <= 289)
                Add(row * 3 + col);
        }

        private bool Add(int index)
        {
            if (nodes.Contains(index))
                return false;
            if (nodes.Count > 0)
            {
                var from = nodes[^1];
                var line = new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = (from % 3 + .5) * Cell, Y1 = (from / 3 + .5) * Cell,
                    X2 = (index % 3 + .5) * Cell, Y2 = (index / 3 + .5) * Cell,
                    Stroke = blue, StrokeThickness = 2
                };
                trail.Children.Add(line);
            }
            nodes.Add(index);
            dots[index].Foreground = blue;
            return true;
        }

        public void Clear()
        {
            nodes.Clear();
            trail.Children.Clear();
            guide.Visibility = Visibility.Collapsed;
            trail.Children.Add(guide);
            foreach (var dot in dots)
                dot.Foreground = gray;
            SubmissionChanged?.Invoke();
        }
    }
}



