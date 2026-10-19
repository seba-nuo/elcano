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
        var window = new MainWindow();
        MainWindow = window;
        if (e.Args.Length == 2 && e.Args[0] == "--render-preview")
        {
            window.ViewModel.LoadDesignPreview();
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -16000; window.Top = -16000;
            window.Show();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                window.UpdateLayout();
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.GetFullPath(e.Args[1]))) encoder.Save(output);
                Shutdown();
            });
        }
        else { window.Show(); window.ViewModel.Initialize(); }
    }
}
