using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MinioDesk.App;

public static class ThemeManager
{
    public static bool IsDark { get; private set; }
    public static event Action? ThemeChanged;
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MinioDesk", "appearance.json");
    public static void Attach(Window window) => window.SourceInitialized += (_, _) => ApplyTitleBar(window);

    private static void ApplyTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var background = ((SolidColorBrush)Application.Current.Resources["AppBackground"]).Color;
        var foreground = ((SolidColorBrush)Application.Current.Resources["Ink"]).Color;
        var caption = background.R | background.G << 8 | background.B << 16;
        var text = foreground.R | foreground.G << 8 | foreground.B << 16;
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    public static void Initialize()
    {
        var dark = false;
        try { if (File.Exists(SettingsPath)) dark = JsonSerializer.Deserialize<Appearance>(File.ReadAllText(SettingsPath))?.Dark ?? false; }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
        Apply(dark);
    }

    public static void Apply(bool dark, bool persist = false)
    {
        IsDark = dark;
        var colors = new Dictionary<string, string>
        {
            ["Ink"] = dark ? "#E3EBF2" : "#172E3B",
            ["Muted"] = dark ? "#9AAFBF" : "#637786",
            ["Accent"] = dark ? "#48C7CB" : "#087E83",
            ["AccentText"] = dark ? "#082D31" : "#FFFFFF",
            ["AppBackground"] = dark ? "#111B24" : "#F3F6F8",
            ["Surface"] = dark ? "#182631" : "#FFFFFF",
            ["SurfaceAlt"] = dark ? "#20323F" : "#F4F7F9",
            ["BorderBrush"] = dark ? "#344B5B" : "#DEE5EA",
            ["InputBorder"] = dark ? "#405969" : "#CBD6DD",
            ["HoverBrush"] = dark ? "#243C4B" : "#F1F6F8",
            ["SelectionBrush"] = dark ? "#254C55" : "#E1EFEF",
            ["PlaceholderBrush"] = dark ? "#8AA2B4" : "#8193A0",
            ["ErrorBackground"] = dark ? "#432C31" : "#FCEDEA",
            ["ErrorBorder"] = dark ? "#80505B" : "#EAC2B9",
            ["ErrorText"] = dark ? "#FFB7BA" : "#8C3327",
            ["ScrollThumb"] = dark ? "#516A7B" : "#BAC8D2",
            ["ScrollThumbHover"] = dark ? "#7893A6" : "#899EAD",
            ["SqlKeyword"] = dark ? "#89B4FA" : "#1759AB",
            ["SqlString"] = dark ? "#A6DA95" : "#39713D",
            ["SqlNumber"] = dark ? "#F5A97F" : "#A34A13",
            ["SqlComment"] = dark ? "#8BA39C" : "#6E7E75",
            ["SqlIdentifier"] = dark ? "#7DD3CE" : "#16706E",
            ["SqlFunction"] = dark ? "#C6A0F6" : "#7A3EB1"
        };
        foreach (var pair in colors)
            Application.Current.Resources[pair.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
        // The built-in DataGrid/ScrollViewer uses this brush for the scrollbar corner.
        Application.Current.Resources[SystemColors.ControlBrushKey] = Application.Current.Resources["SurfaceAlt"];
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
        ThemeChanged?.Invoke();
        if (!persist) return;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Appearance(dark)));
    }
    private sealed record Appearance(bool Dark);
}
