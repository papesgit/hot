using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.NetConsole;

namespace HlaeObsTools.ViewModels.Docks;

public class NetConsoleDockViewModel : Tool, IDisposable
{
    private const int MaxLogLines = 500;
    private const int MaxLogFlushBatch = 200;
    private const int MaxSuggestions = 50;

    private readonly ObservableCollection<string> _logLines = new();
    private readonly ObservableCollection<ConsoleCommandInfo> _suggestions = new();
    private readonly Lazy<IReadOnlyList<ConsoleCommandInfo>> _allCommands = new(LoadCommands);
    private readonly DelegateCommand _toggleConnectionCommand;
    private readonly DelegateCommand _sendCommand;
    private readonly List<string> _history = new();
    private readonly ConcurrentQueue<string> _pendingLogLines = new();
    private readonly DispatcherTimer _logFlushTimer;
    private readonly StringBuilder _incomingBuffer = new();
    private readonly object _incomingBufferLock = new();
    private static readonly Regex GameEventStartRegex = new("^Game event \"[^\"]+\", Tick \\d+:$", RegexOptions.Compiled);
    private static readonly Regex GameEventFieldRegex = new("^- \"[^\"]+\" = \".*\"$", RegexOptions.Compiled);

    private Cs2NetConsoleClient? _client;
    private string _currentHost = "127.0.0.1";
    private int _currentPort = 54545;

    private string _hostPortText = "127.0.0.1:54545";
    private string _inputText = string.Empty;
    private string _statusText = "Not connected";
    private bool _isConnected;
    private bool _isConnecting;
    private bool _disposed;
    private int _selectedSuggestionIndex = -1;
    private bool _suppressSuggestionRefresh;
    private int _historyIndex = -1;
    private bool _suppressHistoryReset;
    private bool _filterGameEvents = true;
    private bool _isSkippingGameEventBlock;

    public NetConsoleDockViewModel()
    {
        Title = "Console";
        CanClose = false;
        CanFloat = true;
        CanPin = true;

        _toggleConnectionCommand = new DelegateCommand(_ => ToggleConnectionAsync(), _ => CanToggleConnection);
        _sendCommand = new DelegateCommand(_ => SendAsync(), _ => IsConnected && !IsConnecting);

        _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _logFlushTimer.Tick += (_, _) => FlushPendingLogs();
    }

    public ObservableCollection<string> LogLines => _logLines;
    public ObservableCollection<ConsoleCommandInfo> Suggestions => _suggestions;

    public bool HasSuggestions => _suggestions.Count > 0;

