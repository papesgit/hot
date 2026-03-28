using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using Dock.Model.Mvvm.Controls;

namespace HlaeObsTools.ViewModels.Docks;

public sealed class AttachPresetAnimationDockViewModel : Tool
{
    private AttachPresetViewModel? _preset;

    public AttachPresetViewModel? Preset
    {
        get => _preset;
        private set
        {
            if (ReferenceEquals(_preset, value)) return;
            UnhookPreset(_preset);
            _preset = value;
            HookPreset(_preset);
            Title = _preset != null ? $"Animation - {_preset.Title}" : "Animation";
            OnPropertyChanged(nameof(Preset));
            OnPropertyChanged(nameof(HasPreset));
            OnPropertyChanged(nameof(HasTransition));
        }
    }

    public bool HasPreset => Preset != null;

    public bool HasTransition => Preset?.AnimationEvents.Any(e => e.IsTransition) ?? false;

    public ICommand AddKeyframeCommand { get; }
    public ICommand AddTransitionCommand { get; }
    public ICommand DeleteEventCommand { get; }

    public AttachPresetAnimationDockViewModel()
    {
        Title = "Animation";
        AddKeyframeCommand = new Relay(_ => AddKeyframe(), _ => HasPreset);
        AddTransitionCommand = new Relay(_ => AddTransition(), _ => HasPreset && !HasTransition);
        DeleteEventCommand = new Relay(o => DeleteEvent(o as AttachPresetAnimationEventViewModel), o => CanDelete(o as AttachPresetAnimationEventViewModel));
    }

    public void OpenPreset(AttachPresetViewModel preset)
    {
        preset.EnsureBaseKeyframe();
        Preset = preset;
    }

    private void AddKeyframe()
    {
        if (Preset == null) return;

        Preset.AnimationEnabled = true;

        var time = Preset.AnimationEvents.Count > 0
            ? Math.Max(0.0, Preset.AnimationEvents.Max(e => e.Time))
            : 0.0;
        time += 1.0;

        var order = NextOrderAtTime(time);
        var vm = new AttachPresetAnimationEventViewModel
        {
            Type = AttachPresetAnimationEventType.Keyframe,
            Time = time,
            Order = order
        };
        InsertEvent(vm);
        RefreshTransitionState();
    }

    private void AddTransition()
    {
        if (Preset == null) return;
        if (HasTransition) return;

        Preset.AnimationEnabled = true;

        var time = Preset.AnimationEvents.Count > 0
            ? Math.Max(0.0, Preset.AnimationEvents.Max(e => e.Time))
            : 0.0;

        var order = NextOrderAtTime(time);
        var vm = new AttachPresetAnimationEventViewModel
        {
            Type = AttachPresetAnimationEventType.Transition,
            Time = time,
            Order = order,
            TransitionDuration = 0.0,
            TransitionEasing = HudSettings.AttachmentPresetAnimationTransitionEasing.Smoothstep
        };
        InsertEvent(vm);
        RefreshTransitionState();
    }

    private int NextOrderAtTime(double time)
    {
        if (Preset == null) return 0;
        var max = Preset.AnimationEvents
            .Where(e => Math.Abs(e.Time - time) < 0.0001)
            .Select(e => e.Order)
            .DefaultIfEmpty(-1)
            .Max();
        return max + 1;
    }

    private void InsertEvent(AttachPresetAnimationEventViewModel vm)
    {
        if (Preset == null)
            return;

        var insertIndex = Preset.AnimationEvents.Count;
        for (var i = 0; i < Preset.AnimationEvents.Count; i++)
        {
            var current = Preset.AnimationEvents[i];
            if (current.IsBaseKeyframe)
                continue;

            if (current.Time > vm.Time)
            {
                insertIndex = i;
                break;
            }

            if (Math.Abs(current.Time - vm.Time) < 0.0001 && vm.IsTransition && current.IsKeyframe)
            {
                insertIndex = i;
                break;
            }
        }

        Preset.AnimationEvents.Insert(insertIndex, vm);
    }

    private static bool CanDelete(AttachPresetAnimationEventViewModel? e)
    {
        return e != null && !e.IsBaseKeyframe;
    }

    private void DeleteEvent(AttachPresetAnimationEventViewModel? e)
    {
        if (Preset == null || e == null) return;
        if (!CanDelete(e)) return;
        Preset.AnimationEvents.Remove(e);
        RefreshTransitionState();
    }

    private void RefreshTransitionState()
    {
        OnPropertyChanged(nameof(HasTransition));
        (AddTransitionCommand as Relay)?.RaiseCanExecuteChanged();
    }

    private void HookPreset(AttachPresetViewModel? preset)
    {
        if (preset == null) return;

        preset.PropertyChanged += OnPresetChanged;
        preset.AnimationEvents.CollectionChanged += OnAnimationEventsChanged;
        foreach (var e in preset.AnimationEvents)
        {
            e.PropertyChanged += OnEventChanged;
        }
    }

    private void UnhookPreset(AttachPresetViewModel? preset)
    {
        if (preset == null) return;

        preset.PropertyChanged -= OnPresetChanged;
        preset.AnimationEvents.CollectionChanged -= OnAnimationEventsChanged;
        foreach (var e in preset.AnimationEvents)
        {
            e.PropertyChanged -= OnEventChanged;
        }
    }

    private void OnPresetChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AttachPresetViewModel.AnimationEnabled))
        {
            // Keep command states up-to-date.
            (AddKeyframeCommand as Relay)?.RaiseCanExecuteChanged();
            (AddTransitionCommand as Relay)?.RaiseCanExecuteChanged();
        }
    }

    private void OnAnimationEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<AttachPresetAnimationEventViewModel>())
            {
                item.PropertyChanged -= OnEventChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<AttachPresetAnimationEventViewModel>())
            {
                item.PropertyChanged += OnEventChanged;
            }
        }

        RefreshTransitionState();
    }

    private void OnEventChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        RefreshTransitionState();
    }

    private sealed class Relay : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public Relay(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
