using System;
using System.Threading.Tasks;

namespace HlaeObsTools.Services.Input;

public static class KeyboardInputGate
{
    private static readonly object SyncRoot = new();
    private static Action<bool>? _suppressionSink;
    private static bool _focusSuppressed;
    private static int _scopedSuppressionCount;
    private static bool _appliedSuppression;

    public static void SetSuppressionSink(Action<bool>? sink)
    {
        bool suppress;
        lock (SyncRoot)
        {
            _suppressionSink = sink;
            suppress = IsSuppressed;
            _appliedSuppression = suppress;
        }

        sink?.Invoke(suppress);
    }

    public static void SetFocusSuppression(bool suppress)
    {
        Action<bool>? sink;
        bool shouldApply;
        bool combined;

        lock (SyncRoot)
        {
            _focusSuppressed = suppress;
            combined = IsSuppressed;
            shouldApply = combined != _appliedSuppression;
            if (shouldApply)
                _appliedSuppression = combined;
            sink = _suppressionSink;
        }

        if (shouldApply)
            sink?.Invoke(combined);
    }

    public static async Task<T> RunSuppressedAsync<T>(Func<Task<T>> action)
    {
        using (Suppress())
        {
            return await action();
        }
    }

    private static IDisposable Suppress()
    {
        UpdateScopedSuppression(1);
        return new Scope();
    }

    private static void UpdateScopedSuppression(int delta)
    {
        Action<bool>? sink;
        bool shouldApply;
        bool combined;

        lock (SyncRoot)
        {
            _scopedSuppressionCount = Math.Max(0, _scopedSuppressionCount + delta);
            combined = IsSuppressed;
            shouldApply = combined != _appliedSuppression;
            if (shouldApply)
                _appliedSuppression = combined;
            sink = _suppressionSink;
        }

        if (shouldApply)
            sink?.Invoke(combined);
    }

    private static bool IsSuppressed => _focusSuppressed || _scopedSuppressionCount > 0;

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            UpdateScopedSuppression(-1);
        }
    }
}
