using Avalonia;
using System;
using WebViewControl;

namespace HlaeObsTools;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Enable off-screen rendering globally so the HUD WebView can render with transparency.
        WebView.Settings.OsrEnabled = true;

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
