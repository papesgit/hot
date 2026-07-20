using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
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
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    public async Task<bool> ExecuteFunctionAsync(string function, string? value, CancellationToken token, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(function))
            return false;

        var uri = BuildFunctionUri(function, value);
        return await SendAsync(uri, token, label).ConfigureAwait(false);
    }

    public async Task<bool> ExecuteFunctionAsync(VmixFunctionCall call, CancellationToken token, string? label = null)
    {
        if (call == null || string.IsNullOrWhiteSpace(call.Function))
            return false;

        var uri = BuildFunctionUri(call);
        return await SendAsync(uri, token, label ?? call.Function).ConfigureAwait(false);
    }

    public async Task<VmixStateSnapshot?> FetchStateAsync(CancellationToken token)
    {
        var uri = BuildStateUri();
        try
        {
            using var response = await _httpClient.GetAsync(uri, token).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();
            var xml = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return ParseStateXml(xml);
        }
        catch
        {
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"[VMIX] Failed to fetch state: {uri}");
            }

            return null;
        }
    }

    private async Task<bool> SendAsync(Uri uri, CancellationToken token, string? label = null)
    {
        try
        {
            using var response = await _httpClient.GetAsync(uri, token).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A superseded marker must stop immediately; it must never create a stale dock record.
            throw;
        }
        catch
        {
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"[VMIX] Request failed: {(label ?? uri.ToString())}");
            }

            return false;
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

    private Uri BuildFunctionUri(VmixFunctionCall call)
    {
        var host = string.IsNullOrWhiteSpace(_settings.Host) ? "127.0.0.1" : _settings.Host;
        var port = _settings.Port <= 0 ? 8088 : _settings.Port;

        var query = new List<string> { $"Function={Uri.EscapeDataString(call.Function)}" };
        AddIfNotEmpty(query, "Value", call.Value);
        if (call.Input != null)
            AddIfNotEmpty(query, "Input", call.Input.Value.ToString(CultureInfo.InvariantCulture));
        AddIfNotEmpty(query, "Channel", call.Channel);
        AddIfNotEmpty(query, "Duration", call.Duration);
        AppendExtraQuery(query, call.ExtraQuery);

        return new Uri($"http://{host}:{port}/api/?{string.Join("&", query)}");
    }

    private Uri BuildStateUri()
    {
        var host = string.IsNullOrWhiteSpace(_settings.Host) ? "127.0.0.1" : _settings.Host;
        var port = _settings.Port <= 0 ? 8088 : _settings.Port;
        return new Uri($"http://{host}:{port}/api");
    }

    private static void AddIfNotEmpty(List<string> query, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            query.Add($"{key}={Uri.EscapeDataString(value)}");
    }

    private static void AppendExtraQuery(List<string> query, string? extraQuery)
    {
        if (string.IsNullOrWhiteSpace(extraQuery))
            return;

        foreach (var segment in extraQuery.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = segment.IndexOf('=');
            if (idx <= 0 || idx >= segment.Length - 1)
            {
                Console.WriteLine($"[VMIX] Ignoring malformed extra query segment: {segment}");
                continue;
            }

            var key = segment[..idx].Trim();
            var value = segment[(idx + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            query.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }
    }

    private static VmixStateSnapshot? ParseStateXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root == null)
                return null;

            var inputs = root.Element("inputs")?
                .Elements("input")
                .Select(e =>
                {
                    var number = int.TryParse((string?)e.Attribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
                    return new VmixInputInfo
                    {
                        Number = number,
                        Title = ((string?)e.Attribute("title")) ?? string.Empty,
                        Key = ((string?)e.Attribute("key")) ?? string.Empty,
                        Type = ((string?)e.Attribute("type")) ?? string.Empty
                    };
                })
                .Where(i => i.Number > 0)
                .OrderBy(i => i.Number)
                .ToList() ?? new List<VmixInputInfo>();

            var transitions = root.Element("transitions")?
                .Elements("transition")
                .Select(t => ((string?)t.Attribute("effect")) ?? string.Empty)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToList() ?? new List<string>();

            var replay = root.Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "replay", StringComparison.OrdinalIgnoreCase));

            return new VmixStateSnapshot
            {
                Inputs = inputs,
                Transitions = transitions,
                Active = root.Element("active")?.Value,
                Preview = root.Element("preview")?.Value,
                ReplayEventsA = ReadIntAttribute(replay, "eventsA"),
                ReplayEventsB = ReadIntAttribute(replay, "eventsB"),
                ReplayEventsTotal = ReadIntAttribute(replay, "events"),
                ReplayChannelMode = ReadStringAttribute(replay, "channelMode"),
                ReplayCameraA = ReadIntAttribute(replay, "cameraA"),
                ReplayCameraB = ReadIntAttribute(replay, "cameraB")
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VMIX] Failed to parse state XML: {ex.Message}");
            return null;
        }
    }

    private static int ReadIntAttribute(XElement? element, string name)
    {
        if (element == null)
            return 0;

        var value = ReadStringAttribute(element, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static string? ReadStringAttribute(XElement? element, string name)
    {
        return element?.Attributes()
            .FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _httpClient.Dispose();
    }
}
