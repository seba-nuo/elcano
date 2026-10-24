using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Elcano.Core;

namespace Elcano.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private bool restoringExplorer;
    private readonly DispatcherTimer queryTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    public MainWindow() : this(new MainViewModel()) { }
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent(); DataContext = ViewModel; ThemeManager.Attach(this);
        queryTimer.Tick += (_, _) =>
        {
            queryTimer.Stop();
            if (ViewModel.AutoRunQuery && ViewModel.QueryText != ViewModel.LastAttemptedQuery && ViewModel.ExecuteQueryCommand.CanExecute(null))
                ViewModel.ExecuteQueryCommand.Execute(null);
        };
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.Entries) or nameof(MainViewModel.ExplorerScrollOffset))
            {
                restoringExplorer = true;
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                {
                    FindScroll(ObjectGrid)?.ScrollToVerticalOffset(ViewModel.ExplorerScrollOffset); restoringExplorer = false;
                });
            }
            if (args.PropertyName is nameof(MainViewModel.IsIdle) or nameof(MainViewModel.AutoRunQuery) && ViewModel.IsIdle && ViewModel.IsPreviewOpen &&
                ViewModel.AutoRunQuery && ViewModel.QueryText != ViewModel.LastAttemptedQuery) queryTimer.Start();
        };
        Closed += (_, _) => { queryTimer.Stop(); CompletionPopup.IsOpen = false; ViewModel.StopBackgroundWork(); };
    }
    private void OpenObject(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ObjectGrid, e.OriginalSource as DependencyObject) is ListBoxItem) ViewModel.OpenSelected();
    }
    private void OpenObjectKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ViewModel.OpenSelected(); e.Handled = true; }
    }
    private void SelectContextFile(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ObjectGrid, e.OriginalSource as DependencyObject) is ListBoxItem item)
        { item.IsSelected = true; item.Focus(); }
        else ObjectGrid.SelectedItem = null;
    }
    private void OpenFileMenu(object sender, ContextMenuEventArgs e)
    {
        if (ViewModel.SelectedEntry is null) e.Handled = true;
    }

    private void QueryChanged(object sender, EventArgs e)
    {
        if (!IsInitialized || CompletionPopup is null) return;
        UpdateCompletion(false);
        queryTimer.Stop();
        if (ViewModel.AutoRunQuery && ViewModel.IsPreviewOpen && ViewModel.IsIdle && ViewModel.QueryText != ViewModel.LastAttemptedQuery)
            queryTimer.Start();
    }

    private void FolderPathChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox text)
            text.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { text.CaretIndex = text.Text.Length; text.ScrollToHorizontalOffset(double.MaxValue); });
    }
    private void OpenBreadcrumb(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FolderCrumb crumb } button) return;
        if (!crumb.IsOverflow) { ViewModel.NavigateToPrefix(crumb.Prefix); return; }
        var menu = new ContextMenu { PlacementTarget = button };
        foreach (var ancestor in ViewModel.Ancestors)
        {
            var item = new MenuItem { Header = ancestor.Name, ToolTip = ancestor.Prefix, IsEnabled = ViewModel.IsIdle };
            item.Click += (_, _) => ViewModel.NavigateToPrefix(ancestor.Prefix);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void ResizeQueryEditor(object sender, DragDeltaEventArgs e)
    {
        var maximum = Math.Max(44, Math.Min(400, FileViewer.ActualHeight - 220));
        QueryEditor.Height = Math.Clamp(QueryEditor.ActualHeight + e.VerticalChange, 44, maximum);
        e.Handled = true;
    }

    private void QuerySelectionChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && CompletionPopup is not null && QueryEditor.IsKeyboardFocusWithin) UpdateCompletion(false);
    }

    private SqlCompletionSpan CompletionSpan() => SqlCompletionSpan.At(QueryEditor.Text, QueryEditor.CaretIndex);

    private void UpdateCompletion(bool force)
    {
        if (!QueryEditor.IsKeyboardFocusWithin || !ViewModel.IsTabularPreview) { CompletionPopup.IsOpen = false; return; }
        var span = CompletionSpan();
        if (!span.CanComplete) { CompletionPopup.IsOpen = false; return; }
        if (!force && span.Prefix.Length == 0) { CompletionPopup.IsOpen = false; return; }
        var suggestions = ViewModel.QuerySuggestions(span.Prefix);
        CompletionList.ItemsSource = suggestions;
        CompletionList.SelectedIndex = suggestions.Count > 0 ? 0 : -1;
        var caret = QueryEditor.GetRectFromCharacterIndex(QueryEditor.CaretIndex, true);
        if (!caret.IsEmpty) { CompletionPopup.HorizontalOffset = caret.X; CompletionPopup.VerticalOffset = caret.Bottom + 3; }
        CompletionPopup.IsOpen = suggestions.Count > 0;
    }

    private void QueryKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.Space) { UpdateCompletion(true); e.Handled = true; return; }
            if (e.Key == Key.Enter)
            {
                CompletionPopup.IsOpen = false; queryTimer.Stop();
                if (ViewModel.ExecuteQueryCommand.CanExecute(null)) ViewModel.ExecuteQueryCommand.Execute(null);
                e.Handled = true; return;
            }
        }
        if (!CompletionPopup.IsOpen) return;
        if (e.Key == Key.Escape) { CompletionPopup.IsOpen = false; e.Handled = true; }
        else if (e.Key is Key.Tab or Key.Enter) { InsertCompletion(); e.Handled = true; }
        else if (e.Key is Key.Up or Key.Down)
        {
            CompletionList.SelectedIndex = Math.Clamp(CompletionList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, CompletionList.Items.Count - 1);
            CompletionList.ScrollIntoView(CompletionList.SelectedItem); e.Handled = true;
        }
    }

    private void AcceptCompletion(object sender, MouseButtonEventArgs e) => InsertCompletion();
    private void InsertCompletion()
    {
        if (CompletionList.SelectedItem is not SqlSuggestion suggestion) return;
        var span = CompletionSpan();
        if (!span.CanComplete) { CompletionPopup.IsOpen = false; return; }
        CompletionPopup.IsOpen = false;
        QueryEditor.Select(span.Start, span.End - span.Start);
        QueryEditor.SelectedText = suggestion.InsertText;
        QueryEditor.CaretIndex = span.Start + suggestion.InsertText.Length;
        QueryEditor.Focus(); CompletionPopup.IsOpen = false;
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
        e.Column = new SelectableTextColumn
        {
            Header = ViewModel.PreviewRows?.Table?.Columns[e.PropertyName]?.Caption ?? e.PropertyName,
            Binding = new Binding(e.PropertyName) { Mode = BindingMode.OneWay }, SortMemberPath = e.PropertyName, Width = new DataGridLength(150), IsReadOnly = true
        };
    }
    private void SchemaColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        => e.Column = new SelectableTextColumn { Header = e.PropertyName, Binding = new Binding(e.PropertyName) { Mode = BindingMode.OneWay }, Width = new DataGridLength(180), IsReadOnly = true };
    private void SortPreview(object sender, DataGridSortingEventArgs e)
    {
        var descending = e.Column.SortDirection == ListSortDirection.Ascending;
        e.Handled = true; ViewModel.SortPreview(e.Column.SortMemberPath, descending);
        foreach (var column in PreviewGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
    }
    private void ExplorerScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (!restoringExplorer && e.OriginalSource is ScrollViewer viewer) ViewModel.RememberExplorerScroll(viewer.VerticalOffset);
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer found) return found;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (ViewModel.IsBusy) { ViewModel.Cancel(); e.Cancel = true; }
        base.OnClosing(e);
    }
}
