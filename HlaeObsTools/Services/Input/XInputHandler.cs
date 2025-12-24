using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace HlaeObsTools.Services.Input;

public sealed class XInputHandler : IDisposable
{
    private readonly HlaeInputSender _inputSender;
    private Timer? _pollTimer;
    private bool _disposed;
    private bool _enabled;
    private float _leftDeadzone;
    private float _rightDeadzone;
    private float _curve;

    public XInputHandler(HlaeInputSender inputSender)
    {
        _inputSender = inputSender;
    }

    public void Start(int pollIntervalMs = 8)
    {
        if (_pollTimer != null)
            return;

        _pollTimer = new Timer(_ => Poll(), null, 0, pollIntervalMs);
    }

    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    public void SetSettings(bool enabled, float leftDeadzone, float rightDeadzone, float curve)
    {
        _enabled = enabled;
        _leftDeadzone = Clamp(leftDeadzone, 0.0f, 0.99f);
        _rightDeadzone = Clamp(rightDeadzone, 0.0f, 0.99f);
        _curve = Math.Max(0.0f, curve);

        _inputSender.SetAnalogEnabled(enabled);
        if (!enabled)
        {
            _inputSender.UpdateAnalog(0.0f, 0.0f, 0.0f, 0.0f);
        }
    }

    private void Poll()
    {
        if (!_enabled)
            return;

        XINPUT_STATE state;
        try
        {
            if (XInputGetState(0, out state) != 0)
            {
                _inputSender.UpdateAnalog(0.0f, 0.0f, 0.0f, 0.0f);
                return;
            }
        }
        catch (DllNotFoundException)
        {
            _inputSender.UpdateAnalog(0.0f, 0.0f, 0.0f, 0.0f);
            SetSettings(false, _leftDeadzone, _rightDeadzone, _curve);
            return;
        }
        catch (EntryPointNotFoundException)
        {
            _inputSender.UpdateAnalog(0.0f, 0.0f, 0.0f, 0.0f);
            SetSettings(false, _leftDeadzone, _rightDeadzone, _curve);
            return;
        }

        float lx = NormalizeAxis(state.Gamepad.sThumbLX);
        float ly = NormalizeAxis(state.Gamepad.sThumbLY);
        float rx = NormalizeAxis(state.Gamepad.sThumbRX);
        float ry = NormalizeAxis(state.Gamepad.sThumbRY);

        lx = ApplyDeadzoneAndCurve(lx, _leftDeadzone, _curve);
        ly = ApplyDeadzoneAndCurve(ly, _leftDeadzone, _curve);
        rx = ApplyDeadzoneAndCurve(rx, _rightDeadzone, _curve);
        ry = ApplyDeadzoneAndCurve(ry, _rightDeadzone, _curve);

        _inputSender.UpdateAnalog(lx, ly, ry, rx);
    }

    private static float NormalizeAxis(short value)
    {
        if (value < 0)
            return Math.Max(-1.0f, value / 32768.0f);
        return Math.Min(1.0f, value / 32767.0f);
    }

    private static float ApplyDeadzoneAndCurve(float value, float deadzone, float curve)
    {
        float abs = Math.Abs(value);
        if (abs <= deadzone)
            return 0.0f;

        float scaled = (abs - deadzone) / (1.0f - deadzone);
        float exponent = 1.0f + (curve * 2.0f);
        float curved = MathF.Pow(scaled, exponent);
        return MathF.CopySign(curved, value);
    }

    private static float Clamp(float value, float min, float max)
    {
        if (value < min)
            return min;
        if (value > max)
            return max;
        return value;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }
}
