using Avalonia;
using System;

namespace HlaeObsTools;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()  // Use Skia with DirectX interop
            .WithInterFont()
            .LogToTrace();
}
