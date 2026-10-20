using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MinioDesk.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Initialize();
        if (e.Args.Length == 2 && e.Args[0] is "--render-preview" or "--render-empty-preview" or "--render-connection-preview" or "--render-dark-preview" or "--render-dark-connection-preview")
        {
            ThemeManager.Apply(e.Args[0].Contains("dark", StringComparison.Ordinal));
            Window previewWindow;
            if (e.Args[0].EndsWith("connection-preview", StringComparison.Ordinal)) previewWindow = new ConnectionDialog(null);
            else
            {
                var preview = new MainWindow();
                preview.ViewModel.LoadDesignPreview(e.Args[0] != "--render-empty-preview");
                previewWindow = preview;
            }
            MainWindow = previewWindow;
            RenderPreview(previewWindow, e.Args[1]);
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show(); window.ViewModel.Initialize();
    }

    private void RenderPreview(Window window, string path)
    {
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -16000; window.Top = -16000;
        window.Show();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
            var height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen())
                drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            bitmap.Render(background);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.GetFullPath(path))) encoder.Save(output);
            Shutdown();
        });
    }
}
