namespace MemoryShell;

internal sealed record ProbeLayoutPreferences(int Columns = 4, int Rows = 4)
{
    public ProbeLayoutPreferences Normalize() => this with { Columns = Math.Clamp(Columns, 1, 32), Rows = Math.Clamp(Rows, 1, 32) };
}
