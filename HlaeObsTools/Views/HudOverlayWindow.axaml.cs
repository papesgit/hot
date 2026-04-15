using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views;

public partial class HudOverlayWindow : Window
{
    public event EventHandler? RightButtonDown;
    public event EventHandler? RightButtonUp;
    public event EventHandler<bool>? ShiftKeyChanged;
    private static readonly object WndProcLock = new();
    private static WndProcDelegate? _wndProc;
    private static IntPtr _wndProcPtr = IntPtr.Zero;
    private static readonly Dictionary<IntPtr, IntPtr> WndProcMap = new();
    private static readonly Dictionary<IntPtr, IntPtr> OwnerMap = new();

    public HudOverlayWindow()
    {
        InitializeComponent();
        ShowActivated = false;
        Focusable = false;

        // Make the window layered for transparency, but NOT click-through
        // This window handles all mouse interactions for the HUD
        if (OperatingSystem.IsWindows())
        {
            this.Opened += OnWindowOpened;
        }

        // Subscribe to pointer events for freecam control
        this.PointerPressed += OnPointerPressed;
        this.PointerReleased += OnPointerReleased;

        // Subscribe to keyboard events for shift key detection
        this.KeyDown += OnKeyDown;
        this.KeyUp += OnKeyUp;
    }

    public Canvas? GetSpeedScaleCanvas()
    {
        return HudContent?.GetSpeedScaleCanvas();
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;

        // Make window layered for transparency but still receive mouse events,
        // while preventing it from stealing focus.
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            MakeLayered(hwnd);
            var ownerHwnd = Owner?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            InstallNoActivateWndProc(hwnd, ownerHwnd);
        }
    }

    private void MakeLayered(IntPtr hwnd)
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_NOACTIVATE = 0x08000000;

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE;
        // Note: NOT adding WS_EX_TRANSPARENT so the window can receive mouse events
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            RightButtonDown?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsRightButtonPressed)
        {
            RightButtonUp?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            ShiftKeyChanged?.Invoke(this, true);
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            ShiftKeyChanged?.Invoke(this, false);
            e.Handled = true;
        }
    }

    public void UpdatePositionAndSize(PixelPoint position, PixelSize size)
    {
        Position = position;
        Width = size.Width;
        Height = size.Height;

        // Update SpeedScaleRegion size when window resizes
        var speedScaleRegion = HudContent?.GetSpeedScaleRegion();
        if (speedScaleRegion != null && size.Width > 0 && size.Height > 0)
        {
            speedScaleRegion.Width = size.Width * 0.3;
            speedScaleRegion.Height = size.Height * 0.4;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                UninstallWndProc(hwnd);
            }
        }
        base.OnClosed(e);
    }

    private static void InstallNoActivateWndProc(IntPtr hwnd, IntPtr ownerHwnd)
    {
        lock (WndProcLock)
        {
            if (WndProcMap.ContainsKey(hwnd))
            {
                OwnerMap[hwnd] = ownerHwnd;
                return;
            }

            if (_wndProc == null)
            {
                _wndProc = HostWndProc;
                _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
            }

            var oldProc = SetWindowLongPtr(hwnd, GWL_WNDPROC, _wndProcPtr);
            if (oldProc != IntPtr.Zero)
            {
                WndProcMap[hwnd] = oldProc;
                OwnerMap[hwnd] = ownerHwnd;
            }
        }
    }

    private static void UninstallWndProc(IntPtr hwnd)
    {
        lock (WndProcLock)
        {
            if (!WndProcMap.TryGetValue(hwnd, out var oldProc))
                return;

            SetWindowLongPtr(hwnd, GWL_WNDPROC, oldProc);
            WndProcMap.Remove(hwnd);
            OwnerMap.Remove(hwnd);
        }
    }

    private static IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_MOUSEACTIVATE = 0x0021;
        const uint WM_ACTIVATE = 0x0006;
        const uint WM_ACTIVATEAPP = 0x001C;
        const int MA_NOACTIVATE = 3;
        const int WA_INACTIVE = 0;

        if (msg == WM_MOUSEACTIVATE)
        {
            return new IntPtr(MA_NOACTIVATE);
        }

        if (msg == WM_ACTIVATE || msg == WM_ACTIVATEAPP)
        {
            int state = (int)(wParam.ToInt64() & 0xFFFF);
            if (state != WA_INACTIVE)
            {
                if (TryGetOwner(hWnd, out var owner))
                {
                    SetForegroundWindow(owner);
                    SetFocus(owner);
                }
                return IntPtr.Zero;
            }
        }

        lock (WndProcLock)
        {
            if (WndProcMap.TryGetValue(hWnd, out var oldProc))
            {
                return CallWindowProc(oldProc, hWnd, msg, wParam, lParam);
            }
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }
    private static bool TryGetOwner(IntPtr hwnd, out IntPtr owner)
    {
        lock (WndProcLock)
        {
            if (OwnerMap.TryGetValue(hwnd, out owner))
                return owner != IntPtr.Zero;
        }

        owner = IntPtr.Zero;
        return false;
    }

    private const int GWL_WNDPROC = -4;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
