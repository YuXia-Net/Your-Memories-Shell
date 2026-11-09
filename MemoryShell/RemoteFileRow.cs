using MemoryShell.Services;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

public sealed record RemoteFileRow(RemoteEntry Entry, string Name, string Size, string Kind,
    string Mode, string Owner, string Modified)
{
    public Symbol Icon => Entry.IsDirectory ? Symbol.Folder : Symbol.Document;
}
