using System.Data;
using System.Windows;
using System.Windows.Controls;

namespace Elcano.App;

public partial class ColumnsDialog : Window
{
    private readonly List<ColumnChoice> choices = [];
    public IReadOnlyList<string> SelectedColumns => choices.Where(choice => choice.Selected).Select(choice => choice.Name).ToArray();
    public ColumnsDialog(DataTable schema, IReadOnlyList<string> selected)
    {
        InitializeComponent(); ThemeManager.Attach(this);
        choices = schema.Rows.Cast<DataRow>().Select(row => new ColumnChoice((string)row[0], (string)row[1], selected.Contains((string)row[0], StringComparer.Ordinal))).ToList();
        foreach (var choice in choices) choice.PropertyChanged += (_, _) => UpdateCount();
        ColumnsList.ItemsSource = choices; UpdateCount();
    }
    private void UpdateCount() { var count = choices.Count(choice => choice.Selected); CountLabel.Text = $"{count} / 64"; ApplyButton.IsEnabled = count is >= 1 and <= 64; }
    private void FilterColumns(object sender, TextChangedEventArgs e)
    { if (ColumnsList is not null) ColumnsList.ItemsSource = choices.Where(choice => choice.Name.Contains(Search.Text, StringComparison.OrdinalIgnoreCase)).ToArray(); }
    private void SelectFirst(object sender, RoutedEventArgs e) { for (var i = 0; i < choices.Count; i++) choices[i].Selected = i < 64; }
    private void SelectNone(object sender, RoutedEventArgs e) { foreach (var choice in choices) choice.Selected = false; }
    private void Apply(object sender, RoutedEventArgs e) { if (SelectedColumns.Count is >= 1 and <= 64) DialogResult = true; }
    public sealed class ColumnChoice(string name, string type, bool selected) : Observable
    {
        private bool chosen = selected;
        public string Name => name;
        public string Type => type;
        public bool Selected { get => chosen; set => Set(ref chosen, value); }
    }
}
