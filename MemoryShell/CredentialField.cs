using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace MemoryShell;

internal static class CredentialField
{
    internal static TextBox Create(string title, string value, bool multiline)
    {
        var box = new TextBox
        {
            Header = title, IsReadOnly = true, AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.NoWrap : TextWrapping.Wrap,
            MaxHeight = multiline ? 260 : double.PositiveInfinity,
            MinHeight = multiline ? 120 : 0
        };
        if (multiline)
        {
            box.FontFamily = new FontFamily("Consolas");
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        }
        box.Text = value;
        return box;
    }

    internal static Button Copy(string value, bool key = false)
    {
        var button = new Button { Content = L10n.Get(key ? "KeyCopyFull" : "KeyCopy") };
        button.Click += (_, _) =>
        {
            try { var data = new DataPackage(); data.SetText(value); Clipboard.SetContent(data); }
            catch (Exception ex) { ToolTipService.SetToolTip(button, ex.Message); }
        };
        return button;
    }
}
