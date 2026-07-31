using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HlaeObsTools.Services.Graphics;
using HlaeObsTools.Services.Updates;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Views;
using HlaeObsTools.Views.Docks;
using System;
using System.Threading.Tasks;

namespace HlaeObsTools;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    #if DEBUG
        this.AttachDeveloperTools();
    #endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => D3D11DeviceService.Instance.Dispose();
            StartDesktopApp(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void StartDesktopApp(IClassicDesktopStyleApplicationLifetime desktop)
    {
        VideoDisplayDockView.ResetStartupReadySignal();

        var progress = new StartupProgressViewModel();
        var splashScreen = new SplashScreenWindow
        {
            DataContext = progress
        };
        splashScreen.Show();

        Dispatcher.UIThread.Post(async () =>
        {
            MainWindowViewModel? mainWindowViewModel = null;
            MainWindow? mainWindow = null;

            try
            {
                await ReportStartupProgressAsync(progress, "Starting application...", "Preparing the startup workflow.", 0);

                mainWindowViewModel = new MainWindowViewModel();
                await mainWindowViewModel.InitializeAsync((status, detail, value) =>
                    ReportStartupProgressAsync(progress, status, detail, value));

                await ReportStartupProgressAsync(progress, "Creating window shell...", "Constructing the main window and loading its XAML.", 13);

                mainWindow = new MainWindow(mainWindowViewModel);

                await ReportStartupProgressAsync(progress, "Binding workspace to window...", "Attaching the prepared workspace and desktop lifetime state.", 14);

                desktop.MainWindow = mainWindow;
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

                await ReportStartupProgressAsync(progress, "Initializing main window...", "Opening the main shell and preparing the first layout pass.", 15);

                mainWindow.ShowInTaskbar = false;
                mainWindow.WindowState = WindowState.Minimized;
                mainWindow.Show();

                await ReportStartupProgressAsync(progress, "Finalizing workspace...", "Waiting for dock content to finalize.", 16);
                if (mainWindowViewModel.ShouldWaitForVideoDockStartup)
                {
                    try
                    {
                        await VideoDisplayDockView.WaitForStartupReadyAsync()
                            .WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch (TimeoutException)
                    {
                        Console.WriteLine("Video display startup readiness timed out; continuing application startup.");
                    }
                }

                await ReportStartupProgressAsync(progress, "Completing startup...", "Opening main window.", 17);

                mainWindow.ShowInTaskbar = true;
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();

                _ = new UpdateCheckService().CheckForUpdatesAsync(mainWindow);
            }
            catch (Exception)
            {
                mainWindowViewModel?.Dispose();
                splashScreen.Close();
                desktop.Shutdown();
                throw;
            }
            finally
            {
                if (mainWindow != null)
                {
                    splashScreen.Close();
                }
            }
        }, DispatcherPriority.Background);
    }

    private static async Task ReportStartupProgressAsync(StartupProgressViewModel progress, string status, string detail, double value)
    {
        progress.StatusText = status;
        progress.DetailText = detail;
        progress.ProgressValue = value;

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Yield();
    }
}
