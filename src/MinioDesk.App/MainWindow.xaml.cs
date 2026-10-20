using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using MinioDesk.Core;

namespace MinioDesk.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    private readonly DispatcherTimer queryTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    public MainWindow()
    {
        InitializeComponent(); DataContext = ViewModel; ThemeManager.Attach(this);
        queryTimer.Tick += (_, _) =>
        {
            queryTimer.Stop();
            if (ViewModel.AutoRunQuery && ViewModel.QueryText != ViewModel.LastAttemptedQuery && ViewModel.ExecuteQueryCommand.CanExecute(null))
                ViewModel.ExecuteQueryCommand.Execute(null);
        };
        ViewModel.PropertyChanged += (_, args) =>
        {
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

    private (int Start, int End, string Prefix) CompletionSpan()
    {
        var text = QueryEditor.Text; var end = QueryEditor.CaretIndex; var start = end;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        var prefix = text[start..end];
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
        if (start > 0 && text[start - 1] == '"') start--;
        if (end < text.Length && text[end] == '"') end++;
        return (start, end, prefix);
    }

    private void UpdateCompletion(bool force)
    {
        if (!QueryEditor.IsKeyboardFocusWithin || !ViewModel.IsTabularPreview) { CompletionPopup.IsOpen = false; return; }
        var span = CompletionSpan();
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
            Binding = new Binding(e.PropertyName) { Mode = BindingMode.OneWay }, Width = new DataGridLength(150), IsReadOnly = true
        };
    }
    private void SchemaColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        => e.Column = new SelectableTextColumn { Header = e.PropertyName, Binding = new Binding(e.PropertyName) { Mode = BindingMode.OneWay }, Width = new DataGridLength(180), IsReadOnly = true };
    protected override void OnClosing(CancelEventArgs e)
    {
        if (ViewModel.IsBusy) { ViewModel.Cancel(); e.Cancel = true; }
        base.OnClosing(e);
    }
}
