using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MinioDesk.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    public MainWindow() { InitializeComponent(); DataContext = ViewModel; }
    private void OpenObject(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ObjectGrid, e.OriginalSource as DependencyObject) is ListBoxItem) ViewModel.OpenSelected();
    }
    private void OpenObjectKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ViewModel.OpenSelected(); e.Handled = true; }
    }
    private void OpenConnectionMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
    private void PreviewColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (ViewModel.PreviewRows?.Table?.Columns[e.PropertyName] is { } column)
            e.Column.Header = new TextBlock { Text = column.Caption };
        e.Column.Width = new DataGridLength(150);
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (ViewModel.IsBusy) { ViewModel.Cancel(); e.Cancel = true; }
        base.OnClosing(e);
    }
}
