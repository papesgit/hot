using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace HlaeObsTools.Controls
{
    public class ScrubbyNumericField : TemplatedControl
    {
        // ---- Platform interop for cursor locking ----
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // ---- Bindable properties ----
        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<ScrubbyNumericField, double>(nameof(Value), 0.0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

        public static readonly StyledProperty<double> MinimumProperty =
            AvaloniaProperty.Register<ScrubbyNumericField, double>(nameof(Minimum), double.NegativeInfinity);

        public static readonly StyledProperty<double> MaximumProperty =
            AvaloniaProperty.Register<ScrubbyNumericField, double>(nameof(Maximum), double.PositiveInfinity);

        public static readonly StyledProperty<double> StepProperty =
            AvaloniaProperty.Register<ScrubbyNumericField, double>(nameof(Step), 1.0);

        /// <summary>
        /// Format string for display. Example: "0.###", "0.00", etc.
        /// </summary>
        public static readonly StyledProperty<string> FormatStringProperty =
            AvaloniaProperty.Register<ScrubbyNumericField, string>(nameof(FormatString), "0.###");

        /// <summary>
        /// How many pixels of mouse movement correspond to one "Step".
        /// Lower = more sensitive.
        /// </summary>
        public static readonly StyledProperty<double> PixelsPerStepProperty =
            AvaloniaProperty.Register<ScrubbyNumericField, double>(nameof(PixelsPerStep), 8.0);

        public static readonly StyledProperty<bool> IsEditingProperty =
            AvaloniaProperty.Register<ScrubbyNumericField, bool>(nameof(IsEditing), false);

        public static readonly DirectProperty<ScrubbyNumericField, string> DisplayTextProperty =
            AvaloniaProperty.RegisterDirect<ScrubbyNumericField, string>(
                nameof(DisplayText),
                o => o.DisplayText);

        public string DisplayText => Value.ToString(FormatString, CultureInfo.CurrentCulture);

        public static readonly DirectProperty<ScrubbyNumericField, bool> IsDisplayVisibleProperty =
            AvaloniaProperty.RegisterDirect<ScrubbyNumericField, bool>(
                nameof(IsDisplayVisible),
                o => o.IsDisplayVisible);

        public bool IsDisplayVisible => !IsEditing;

        public double Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public double Minimum
        {
            get => GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public double Maximum
        {
            get => GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public double Step
        {
            get => GetValue(StepProperty);
            set => SetValue(StepProperty, value);
        }

        public string FormatString
        {
            get => GetValue(FormatStringProperty);
            set => SetValue(FormatStringProperty, value);
        }

        public double PixelsPerStep
        {
            get => GetValue(PixelsPerStepProperty);
            set => SetValue(PixelsPerStepProperty, value);
        }

        public bool IsEditing
        {
            get => GetValue(IsEditingProperty);
            private set => SetValue(IsEditingProperty, value);
        }

        // ---- Template parts ----
        private TextBox? _editor;

        // ---- Drag state ----
        private bool _dragging;
        private Point _dragStartPoint;
        private double _dragStartValue;
        private bool _pressed;
        private IPointer? _activePointer;

        // used to accumulate fractional steps smoothly
        private double _accumulatedPixels;

        // screen position to lock cursor during drag
        private POINT _lockCursorPos;

        // used to decide whether a click was “really a drag”
        private const double DragThreshold = 2.0;

        private const string PseudoHover = ":hover";
        private const string PseudoFocus = ":focus";

        static ScrubbyNumericField()
        {
            FocusableProperty.OverrideDefaultValue<ScrubbyNumericField>(true);
        }

        public ScrubbyNumericField()
        {
            AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == ValueProperty || change.Property == FormatStringProperty)
                RaisePropertyChanged(DisplayTextProperty, default!, DisplayText);

            if (change.Property == IsEditingProperty)
                RaisePropertyChanged(IsDisplayVisibleProperty, default!, IsDisplayVisible);
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            _editor = e.NameScope.Find<TextBox>("PART_Editor");

            if (_editor != null)
            {
                _editor.LostFocus -= EditorOnLostFocus;
                _editor.KeyDown -= EditorOnKeyDown;

                _editor.LostFocus += EditorOnLostFocus;
                _editor.KeyDown += EditorOnKeyDown;
            }

            UpdateCursor();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (IsEditing)
                return;

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            Focus();

            _pressed = true;
            _dragging = false;
            _activePointer = e.Pointer;

            _dragStartPoint = e.GetPosition(this);
            _dragStartValue = Value;
            _accumulatedPixels = 0;

            // IMPORTANT: Do NOT set e.Handled here.
            // Let Avalonia still detect tap/double-tap.
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (IsEditing || !_pressed)
                return;

            var p = e.GetPosition(this);
            var dx = p.X - _dragStartPoint.X;

            // Only become a "drag" after threshold.
            if (!_dragging)
            {
                if (Math.Abs(dx) < DragThreshold)
                    return;

                _dragging = true;

                // Capture only once we know it's a drag
                _activePointer?.Capture(this);

                // Store cursor position to lock it in place
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    GetCursorPos(out _lockCursorPos);
                }

                UpdateCursor(isDragging: true);
                e.Handled = true;
            }

            // Get current screen cursor position and calculate delta from lock position
            double deltaPixels;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && GetCursorPos(out var currentPos))
            {
                deltaPixels = currentPos.X - _lockCursorPos.X;

                // Reset cursor to locked position
                if (deltaPixels != 0)
                {
                    SetCursorPos(_lockCursorPos.X, _lockCursorPos.Y);
                }
            }
            else
            {
                // Fallback for non-Windows: use relative position
                deltaPixels = dx - _accumulatedPixels;
            }

            if (Math.Abs(deltaPixels) < 0.001)
                return;

            var sensitivity = GetSensitivityMultiplier(e.KeyModifiers);

            // pixelsPerStep smaller => more sensitive; apply modifier scaling
            var pixelsPerStep = Math.Max(0.001, PixelsPerStep / sensitivity);

            // Accumulate pixels and convert to steps
            _accumulatedPixels += deltaPixels;

            var steps = _accumulatedPixels / pixelsPerStep;
            SetValueClamped(_dragStartValue + steps * Step);

            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (!_pressed)
                return;

            if (_activePointer?.Captured == this)
                _activePointer.Capture(null);

            _pressed = false;
            _dragging = false;
            _activePointer = null;

            UpdateCursor();
            // Don’t need to handle release.
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            PseudoClasses.Set(PseudoHover, true);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            PseudoClasses.Set(PseudoHover, false);
        }

        protected override void OnGotFocus(GotFocusEventArgs e)
        {
            base.OnGotFocus(e);
            PseudoClasses.Set(PseudoFocus, true);

            // If focus came from keyboard navigation (Tab), enter edit mode
            if (e.NavigationMethod == NavigationMethod.Tab && !IsEditing)
            {
                BeginEdit();
            }
        }

        protected override void OnLostFocus(RoutedEventArgs e)
        {
            base.OnLostFocus(e);
            PseudoClasses.Set(PseudoFocus, false);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (IsEditing)
                return;

            // Enter edit mode with keyboard too
            if (e.Key == Key.F2 || e.Key == Key.Enter)
            {
                BeginEdit();
                e.Handled = true;
                return;
            }

            // Optional: arrow keys adjust
            if (e.Key == Key.Up || e.Key == Key.Right)
            {
                SetValueClamped(Value + Step * GetSensitivityMultiplier(e.KeyModifiers));
                e.Handled = true;
            }
            else if (e.Key == Key.Down || e.Key == Key.Left)
            {
                SetValueClamped(Value - Step * GetSensitivityMultiplier(e.KeyModifiers));
                e.Handled = true;
            }
        }

        private void OnDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (IsEditing)
                return;

            BeginEdit();
            e.Handled = true;
        }

        private void BeginEdit()
        {
            IsEditing = true;

            if (_editor == null)
                return;

            _editor.Text = Value.ToString(FormatString, CultureInfo.CurrentCulture);
            _editor.IsVisible = true;
            _editor.Focus();
            _editor.SelectAll();
        }

        private void EndEdit(bool commit)
        {
            if (!IsEditing)
                return;

            if (_editor != null && commit)
            {
                if (TryParseDouble(_editor.Text, out var parsed))
                    SetValueClamped(parsed);
                else
                    _editor.Text = Value.ToString(FormatString, CultureInfo.CurrentCulture);
            }

            IsEditing = false;

            // return focus to control itself
            Focus();
        }

        private void EditorOnLostFocus(object? sender, RoutedEventArgs e) => EndEdit(commit: true);

        private void EditorOnKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsEditing)
                return;

            if (e.Key == Key.Enter)
            {
                EndEdit(commit: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                EndEdit(commit: false);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab)
            {
                // Commit the edit and let Tab navigation proceed naturally
                EndEdit(commit: true);
                // Don't set e.Handled - let the Tab key propagate to move focus
            }
        }

        private void SetValueClamped(double value, bool setBase = true)
        {
            var clamped = Math.Min(Maximum, Math.Max(Minimum, value));

            // If we’re dragging, we want to base changes off dragStartValue,
            // but still clamp and set Value.
            Value = clamped;
        }

        private static bool TryParseDouble(string? text, out double value)
        {
            text ??= "";
            // Allow both current culture and invariant, helpful for "1.5" vs "1,5"
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                   || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static double GetSensitivityMultiplier(KeyModifiers mods)
        {
            // Shift = fine, Ctrl = coarse (common creative-app convention)
            var mult = 1.0;
            if (mods.HasFlag(KeyModifiers.Shift)) mult *= 0.2;
            if (mods.HasFlag(KeyModifiers.Control)) mult *= 5.0;
            return mult;
        }

        private void UpdateCursor(bool isDragging = false)
        {
            // Hide cursor during drag (since it's locked in place), show resize cursor otherwise
            Cursor = isDragging
                ? new Cursor(StandardCursorType.None)
                : new Cursor(StandardCursorType.SizeWestEast);
        }
    }
}
