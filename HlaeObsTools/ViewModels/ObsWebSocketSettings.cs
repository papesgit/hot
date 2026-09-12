namespace HlaeObsTools.ViewModels;

/// <summary>Connection settings for OBS's built-in obs-websocket server.</summary>
public sealed class ObsWebSocketSettings : ViewModelBase
{
    private string _host = "127.0.0.1";
    private int _port = 4455;
    private string _password = string.Empty;
    private string _connectionStatus = "Disconnected";
    public string Host { get => _host; set => SetProperty(ref _host, value ?? string.Empty); }
    public int Port { get => _port; set => SetProperty(ref _port, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value ?? string.Empty); }
    public string ConnectionStatus { get => _connectionStatus; set => SetProperty(ref _connectionStatus, value ?? "Disconnected"); }
}
