using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;

namespace HlaeObsTools.Services.HotLink;

public sealed record HotLinkPublisherEndpoint(string MachineName, IPAddress Address, int Port)
{
    public string DisplayName => $"{MachineName} - {Address}:{Port}";
}

/// <summary>
/// Advertises and browses HOT Link publishers on the local network.
/// </summary>
public sealed class HotLinkServiceDiscovery : IDisposable
{
    public const string ServiceType = HotLinkProtocol.ServiceType;

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

    public async Task<IReadOnlyList<HotLinkPublisherEndpoint>> BrowseAsync(CancellationToken cancellationToken = default)
    {
        using var browser = new ServiceDiscovery();
        var hosts = new Dictionary<string, HotLinkPublisherEndpoint>(StringComparer.OrdinalIgnoreCase);
        var sync = new object();
        var preferredLanNetworks = GetDefaultGatewayLanNetworks();

        browser.ServiceInstanceDiscovered += (_, e) =>
        {
            var records = e.Message.Answers.Concat(e.Message.AdditionalRecords).ToArray();
            var serviceRecord = records.OfType<SRVRecord>()
                .FirstOrDefault(record => string.Equals(record.Name.ToString(), e.ServiceInstanceName.ToString(), StringComparison.OrdinalIgnoreCase));
            if (serviceRecord == null)
                return;

            // ServiceProfile(addresses: null) advertises every eligible interface. Do not let a VPN,
            // tunnel, or public-facing adapter win merely because its A record arrived first.
            var address = records.OfType<AddressRecord>()
                .Where(record => string.Equals(record.Name.ToString(), serviceRecord.Target.ToString(), StringComparison.OrdinalIgnoreCase)
                    && record.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(record => record.Address)
                .OrderByDescending(address => IsInAnyNetwork(address, preferredLanNetworks))
                .ThenByDescending(IsPrivateLanAddress)
                .ThenBy(address => address.ToString(), StringComparer.Ordinal)
                .FirstOrDefault();
            if (address == null)
                return;

            var machineName = GetDisplayInstanceName(e.ServiceInstanceName.ToString());
            var host = new HotLinkPublisherEndpoint(machineName, address, serviceRecord.Port);
            lock (sync)
            {
                // Records for one host can arrive in separate mDNS responses. Keep one endpoint
                // per advertised host and replace a public address only when a LAN address arrives.
                var hostKey = $"{serviceRecord.Target}:{serviceRecord.Port}";
                if (!hosts.TryGetValue(hostKey, out var existing) ||
                    IsInAnyNetwork(host.Address, preferredLanNetworks) ||
                    (!IsInAnyNetwork(existing.Address, preferredLanNetworks) && IsPrivateLanAddress(host.Address)) ||
                    (!IsPrivateLanAddress(existing.Address) && !IsPrivateLanAddress(host.Address)))
                    hosts[hostKey] = host;
            }
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

        return instanceName;
    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
    }

    private static IReadOnlyList<(uint Address, uint Mask)> GetDefaultGatewayLanNetworks()
    {
        var networks = new List<(uint Address, uint Mask)>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var properties = adapter.GetIPProperties();
            if (!properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(gateway.Address)))
                continue;

            foreach (var unicast in properties.UnicastAddresses.Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && address.IPv4Mask != null))
                networks.Add((ToUInt32(unicast.Address), ToUInt32(unicast.IPv4Mask)));
        }
        return networks;
    }

    private static bool IsInAnyNetwork(IPAddress address, IReadOnlyList<(uint Address, uint Mask)> networks)
    {
        var value = ToUInt32(address);
        return networks.Any(network => (value & network.Mask) == (network.Address & network.Mask));
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
