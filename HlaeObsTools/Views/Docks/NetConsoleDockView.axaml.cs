using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HlaeObsTools.Services.Input;
using Avalonia;
using System.Linq;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class NetConsoleDockView : UserControl
{
    private INotifyCollectionChanged? _logLinesChanged;
    private NetConsoleDockViewModel? _logViewModel;
    private bool _scrollPending;
    private bool _logLinesAttached;
    private bool _logViewportWasVisible;
    private bool _isFollowingLog = true;
    private readonly Dictionary<NetConsoleLogLineViewModel, double> _logLineHeights = new();
    private bool _logLineHeightCacheDirty = true;
    private double _lastLogViewportWidth = double.NaN;

    public NetConsoleDockView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) =>
        {
            AttachLogLines();
            _logViewportWasVisible = false;
            RequestScrollToEnd();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            DetachLogLines();
            _logViewportWasVisible = false;
        };
        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachLogLines();
        _logLinesChanged = null;
        _logViewModel = null;
        _logLineHeights.Clear();

        if (DataContext is NetConsoleDockViewModel vm)
        {
            _logLinesChanged = vm.LogLines;
            _logViewModel = vm;
        }

        if (this.IsAttachedToVisualTree())
            AttachLogLines();
    }

    private void AttachLogLines()
    {
        if (_logLinesChanged == null || _logLinesAttached)
            return;
        _logLinesChanged.CollectionChanged += OnLogLinesChanged;
        _logViewModel?.LogLinesTrimming += OnLogLinesTrimming;
        _logLineHeightCacheDirty = true;
        _logLinesAttached = true;
    }

    private void DetachLogLines()
    {
        if (_logLinesChanged == null || !_logLinesAttached)
            return;
        _logLinesChanged.CollectionChanged -= OnLogLinesChanged;
        _logViewModel?.LogLinesTrimming -= OnLogLinesTrimming;
        _logLinesAttached = false;
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
                ScrollLogToEnd();
                e.Handled = true;
            }
        }
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
        {
            foreach (var line in e.OldItems.OfType<NetConsoleLogLineViewModel>())
                _logLineHeights.Remove(line);
        }

        if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Reset)
        {
            _logLineHeightCacheDirty = true;
            if (_isFollowingLog)
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

        var text = string.Join(Environment.NewLine, selected.Cast<NetConsoleLogLineViewModel>().Select(line => line.Message));
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;

        await clipboard.SetTextAsync(text);
        e.Handled = true;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        var scrollViewer = LogListBox?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var isVisible = scrollViewer is { Viewport.Height: > 0, Bounds.Height: > 0 };

        if (isVisible && !_logViewportWasVisible)
        {
            RequestScrollToEnd();
        }

        _logViewportWasVisible = isVisible;
        if (scrollViewer != null)
        {
            if (_logLineHeightCacheDirty || Math.Abs(scrollViewer.Viewport.Width - _lastLogViewportWidth) > 0.5)
            {
                CacheLogLineHeights();
                _logLineHeightCacheDirty = false;
                _lastLogViewportWidth = scrollViewer.Viewport.Width;
            }
            UpdateLogFollowState(scrollViewer);
        }
    }

    private void OnSendClick(object? sender, RoutedEventArgs e) => ScrollLogToEnd();

    private void OnScrollToBottomClick(object? sender, RoutedEventArgs e) => ScrollLogToEnd();

    private void OnLogLinesTrimming(object? sender, int linesToTrim)
    {
        if (_isFollowingLog || LogListBox == null || sender is not NetConsoleDockViewModel vm)
            return;

        var scrollViewer = LogListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer == null)
            return;

        var linesToRemove = vm.LogLines.Take(linesToTrim).ToList();
        var removedHeight = linesToRemove.All(_logLineHeights.ContainsKey)
            ? linesToRemove.Sum(line => _logLineHeights[line])
            : LogListBox.GetVisualDescendants()
                        .OfType<ListBoxItem>()
                        .OrderBy(item => item.TranslatePoint(default, scrollViewer)?.Y ?? double.MaxValue)
                        .Take(linesToTrim)
                        .Sum(item => item.Bounds.Height);
        var offset = scrollViewer.Offset;
        scrollViewer.Offset = new Vector(offset.X, Math.Max(0, offset.Y - removedHeight));
    }

    private void CacheLogLineHeights()
    {
        if (LogListBox == null)
            return;

        foreach (var item in LogListBox.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (item.DataContext is NetConsoleLogLineViewModel line && item.Bounds.Height > 0)
                _logLineHeights[line] = item.Bounds.Height;
        }
    }

    private async void OnFiltersClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NetConsoleDockViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
            return;

        await KeyboardInputGate.RunSuppressedAsync(async () =>
        {
            await new NetConsoleFiltersWindow(vm).ShowDialog(owner);
            return true;
        });
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

    private void ScrollLogToEnd()
    {
        _isFollowingLog = true;
        ScrollToBottomButton.IsVisible = false;
        RequestScrollToEnd();
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

    private void UpdateLogFollowState(ScrollViewer scrollViewer)
    {
        const double bottomTolerance = 1;
        var distanceFromBottom = scrollViewer.Extent.Height - scrollViewer.Viewport.Height - scrollViewer.Offset.Y;
        _isFollowingLog = distanceFromBottom <= bottomTolerance;
        ScrollToBottomButton.IsVisible = !_isFollowingLog;
    }

    private void MoveCaretToEnd()
    {
        if (InputBox == null)
            return;

        var text = InputBox.Text ?? string.Empty;
        InputBox.CaretIndex = text.Length;
    }
}
