using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemoryShell;

internal sealed class PresetSelector<T> : StackPanel where T : class
{
    public StackPanel Editor { get; } = new() { Spacing = 8 };
    public PresetSelector(List<T> items, Func<T, Guid> id, Func<T, string> name,
        Func<Guid, string, T> read, Action<T> apply, Action persist, Guid? selected = null, Func<T, string>? displayName = null)
    {
        Spacing = 8;
        var picker = new ComboBox { Header = L10n.Get("PresetChoose"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var label = new TextBox { Header = L10n.Get("S5FE110AA6D1D") };
        var save = new Button();
        var fresh = new Button { Content = L10n.Get("PresetNew") };
        var remove = new Button { Content = L10n.Get("S2F9DAA828907") };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        bool filling = false;
        T? Current() => picker.SelectedItem is ComboBoxItem { Tag: T item } ? item : null;
        void State()
        {
            var current = Current();
            label.Text = current is null ? "" : name(current);
            remove.IsEnabled = current is not null;
            save.Content = L10n.Get(current is null ? "SavePresetApply" : "PresetUpdateApply");
            message.Text = "";
        }
        void Refill(Guid? selection)
        {
            filling = true;
            picker.Items.Clear();
            picker.Items.Add(new ComboBoxItem { Content = L10n.Get("S6E382D9250F8") });
            foreach (var item in items.OrderBy(name, StringComparer.CurrentCultureIgnoreCase))
                picker.Items.Add(new ComboBoxItem { Content = (displayName ?? name)(item), Tag = item });
            picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag is T item && id(item) == selection) ?? picker.Items[0];
            filling = false;
            State();
        }
        picker.SelectionChanged += (_, _) => { if (filling) return; if (Current() is { } item) apply(item); State(); };
        fresh.Click += (_, _) => Save(asNew: true);
        save.Click += (_, _) => Save(asNew: false);
        void Save(bool asNew)
        {
            try
            {
                var selectedItem = Current();
                var current = asNew ? null : selectedItem;
                var value = label.Text.Trim();
                if (value.Length == 0) throw new ArgumentException(L10n.Get("SCAB9417058BF"));
                if (asNew && selectedItem is not null && name(selectedItem).Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    var suffix = 2;
                    var originalName = value;
                    do { value = $"{originalName} ({suffix++})"; }
                    while (items.Any(x => name(x).Equals(value, StringComparison.OrdinalIgnoreCase)));
                }
                if (items.Any(x => x != current && name(x).Equals(value, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException(L10n.Get("S5A8780CDDE5E"));
                var next = read(current is null ? Guid.NewGuid() : id(current), value);
                var index = current is null ? items.Count : items.IndexOf(current);
                if (current is null) items.Add(next); else items[index] = next;
                try { persist(); }
                catch { if (current is null) items.RemoveAt(index); else items[index] = current; throw; }
                apply(next); Refill(id(next)); message.Text = L10n.Get("PresetApplied");
            }
            catch (Exception ex) { message.Text = ex.Message; }
        }
        remove.Click += (_, _) =>
        {
            if (Current() is not { } current) return;
            var index = items.IndexOf(current);
            items.RemoveAt(index);
            try { persist(); Refill(null); }
            catch (Exception ex) { items.Insert(index, current); message.Text = ex.Message; }
        };
        Children.Add(picker);
        Editor.Children.Add(label);
        Editor.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, fresh, remove } });
        Editor.Children.Add(new TextBlock { Text = L10n.Get("PresetHint"), TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 });
        Editor.Children.Add(message);
        Refill(selected);
    }
}
