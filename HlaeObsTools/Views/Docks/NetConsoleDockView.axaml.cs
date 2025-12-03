using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using System.Linq;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class NetConsoleDockView : UserControl
{
    private INotifyPropertyChanged? _vmPropertyChanged;

    public NetConsoleDockView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vmPropertyChanged != null)
        {
            _vmPropertyChanged.PropertyChanged -= OnVmPropertyChanged;
            _vmPropertyChanged = null;
        }

        _vmPropertyChanged = DataContext as INotifyPropertyChanged;
        if (_vmPropertyChanged != null)
        {
            _vmPropertyChanged.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not NetConsoleDockViewModel vm)
            return;

        var inputEmpty = string.IsNullOrWhiteSpace(vm.InputText);
        var historyActive = vm.IsHistoryActive;

        if (e.Key == Key.Up)
        {
            if ((inputEmpty || historyActive) && vm.TryHistoryPrevious())
            {
                MoveCaretToEnd();
                e.Handled = true;
                return;
            }

            if (vm.HasSuggestions)
            {
                vm.MoveSelection(-1);
                MoveCaretToEnd();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Down)
        {
            if ((inputEmpty || historyActive) && vm.TryHistoryNext())
            {
                MoveCaretToEnd();
                e.Handled = true;
                return;
            }

            if (vm.HasSuggestions)
            {
                vm.MoveSelection(1);
                MoveCaretToEnd();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Tab && vm.HasSuggestions)
        {
            vm.AcceptCurrentSuggestion();
            MoveCaretToEnd();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            if (vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NetConsoleDockViewModel.LogText))
        {
            ScrollLogToEnd();
        }
    }

    private void OnSuggestionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems is { Count: > 0 })
        {
            MoveCaretToEnd();
        }
    }

    private void OnSuggestionDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is NetConsoleDockViewModel vm)
        {
            vm.AcceptCurrentSuggestion();
            MoveCaretToEnd();
            InputBox?.Focus();
            e.Handled = true;
        }
    }

    private void ScrollLogToEnd()
    {
        if (LogTextBox == null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (LogTextBox == null)
                return;

            var text = LogTextBox.Text ?? string.Empty;
            LogTextBox.CaretIndex = text.Length;

            var scrollViewer = LogTextBox.GetVisualDescendants()
                                         .OfType<ScrollViewer>()
                                         .FirstOrDefault();
            if (scrollViewer != null)
            {
                var extent = scrollViewer.Extent;
                scrollViewer.Offset = new Vector(extent.Width, extent.Height);
            }
        }, DispatcherPriority.Background);
    }

    private void MoveCaretToEnd()
    {
        if (InputBox == null)
            return;

        var text = InputBox.Text ?? string.Empty;
        InputBox.CaretIndex = text.Length;
    }
}
