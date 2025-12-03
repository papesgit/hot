using Avalonia;
using Avalonia.Controls;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace HlaeObsTools.Views;

public partial class DockHostWindow : Window, IHostWindow
{
    public DockHostWindow()
    {
        InitializeComponent();
    }

    public IDockWindow? Window { get; set; }

    public IDockManager? DockManager { get; set; }

    public IHostWindowState? HostWindowState { get; set; }

    public bool IsTracked { get; set; }

    public IDockable? DockableViewModel
    {
        get => DockControl?.DataContext as IDockable;
        set
        {
            if (DockControl != null)
            {
                DockControl.DataContext = value;
            }
        }
    }

    public bool OnClose()
    {
        // Allow the window to close
        return true;
    }

    public void OnClosed()
    {
        // Cleanup if needed
    }

    public void Present(bool isDialog)
    {
        if (!isDialog)
        {
            Show();
        }
        else
        {
            if (Owner is Window ownerWindow)
            {
                ShowDialog(ownerWindow);
            }
            else
            {
                ShowDialog(null!);
            }
        }
    }

    public void Exit()
    {
        Close();
    }

    public void SetPosition(double x, double y)
    {
        Position = new PixelPoint((int)x, (int)y);
    }

    public void GetPosition(out double x, out double y)
    {
        x = Position.X;
        y = Position.Y;
    }

    public void SetSize(double width, double height)
    {
        Width = width;
        Height = height;
    }

    public void GetSize(out double width, out double height)
    {
        width = Width;
        height = Height;
    }

    public void SetTitle(string? title)
    {
        if (!string.IsNullOrEmpty(title))
        {
            Title = title;
        }
    }

    public void SetLayout(IDock? dock)
    {
        if (DockControl != null)
        {
            DockControl.Layout = dock;
        }
    }

    public void SetActive()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
    }
}
