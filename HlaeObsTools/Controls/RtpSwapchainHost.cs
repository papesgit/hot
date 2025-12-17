using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace HlaeObsTools.Controls;

/// <summary>
/// Hosts a native RTP swapchain child window inside Avalonia. Windows-only; no-op elsewhere.
/// </summary>
public class RtpSwapchainHost : NativeControlHost
{
    private IntPtr _containerHwnd;
    private IntPtr _childHwnd;
    private IPlatformHandle? _parentHandle;
    private bool _childLayoutSet;
    private int _childX;
    private int _childY;
    private int _childW;
    private int _childH;
    private int _containerW;
    private int _containerH;
    private bool _containerLayoutSet;
    private int _containerX;
    private int _containerY;
    private int _containerLayoutW;
    private int _containerLayoutH;

    public event EventHandler<IntPtr>? ContainerHandleChanged;
    public IntPtr ContainerHwnd => _containerHwnd;

    /// <summary>
    /// Set the HWND created by RtpSwapchainViewer to embed it as a child.
    /// Must be called before the control is added to the visual tree or before OnPlatformHandleCreated.
    /// </summary>
    public void SetChildHwnd(IntPtr hwnd)
    {
        _childHwnd = hwnd;
        AttachViewerToContainer();
        UpdateChildBounds();
    }

    public void SetChildLayout(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        _childLayoutSet = true;
        _childX = x;
        _childY = y;
        _childW = width;
        _childH = height;

        ApplyChildLayout();
    }

    public void SetContainerLayout(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        _containerLayoutSet = true;
        _containerX = x;
        _containerY = y;
        _containerLayoutW = width;
        _containerLayoutH = height;

        UpdateChildBounds();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _parentHandle = parent;
        if (!OperatingSystem.IsWindows())
            return base.CreateNativeControlCore(parent);

        _containerHwnd = CreateChildWindow(parent.Handle);
        ApplyTransparentStyle(_containerHwnd);
        ContainerHandleChanged?.Invoke(this, _containerHwnd);
        AttachViewerToContainer();

        return new PlatformHandle(_containerHwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // The viewer owns the child window lifecycle; do nothing here.
        if (_containerHwnd != IntPtr.Zero)
        {
            DestroyWindow(_containerHwnd);
            _containerHwnd = IntPtr.Zero;
            ContainerHandleChanged?.Invoke(this, IntPtr.Zero);
        }
        _parentHandle = null;
        base.DestroyNativeControlCore(control);
    }

    protected override void OnMeasureInvalidated()
    {
        base.OnMeasureInvalidated();
        UpdateChildBounds();
    }

    public void UpdateChildBounds()
    {
        if (!OperatingSystem.IsWindows() || _parentHandle == null || _containerHwnd == IntPtr.Zero)
            return;

        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0)
            return;

        double scale = (this.VisualRoot as Avalonia.Rendering.IRenderRoot)?.RenderScaling ?? 1.0;
        int baseX = (int)Math.Round(b.X * scale);
        int baseY = (int)Math.Round(b.Y * scale);
        int baseW = (int)Math.Round(b.Width * scale);
        int baseH = (int)Math.Round(b.Height * scale);

        int x;
        int y;
        int w;
        int h;
        if (_containerLayoutSet)
        {
            x = baseX + _containerX;
            y = baseY + _containerY;
            w = _containerLayoutW;
            h = _containerLayoutH;
        }
        else
        {
            x = baseX;
            y = baseY;
            w = baseW;
            h = baseH;
        }
        _containerW = w;
        _containerH = h;

        const uint flags = 0x0014; // SWP_NOZORDER | SWP_NOACTIVATE
        SetWindowPos(_containerHwnd, IntPtr.Zero, x, y, w, h, flags);

        ApplyChildLayout();
    }

    private void ApplyChildLayout()
    {
        if (_childHwnd == IntPtr.Zero || _containerHwnd == IntPtr.Zero)
            return;

        int x = _childLayoutSet ? _childX : 0;
        int y = _childLayoutSet ? _childY : 0;
        int w = _childLayoutSet ? _childW : Math.Max(1, _containerW);
        int h = _childLayoutSet ? _childH : Math.Max(1, _containerH);
        const uint flags = 0x0014; // SWP_NOZORDER | SWP_NOACTIVATE
        SetWindowPos(_childHwnd, IntPtr.Zero, x, y, w, h, flags);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(IntPtr lpModuleName);

    private static IntPtr CreateChildWindow(IntPtr parent)
    {
        const int WS_CHILD = 0x40000000;
        const int WS_VISIBLE = 0x10000000;
        const int WS_CLIPSIBLINGS = 0x04000000;
        const int WS_CLIPCHILDREN = 0x02000000;
        const int SS_BLACKRECT = 0x00000004;
        return CreateWindowEx(
            0,
            "STATIC",
            "",
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | SS_BLACKRECT,
            0, 0, 32, 32,
            parent,
            IntPtr.Zero,
            GetModuleHandle(IntPtr.Zero),
            IntPtr.Zero);
    }

    private static void ApplyTransparentStyle(IntPtr hwnd)
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_NOACTIVATE = 0x08000000;
        if (hwnd == IntPtr.Zero) return;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    }

    private void AttachViewerToContainer()
    {
        if (_childHwnd == IntPtr.Zero || _containerHwnd == IntPtr.Zero)
            return;

        SetParent(_childHwnd, _containerHwnd);

        const int GWL_STYLE = -16;
        const int WS_CHILD = 0x40000000;
        const int WS_VISIBLE = 0x10000000;
        const int WS_CLIPSIBLINGS = 0x04000000;
        const int WS_OVERLAPPEDWINDOW = 0x00CF0000;

        int style = GetWindowLong(_childHwnd, GWL_STYLE);
        style &= ~WS_OVERLAPPEDWINDOW;
        style |= WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
        SetWindowLong(_childHwnd, GWL_STYLE, style);
    }
}
