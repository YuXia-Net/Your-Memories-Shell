using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace MemoryShell;

internal sealed class FirstLanguageWindow : Window
{
    public FirstLanguageWindow(Action continueToApp)
    {
        Title = "你的记忆Shell终端 / Your Memories Shell";
        WorkspaceTheme.Apply(WorkspaceAppearance.Default);
        var heading = new TextBlock
        {
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = WorkspaceTheme.Brush("ShellTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var picker = new ListView { SelectionMode = ListViewSelectionMode.Single };
        foreach (var language in L10n.Languages)
            picker.Items.Add(new ListViewItem { Content = language.NativeName, Tag = language.Code, MinHeight = 44 });

        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = WorkspaceTheme.Brush("ShellErrorBrush")
        };
        var next = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 112,
            Style = (Style)Application.Current.Resources["ShellPrimaryButtonStyle"]
        };
        void UpdateLanguage()
        {
            var code = (picker.SelectedItem as ListViewItem)?.Tag as string ?? L10n.Code;
            (heading.Text, next.Content) = code switch
            {
                "zh-CN" => ("选择语言", "继续"),
                "zh-TW" => ("選擇語言", "繼續"),
                "ja" => ("言語を選択", "続行"),
                "ko" => ("언어 선택", "계속"),
                "de" => ("Sprache auswählen", "Weiter"),
                "fr" => ("Choisir la langue", "Continuer"),
                "es" => ("Elegir idioma", "Continuar"),
                _ => ("Choose a language", "Continue")
            };
        }
        picker.SelectionChanged += (_, _) => UpdateLanguage();
        picker.SelectedIndex = Math.Max(0, Array.FindIndex(L10n.Languages, item => item.Code == L10n.Code));
        UpdateLanguage();
        next.Click += (_, _) =>
        {
            if (picker.SelectedItem is not ListViewItem { Tag: string code }) return;
            try
            {
                L10n.SavePreference(code);
                L10n.ApplyCode(code);
                continueToApp();
                Close();
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        picker.DoubleTapped += (_, _) => next.Focus(FocusState.Programmatic);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            Children =
            {
                new Image { Width = 48, Height = 48, Source = new BitmapImage(new Uri("ms-appx:///Assets/MemoryShell.png")) },
                heading
            }
        };
        var root = new Grid
        {
            RequestedTheme = ElementTheme.Light,
            Background = WorkspaceTheme.Brush("ShellBaseBrush"),
            Padding = new Thickness(28),
            RowSpacing = 18
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(header);
        var listBorder = new Border
        {
            Child = picker,
            BorderBrush = WorkspaceTheme.Brush("ShellLineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        Grid.SetRow(listBorder, 1);
        root.Children.Add(listBorder);
        Grid.SetRow(error, 2);
        root.Children.Add(error);
        Grid.SetRow(next, 3);
        root.Children.Add(next);
        Content = root;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96d;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min((int)Math.Round(540 * scale), workArea.Width);
        var height = Math.Min((int)Math.Round(620 * scale), workArea.Height);
        AppWindow.MoveAndResize(new RectInt32(workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2, width, height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
