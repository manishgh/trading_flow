namespace TradingFlow.Mobile.Controls;

/// <summary>Native form controls carry query values; market filtering remains in the API.</summary>
public sealed class MarketFilterForm : VerticalStackLayout
{
    private readonly Dictionary<string, Func<string?>> values = new();
    private readonly List<(View Container, Action Reset)> privateFields = new();
    private Picker? groupPicker;
    private bool updating;
    public event EventHandler? Changed;
    public MarketFilterForm() { Spacing = 6; }
    private void Notify() { if (!updating) Changed?.Invoke(this, EventArgs.Empty); }
    private void Field(string label, View control, bool isPrivate, Action reset)
    {
        var row = new VerticalStackLayout { Spacing = 2 };
        row.Children.Add(new Label { Text = label, FontSize = 12, FontAttributes = FontAttributes.Bold });
        row.Children.Add(control);
        Children.Add(row);
        if (isPrivate) { row.IsVisible = false; privateFields.Add((row, reset)); }
    }
    public void EntryField(string key, string label, bool numeric = false)
    {
        var entry = new Entry { Placeholder = "Any / unknown included", Keyboard = numeric ? Keyboard.Numeric : Keyboard.Text };
        values[key] = () => entry.Text?.Trim();
        entry.TextChanged += (_, _) => Notify();
        Field(label, entry, false, () => entry.Text = null);
    }
    public Picker Choice(string key, string label, ChoiceValue[] choices, bool isPrivate = false)
    {
        var picker = new Picker { ItemsSource = choices, ItemDisplayBinding = new Binding(nameof(ChoiceValue.Label)), SelectedIndex = 0 };
        values[key] = () => (picker.SelectedItem as ChoiceValue)?.Value;
        picker.SelectedIndexChanged += (_, _) => Notify();
        Field(label, picker, isPrivate, () => picker.SelectedIndex = 0);
        return picker;
    }
    public void Toggle(string key, string label, bool isPrivate = false)
    {
        var toggle = new Switch();
        values[key] = () => toggle.IsToggled ? "true" : null;
        toggle.Toggled += (_, _) => Notify();
        Field(label, toggle, isPrivate, () => toggle.IsToggled = false);
    }
    public void GroupField()
    {
        groupPicker = Choice("groupId", "One group", [new("", "All groups")], true);
    }
    public void SetGroups(IEnumerable<(Guid Id, string Name)> groups)
    {
        if (groupPicker is null) return;
        updating = true;
        try {
            var selected = (groupPicker.SelectedItem as ChoiceValue)?.Value;
            var options = new[] { new ChoiceValue("", "All groups") }.Concat(groups.Select(x => new ChoiceValue(x.Id.ToString(), x.Name))).ToArray();
            groupPicker.ItemsSource = options;
            groupPicker.SelectedIndex = Math.Max(0, Array.FindIndex(options, x => x.Value == selected));
        } finally { updating = false; }
    }
    public void SetAuthenticated(bool authenticated)
    {
        updating = true;
        try {
            foreach (var (container, reset) in privateFields) { container.IsVisible = authenticated; if (!authenticated) reset(); }
            if (!authenticated) SetGroups([]);
        } finally { updating = false; }
    }
    public Dictionary<string, string> Snapshot() => values.Select(x => (x.Key, Value: x.Value())).Where(x => !String.IsNullOrWhiteSpace(x.Value)).ToDictionary(x => x.Key, x => x.Value!);
    public sealed record ChoiceValue(string Value, string Label);
}
