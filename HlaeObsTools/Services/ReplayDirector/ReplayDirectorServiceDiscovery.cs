using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;

namespace HlaeObsTools.Services.ReplayDirector;

public sealed record ReplayDirectorHost(string MachineName, IPAddress Address, int Port)
{
    public string DisplayName => $"{MachineName} - {Address}:{Port}";
}

/// <summary>
/// Advertises and browses the local-link DNS-SD service used by replay directors.
/// </summary>
public sealed class ReplayDirectorServiceDiscovery : IDisposable
{
    public const string ServiceType = "_hlae-replay-director._tcp";

    private readonly ServiceDiscovery _advertiser = new();
    private readonly object _sync = new();
    private ServiceProfile? _advertisedProfile;
    private bool _disposed;

    public void Advertise(int port)
    {
        lock (_sync)
        {
            if (_advertisedProfile != null || _disposed)
                return;

            var instanceName = Environment.MachineName;
            var profile = new ServiceProfile(
                new DomainName(instanceName),
                new DomainName(ServiceType),
                checked((ushort)port),
                addresses: null,
                sharedProfile: false);
            _advertiser.Advertise(profile);
            _advertiser.Announce(profile);
            _advertisedProfile = profile;
        }
    }

    public void StopAdvertising()
    {
        lock (_sync)
        {
            if (_advertisedProfile == null)
                return;

            _advertiser.Unadvertise(_advertisedProfile);
            _advertisedProfile = null;
        }
    }

    public async Task<IReadOnlyList<ReplayDirectorHost>> BrowseAsync(CancellationToken cancellationToken = default)
    {
        using var browser = new ServiceDiscovery();
        var hosts = new Dictionary<string, ReplayDirectorHost>(StringComparer.OrdinalIgnoreCase);
        var sync = new object();

        browser.ServiceInstanceDiscovered += (_, e) =>
        {
            var records = e.Message.Answers.Concat(e.Message.AdditionalRecords).ToArray();
            var serviceRecord = records.OfType<SRVRecord>()
                .FirstOrDefault(record => string.Equals(record.Name.ToString(), e.ServiceInstanceName.ToString(), StringComparison.OrdinalIgnoreCase));
            if (serviceRecord == null)
                return;

            var address = records.OfType<AddressRecord>()
                .FirstOrDefault(record => string.Equals(record.Name.ToString(), serviceRecord.Target.ToString(), StringComparison.OrdinalIgnoreCase)
                    && record.Address.AddressFamily == AddressFamily.InterNetwork)
                ?.Address;
            if (address == null)
                return;

            var machineName = GetDisplayInstanceName(e.ServiceInstanceName.ToString());
            var host = new ReplayDirectorHost(machineName, address, serviceRecord.Port);
            lock (sync)
                hosts[$"{address}:{serviceRecord.Port}"] = host;
        };

        browser.QueryServiceInstances(new DomainName(ServiceType));
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

        lock (sync)
            return hosts.Values.OrderBy(host => host.MachineName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopAdvertising();
        _advertiser.Dispose();
    }

    private static string GetDisplayInstanceName(string fullyQualifiedName)
    {
        var instanceName = fullyQualifiedName.Split('.', 2)[0];
        instanceName = Regex.Replace(instanceName, @"\\(\d{3})", match => ((char)int.Parse(match.Groups[1].Value)).ToString());

        // Older HlaeObsTools advertisements included the service port in the instance name.
        return Regex.Replace(instanceName, @"\s+\(\d+\)$", string.Empty);
    }
}
