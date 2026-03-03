namespace HlaeObsTools.ViewModels;

public sealed class VmixSettings : ViewModelBase
{
    private string _host = "127.0.0.1";
    private int _port = 8088;

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value ?? string.Empty);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }
}
