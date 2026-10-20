using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MinioDesk.App;

public sealed class SelectableTextColumn : DataGridTextColumn
{
    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var text = new TextBox
        {
            IsReadOnly = true, IsReadOnlyCaretVisible = true, IsTabStop = false,
            BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
        };
        text.SetResourceReference(Control.ForegroundProperty, "Ink");
        text.SetResourceReference(TextBox.CaretBrushProperty, "Ink");
        text.SetResourceReference(TextBox.SelectionBrushProperty, "SelectionBrush");
        text.SetBinding(TextBox.TextProperty, Binding);
        return text;
    }
}