    public string HostPortText
    {
        get => _hostPortText;
        set => SetProperty(ref _hostPortText, value);
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value))
            {
                if (!_suppressSuggestionRefresh)
                {
                    UpdateSuggestions(_inputText);
                }

                if (!string.IsNullOrEmpty(value) && !_suppressHistoryReset)
                {
                    ResetHistoryNavigation();
                }
            }
        }
    }

    public int SelectedSuggestionIndex
    {
        get => _selectedSuggestionIndex;
        set => SetSelectedSuggestion(value, applyToInput: false);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool FilterGameEvents
    {
        get => _filterGameEvents;
        set
        {
            if (SetProperty(ref _filterGameEvents, value) && !value)
            {
                _isSkippingGameEventBlock = false;
            }
        }
    }

    public bool IsHistoryActive => _historyIndex != -1;

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(ConnectionButtonText));
                OnPropertyChanged(nameof(CanToggleConnection));
                _toggleConnectionCommand.RaiseCanExecuteChanged();
                _sendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
            {
                OnPropertyChanged(nameof(ConnectionButtonText));
                OnPropertyChanged(nameof(CanToggleConnection));
                _toggleConnectionCommand.RaiseCanExecuteChanged();
                _sendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ConnectionButtonText => IsConnected ? "Disconnect" : IsConnecting ? "Connecting..." : "Connect";

    public bool CanToggleConnection => !IsConnecting;

    public ICommand ToggleConnectionCommand => _toggleConnectionCommand;

    public ICommand SendCommand => _sendCommand;

    public async Task ToggleConnectionAsync()
    {
        if (IsConnecting)
            return;

        if (IsConnected)
        {
            await DisconnectAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    public async Task ConnectAsync()
    {
        if (IsConnecting || IsConnected)
            return;

        if (!TryParseHostPort(HostPortText, out var host, out var port))
        {
            AppendLog("SYS", $"Invalid address: {HostPortText} (use host:port)");
            return;
        }

        _currentPort = port;
        _currentHost = host;
        IsConnecting = true;
        StatusText = $"Connecting to {host}:{port}...";

        AppendLog("SYS", $"Connecting to {host}:{port}...");

        var client = new Cs2NetConsoleClient(host, port);
        _client = client;

        client.MessageReceived += OnMessageReceived;
        client.Connected += OnClientConnected;
        client.Disconnected += OnClientDisconnected;
        client.Error += OnClientError;

        var success = await client.ConnectSafeAsync();
        if (!success)
        {
            AppendLog("SYS", $"Failed to connect to port {port}");
            StatusText = "Connection failed";
            CleanupClient();
            IsConnecting = false;
            return;
        }

        // OnClientConnected will also set these when the socket is ready
        if (!IsConnected)
        {
            SetConnectedState();
        }
    }

    public async Task DisconnectAsync()
    {
        var client = _client;
        if (client == null)
            return;

        StatusText = "Disconnecting...";
        AppendLog("SYS", "Disconnecting...");

        client.MessageReceived -= OnMessageReceived;
        client.Connected -= OnClientConnected;
        client.Disconnected -= OnClientDisconnected;
        client.Error -= OnClientError;

        FlushIncomingBuffer();

        await client.DisconnectSafeAsync();
        client.Dispose();

        CleanupClient();
        SetDisconnectedState("Disconnected");
    }

    public async Task SendAsync()
    {
        var client = _client;

        if (client == null || !IsConnected)
        {
            AppendLog("SYS", "Not connected to CS2 console");
            return;
        }

        var text = (InputText ?? string.Empty).ReplaceLineEndings(string.Empty);
        if (string.IsNullOrWhiteSpace(text))
            return;

        var commandForHistory = text;

        InputText = string.Empty;
        _suggestions.Clear();
        _selectedSuggestionIndex = -1;
        ResetHistoryNavigation();
        OnPropertyChanged(nameof(HasSuggestions));

        AppendLog("OUT", text);

        var success = await client.SendLineAsync(text);
        if (!success)
        {
            AppendLog("SYS", "Failed to send message");
        }

        AddHistory(commandForHistory);
    }

    public void MoveSelection(int delta)
    {
        if (_suggestions.Count == 0)
            return;

        var newIndex = _selectedSuggestionIndex;
        if (newIndex < 0)
            newIndex = 0;

        newIndex = Math.Clamp(newIndex + delta, 0, _suggestions.Count - 1);
        SetSelectedSuggestion(newIndex, applyToInput: true);
    }

    public void AcceptCurrentSuggestion()
    {
        if (_selectedSuggestionIndex < 0 || _selectedSuggestionIndex >= _suggestions.Count)
            return;

        ApplySelectionToInput(_suggestions[_selectedSuggestionIndex]);
    }

    private void SetSelectedSuggestion(int index, bool applyToInput)
    {
        var clamped = _suggestions.Count == 0 ? -1 : Math.Clamp(index, 0, _suggestions.Count - 1);
        if (SetProperty(ref _selectedSuggestionIndex, clamped))
        {
            if (applyToInput && clamped >= 0 && clamped < _suggestions.Count)
            {
                ApplySelectionToInput(_suggestions[clamped]);
            }
        }
    }

    private void ApplySelectionToInput(ConsoleCommandInfo info)
    {
        _suppressSuggestionRefresh = true;
        InputText = info.Name;
        _suppressSuggestionRefresh = false;
    }

    private void OnClientConnected(object? sender, EventArgs e)
    {
        SetConnectedState();
    }

    private void OnClientDisconnected(object? sender, EventArgs e)
    {
        FlushIncomingBuffer();
        var client = _client;
        CleanupClient();
        client?.Dispose();
        SetDisconnectedState("Connection closed");
    }

    private void OnClientError(object? sender, Exception error)
    {
        AppendLog("ERR", $"Socket error: {error.Message}");
    }

    private void OnMessageReceived(object? sender, string message)
    {
        var normalized = message.Replace("\r\n", "\n", StringComparison.Ordinal)
                                .Replace("\r", "\n", StringComparison.Ordinal);
        var lines = FilterIncomingLines(ExtractCompleteLines(normalized));

        if (lines.Count > 0)
        {
            AppendLogLines("IN", lines);
        }
    }

    private void SetConnectedState()
    {
        RunOnUi(() =>
        {
            IsConnecting = false;
            IsConnected = true;
            StatusText = $"Connected to {_currentHost}:{_currentPort}";
            AppendLog("SYS", "Connected");
        });
    }

    private void SetDisconnectedState(string reason)
    {
        RunOnUi(() =>
        {
            IsConnecting = false;
            IsConnected = false;
            StatusText = reason;
            AppendLog("SYS", reason);
        });
    }

    private void AppendLog(string prefix, string message)
    {
        AppendLogLines(prefix, new[] { message });
    }

    private void AppendLogLines(string prefix, IEnumerable<string> lines)
    {
        var timestamp = DateTime.Now;
        foreach (var line in lines)
        {
            var content = string.IsNullOrWhiteSpace(line) ? "<empty>" : line;
            _pendingLogLines.Enqueue($"[{timestamp:HH:mm:ss}] [{prefix}] {content}");
        }

        StartLogFlushTimer();
    }

    private void UpdateSuggestions(string text)
    {
        var raw = text ?? string.Empty;
        var containsMode = raw.StartsWith(" ", StringComparison.Ordinal);
        var query = containsMode ? raw.TrimStart() : raw.Trim();
        var source = _allCommands.Value;

        List<ConsoleCommandInfo> matches;
        if (string.IsNullOrEmpty(query))
        {
            matches = new List<ConsoleCommandInfo>();
        }
        else
        {
            if (containsMode)
            {
                matches = source
                    .Where(c => c.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(MaxSuggestions)
                    .ToList();
            }
            else
            {
                matches = source
                    .Where(c => c.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                    .Take(MaxSuggestions)
                    .ToList();
            }
        }

        _suggestions.Clear();
        foreach (var m in matches)
        {
            _suggestions.Add(m);
        }

        OnPropertyChanged(nameof(HasSuggestions));
        SetSelectedSuggestion(_suggestions.Count > 0 ? 0 : -1, applyToInput: false);
    }

    private void AddHistory(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        if (_history.Count == 0 || !_history[^1].Equals(command, StringComparison.Ordinal))
        {
            _history.Add(command);
        }
    }

    public bool TryHistoryPrevious()
    {
        if (_history.Count == 0)
            return false;

        if (_historyIndex == -1)
            _historyIndex = _history.Count;

        if (_historyIndex <= 0)
            _historyIndex = 0;
        else
            _historyIndex--;

        ApplyHistory();
        return true;
    }

    public bool TryHistoryNext()
    {
        if (_historyIndex == -1)
            return false;

        if (_historyIndex + 1 >= _history.Count)
        {
            _historyIndex = -1;
            InputText = string.Empty;
            return true;
        }

        _historyIndex++;
        ApplyHistory();
        return true;
    }

    public void ResetHistoryNavigation()
    {
        _historyIndex = -1;
    }

    private void ApplyHistory()
    {
        if (_historyIndex >= 0 && _historyIndex < _history.Count)
        {
            _suppressSuggestionRefresh = true;
            _suppressHistoryReset = true;
            InputText = _history[_historyIndex];
            _suppressHistoryReset = false;
            _suppressSuggestionRefresh = false;
        }
    }

    private void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
        }
    }

    private List<string> ExtractCompleteLines(string text)
    {
        lock (_incomingBufferLock)
        {
            _incomingBuffer.Append(text);
            var combined = _incomingBuffer.ToString();
            var parts = combined.Split('\n');

            _incomingBuffer.Clear();
            var tailIndex = parts.Length - 1;
            if (tailIndex >= 0 && parts[tailIndex].Length > 0)
            {
                _incomingBuffer.Append(parts[tailIndex]);
            }

            var lines = new List<string>(Math.Max(0, parts.Length - 1));
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Length > 0)
                {
                    lines.Add(parts[i]);
                }
            }

            return lines;
        }
    }

    private List<string> FilterIncomingLines(List<string> lines)
    {
        if (!FilterGameEvents || lines.Count == 0)
            return lines;

        var filtered = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (GameEventStartRegex.IsMatch(line))
            {
                _isSkippingGameEventBlock = true;
                continue;
            }

            if (_isSkippingGameEventBlock)
            {
                if (GameEventFieldRegex.IsMatch(line))
                {
                    continue;
                }

                _isSkippingGameEventBlock = false;
            }

            filtered.Add(line);
        }

        return filtered;
    }

    private void FlushIncomingBuffer()
    {
        string? leftover = null;
        lock (_incomingBufferLock)
        {
            if (_incomingBuffer.Length > 0)
            {
                leftover = _incomingBuffer.ToString();
                _incomingBuffer.Clear();
            }
        }

        if (!string.IsNullOrEmpty(leftover))
        {
            AppendLog("IN", leftover);
        }
    }

    private void StartLogFlushTimer()
    {
        if (_logFlushTimer.IsEnabled)
            return;

        RunOnUi(() =>
        {
            if (!_logFlushTimer.IsEnabled)
            {
                _logFlushTimer.Start();
            }
        });
    }

    private void FlushPendingLogs()
    {
        var added = 0;
        while (added < MaxLogFlushBatch && _pendingLogLines.TryDequeue(out var line))
        {
            _logLines.Add(line);
            added++;
        }

        while (_logLines.Count > MaxLogLines)
        {
            _logLines.RemoveAt(0);
        }

        if (_pendingLogLines.IsEmpty)
        {
            _logFlushTimer.Stop();
        }
    }

    private void CleanupClient()
    {
        _client = null;
    }

    private static bool TryParseHostPort(string input, out string host, out int port)
    {
        host = "127.0.0.1";
        port = 0;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        var colonIdx = trimmed.LastIndexOf(':');
        if (colonIdx <= 0 || colonIdx == trimmed.Length - 1)
            return false;

        host = trimmed.Substring(0, colonIdx).Trim();
        var portStr = trimmed[(colonIdx + 1)..].Trim();
        if (!int.TryParse(portStr, out port) || port < 1 || port > 65535)
            return false;

        return !string.IsNullOrWhiteSpace(host);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        var client = _client;
        if (client != null)
        {
            client.MessageReceived -= OnMessageReceived;
            client.Connected -= OnClientConnected;
            client.Disconnected -= OnClientDisconnected;
            client.Error -= OnClientError;
            FlushIncomingBuffer();
            client.DisconnectSafeAsync().Wait();
            client.Dispose();
        }

        _client = null;

        if (_logFlushTimer.IsEnabled)
        {
            RunOnUi(() => _logFlushTimer.Stop());
        }
    }

    private static IReadOnlyList<ConsoleCommandInfo> LoadCommands()
    {
        var list = new List<ConsoleCommandInfo>();
        try
        {
            var uri = new Uri("avares://HlaeObsTools/Assets/cvarlist.md");
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);

            // Skip header rows (2 lines)
            reader.ReadLine();
            reader.ReadLine();

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split('|');
                if (parts.Length == 0)
                    continue;

                var name = parts[0].Trim();
                if (string.IsNullOrEmpty(name))
                    continue;

                var flags = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                var description = parts.Length > 2 ? parts[2].Trim() : string.Empty;

                description = description.Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
                                         .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase)
                                         .Replace("<br />", " ", StringComparison.OrdinalIgnoreCase);

                list.Add(new ConsoleCommandInfo(name, flags, description));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load cvar list: {ex.Message}");
        }

        return list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public class DelegateCommand : ICommand
{
    private readonly Func<object?, Task> _executeAsync;
    private readonly Predicate<object?>? _canExecute;
    private bool _isExecuting;

    public DelegateCommand(Func<object?, Task> executeAsync, Predicate<object?>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (_isExecuting)
            return false;

        return _canExecute?.Invoke(parameter) ?? true;
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        try
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();
            await _executeAsync(parameter);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public record ConsoleCommandInfo(string Name, string Flags, string Description)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string Display => HasDescription ? $"{Name} — {Description}" : Name;
}
