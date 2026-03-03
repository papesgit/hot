using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Services.Vmix;

public sealed class VmixApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly VmixSettings _settings;
    private bool _disposed;

    public VmixApiClient(VmixSettings settings)
    {
        _settings = settings;
        _httpClient = new HttpClient();
    }

    public async Task ExecuteFunctionAsync(string function, string? value, CancellationToken token, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(function))
            return;

        var uri = BuildFunctionUri(function, value);
        try
        {
            using var response = await _httpClient.GetAsync(uri, token).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();
        }
        catch
        {
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"[VMIX] Request failed: {(label ?? uri.ToString())}");
            }
        }
    }

    private Uri BuildFunctionUri(string function, string? value)
    {
        var host = string.IsNullOrWhiteSpace(_settings.Host) ? "127.0.0.1" : _settings.Host;
        var port = _settings.Port <= 0 ? 8088 : _settings.Port;

        var uri = $"http://{host}:{port}/api/?Function={function}";
        if (!string.IsNullOrWhiteSpace(value))
        {
            uri += $"&Value={Uri.EscapeDataString(value)}";
        }

        return new Uri(uri);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _httpClient.Dispose();
    }
}
