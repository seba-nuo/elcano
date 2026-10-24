using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using System.Globalization;

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
        text.SetResourceReference(TextBox.CaretBrushProperty, "Ink");
        text.SetResourceReference(TextBox.SelectionBrushProperty, "SelectionBrush");
        if (Binding is Binding binding)
        {
            text.SetBinding(TextBox.TextProperty, new Binding(binding.Path.Path) { Mode = BindingMode.OneWay, Converter = CellTextConverter.Instance, TargetNullValue = "NULL" });
            var style = new Style(typeof(TextBox), text.Style);
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Ink")));
            var trigger = new DataTrigger { Binding = new Binding(binding.Path.Path) { Converter = CellNullConverter.Instance, TargetNullValue = true }, Value = true };
            trigger.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Muted")));
            trigger.Setters.Add(new Setter(Control.FontStyleProperty, FontStyles.Italic));
            trigger.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, "Valor nulo; distinto de una cadena vacía."));
            style.Triggers.Add(trigger); text.Style = style;
        }
        else text.SetBinding(TextBox.TextProperty, Binding);
        return text;
    }
}
public sealed class CellTextConverter : IValueConverter
{
    public static CellTextConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is null or DBNull ? "NULL" : value.ToString() ?? "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class CellNullConverter : IValueConverter
{
    public static CellNullConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is null or DBNull;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
