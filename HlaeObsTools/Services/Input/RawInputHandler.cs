using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HlaeObsTools.Services.Input;

/// <summary>
/// Handles global Windows Raw Input for mouse and keyboard capture.
/// Aggregates deltas and periodically flushes them into <see cref="HlaeInputSender"/>.
/// </summary>
public class RawInputHandler : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const int RIDEV_INPUTSINK = 0x00000100;
    private const int RID_INPUT = 0x10000003;

    private readonly NativeWindow _messageWindow;
    private readonly HashSet<Keys> _currentlyPressedKeys = new();
    private readonly object _lockObject = new();

    // Accumulated mouse state
    private int _mouseDx;
    private int _mouseDy;
    private int _mouseWheel;
    private bool _mouseLeftButton;
    private bool _mouseRightButton;
    private bool _mouseMiddleButton;

    // Previous button state to detect changes
    private bool _prevMouseLeftButton;
    private bool _prevMouseRightButton;
    private bool _prevMouseMiddleButton;

    // Track keyboard state changes between flushes
    private bool _keysDirty;

    private bool _captureOnlyWhenFocused;
    private IntPtr _focusedWindowHandle;

    private HlaeInputSender? _inputSender;

    public bool CaptureOnlyWhenFocused
    {
        get => _captureOnlyWhenFocused;
        set => _captureOnlyWhenFocused = value;
    }

    public void SetFocusedWindow(IntPtr handle)
    {
        _focusedWindowHandle = handle;
    }

    public void SetInputSender(HlaeInputSender sender)
    {
        _inputSender = sender;
    }

    public RawInputHandler()
    {
        _messageWindow = new InputMessageWindow(this);
        _messageWindow.CreateHandle(new CreateParams());
        RegisterRawInputDevices(_messageWindow.Handle);
    }

    private void RegisterRawInputDevices(IntPtr hwnd)
    {
        var devices = new RAWINPUTDEVICE[2];

        // Mouse
        devices[0].usUsagePage = 0x01;
        devices[0].usUsage = 0x02;
        devices[0].dwFlags = RIDEV_INPUTSINK;
        devices[0].hwndTarget = hwnd;

        // Keyboard
        devices[1].usUsagePage = 0x01;
        devices[1].usUsage = 0x06;
        devices[1].dwFlags = RIDEV_INPUTSINK;
        devices[1].hwndTarget = hwnd;

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
        {
            throw new InvalidOperationException("Failed to register raw input devices.");
        }
    }

    private void ProcessRawInput(IntPtr lParam)
    {
        // Optional focus gating
        if (_captureOnlyWhenFocused && _focusedWindowHandle != IntPtr.Zero)
        {
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow != _focusedWindowHandle)
            {
                return;
            }
        }

        uint dwSize = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref dwSize, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));

        if (dwSize == 0)
            return;

        IntPtr buffer = Marshal.AllocHGlobal((int)dwSize);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref dwSize, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER))) != dwSize)
                return;

            var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);

            if (raw.header.dwType == 0) // Mouse
            {
                ProcessMouseInput(ref raw.mouse);
            }
            else if (raw.header.dwType == 1) // Keyboard
            {
                ProcessKeyboardInput(ref raw.keyboard);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ProcessMouseInput(ref RAWMOUSE mouse)
    {
        lock (_lockObject)
        {
            // Accumulate mouse deltas (relative movement)
            if ((mouse.usFlags & 0x00) == 0x00) // MOUSE_MOVE_RELATIVE
            {
                _mouseDx += mouse.lLastX;
                _mouseDy += mouse.lLastY;
            }

            // Mouse buttons
            if ((mouse.usButtonFlags & 0x0001) != 0) // RI_MOUSE_LEFT_BUTTON_DOWN
                _mouseLeftButton = true;
            if ((mouse.usButtonFlags & 0x0002) != 0) // RI_MOUSE_LEFT_BUTTON_UP
                _mouseLeftButton = false;

            if ((mouse.usButtonFlags & 0x0004) != 0) // RI_MOUSE_RIGHT_BUTTON_DOWN
                _mouseRightButton = true;
            if ((mouse.usButtonFlags & 0x0008) != 0) // RI_MOUSE_RIGHT_BUTTON_UP
                _mouseRightButton = false;

            if ((mouse.usButtonFlags & 0x0010) != 0) // RI_MOUSE_MIDDLE_BUTTON_DOWN
                _mouseMiddleButton = true;
            if ((mouse.usButtonFlags & 0x0020) != 0) // RI_MOUSE_MIDDLE_BUTTON_UP
                _mouseMiddleButton = false;

            // Mouse wheel
            if ((mouse.usButtonFlags & 0x0400) != 0) // RI_MOUSE_WHEEL
            {
                short wheelDelta = (short)mouse.usButtonData; // WHEEL_DELTA multiples (usually 120)
                _mouseWheel += wheelDelta / 120;
            }
        }
    }

    private void ProcessKeyboardInput(ref RAWKEYBOARD keyboard)
    {
        var key = (Keys)keyboard.VKey;
        bool isKeyDown = (keyboard.Flags & 0x01) == 0; // RI_KEY_MAKE = 0, RI_KEY_BREAK = 1

        lock (_lockObject)
        {
            if (isKeyDown)
            {
                if (_currentlyPressedKeys.Add(key))
                {
                    _keysDirty = true;
                }
            }
            else
            {
                if (_currentlyPressedKeys.Remove(key))
                {
                    _keysDirty = true;
                }
            }
        }
    }

    /// <summary>
    /// Flush accumulated input into the attached <see cref="HlaeInputSender"/>.
    /// Call this periodically (e.g. every 4 ms for ~250 Hz).
    /// </summary>
    public void FlushToSender()
    {
        if (_inputSender == null)
            return;

        int dx, dy, wheel;
        bool left, right, middle;
        Keys[] keys;
        bool buttonChanged;
        bool hasMovement;
        bool keysChanged;

        lock (_lockObject)
        {
            buttonChanged =
                _mouseLeftButton != _prevMouseLeftButton ||
                _mouseRightButton != _prevMouseRightButton ||
                _mouseMiddleButton != _prevMouseMiddleButton;

            hasMovement = _mouseDx != 0 || _mouseDy != 0 || _mouseWheel != 0;

            keysChanged = _keysDirty;

            if (!hasMovement && !buttonChanged && !keysChanged)
            {
                return;
            }

            dx = _mouseDx;
            dy = _mouseDy;
            wheel = _mouseWheel;
            left = _mouseLeftButton;
            right = _mouseRightButton;
            middle = _mouseMiddleButton;

            keys = new Keys[_currentlyPressedKeys.Count];
            _currentlyPressedKeys.CopyTo(keys);

            // Reset accumulators
            _mouseDx = 0;
            _mouseDy = 0;
            _mouseWheel = 0;

            _prevMouseLeftButton = _mouseLeftButton;
            _prevMouseRightButton = _mouseRightButton;
            _prevMouseMiddleButton = _mouseMiddleButton;
            _keysDirty = false;
        }

        // Push into UDP sender outside of lock
        _inputSender.UpdateMouse(dx, dy, wheel);
        _inputSender.UpdateMouseButtons(left, right, middle);
        _inputSender.UpdateKeys(keys);
    }

    public void Dispose()
    {
        _messageWindow.DestroyHandle();
        GC.SuppressFinalize(this);
    }

    private class InputMessageWindow : NativeWindow
    {
        private readonly RawInputHandler _handler;

        public InputMessageWindow(RawInputHandler handler)
        {
            _handler = handler;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                _handler.ProcessRawInput(m.LParam);
            }
            base.WndProc(ref m);
        }
    }

    #region P/Invoke Declarations

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWINPUT
    {
        [FieldOffset(0)]
        public RAWINPUTHEADER header;
        [FieldOffset(24)]
        public RAWMOUSE mouse;
        [FieldOffset(24)]
        public RAWKEYBOARD keyboard;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWMOUSE
    {
        [FieldOffset(0)]
        public ushort usFlags;

        // Union at offset 4 (with 2 bytes padding after usFlags for alignment)
        [FieldOffset(4)]
        public uint ulButtons;

        [FieldOffset(4)]
        public ushort usButtonFlags;

        [FieldOffset(6)]
        public ushort usButtonData;

        [FieldOffset(8)]
        public uint ulRawButtons;

        [FieldOffset(12)]
        public int lLastX;

        [FieldOffset(16)]
        public int lLastY;

        [FieldOffset(20)]
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    #endregion
}
