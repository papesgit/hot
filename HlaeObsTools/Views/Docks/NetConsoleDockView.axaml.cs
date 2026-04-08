using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using System.Linq;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class NetConsoleDockView : UserControl
{
    private INotifyCollectionChanged? _logLinesChanged;
    private bool _scrollPending;

    public NetConsoleDockView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_logLinesChanged != null)
        {
            _logLinesChanged.CollectionChanged -= OnLogLinesChanged;
            _logLinesChanged = null;
        }

        if (DataContext is NetConsoleDockViewModel vm)
        {
            _logLinesChanged = vm.LogLines;
            _logLinesChanged.CollectionChanged += OnLogLinesChanged;
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

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Reset)
        {
            RequestScrollToEnd();
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

    private async void OnLogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        if (LogListBox?.SelectedItems is not { Count: > 0 } selected)
            return;

        var text = string.Join(Environment.NewLine, selected.Cast<NetConsoleLogLineViewModel>().Select(line => line.Text));
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;

        await clipboard.SetTextAsync(text);
        e.Handled = true;
    }

    private void RequestScrollToEnd()
    {
        if (_scrollPending)
            return;

        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            ScrollLogToEndCore();
        }, DispatcherPriority.Background);
    }

    private void ScrollLogToEndCore()
    {
        if (LogListBox == null)
            return;

        var scrollViewer = LogListBox.GetVisualDescendants()
                                     .OfType<ScrollViewer>()
                                     .FirstOrDefault();
        if (scrollViewer != null)
        {
            var extent = scrollViewer.Extent;
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, extent.Height);
        }
    }

    private void MoveCaretToEnd()
    {
        if (InputBox == null)
            return;

        var text = InputBox.Text ?? string.Empty;
        InputBox.CaretIndex = text.Length;
    }
}
