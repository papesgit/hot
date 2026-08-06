using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using HlaeObsTools.Services.Settings;
using HlaeObsTools.Views;

namespace HlaeObsTools.Services.Updates;

public sealed class UpdateCheckService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/papesgit/hot/releases/latest";
    private const string FallbackReleasePageUrl = "https://github.com/papesgit/hot/releases/latest";
    private const string ReleaseFixtureEnvironmentVariable = "HOT_UPDATE_RELEASE_FIXTURE";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex HlaeAssetNamePattern = new(
        @"^HLAEv(?<hlae>\d+\.\d+\.\d+)(?:-r(?<revision>[1-9]\d*))?-HOTv(?<hot>\d+\.\d+\.\d+)\.zip$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly SettingsStorage _settingsStorage = new();
    private GitHubRelease? _latestRelease;
    private Window? _owner;
    private bool _isShowingDialog;

    public async Task CheckForUpdatesAsync(Window owner)
    {
        _owner = owner;

        try
        {
            var now = DateTimeOffset.UtcNow;
            var shouldCheck = false;
            var usingFixture = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(ReleaseFixtureEnvironmentVariable));

            _settingsStorage.Update(settings =>
            {
                if (!usingFixture && settings.LastUpdateCheckUtc is { } lastCheck && now - lastCheck < CheckInterval)
                    return;

                shouldCheck = true;
                settings.LastUpdateCheckUtc = now;
            });

            if (!shouldCheck)
                return;

            _latestRelease = await GetLatestReleaseAsync();
            if (_latestRelease is null)
                return;

            await EvaluateUpdatesAsync();
        }
        catch (Exception ex)
        {
            // Update checks must never affect startup, including when offline or rate limited.
            Console.WriteLine($"Update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Stores the HLAE build reported by the active WebSocket connection and re-evaluates
    /// the already-fetched release data. This makes a startup warning definitive once HLAE connects.
    /// </summary>
    public void ReportConnectedHlaeVersion(Version hlaeVersion, Version hotVersion, int revision)
    {
        _settingsStorage.Update(settings =>
        {
            settings.LastConnectedHlaeVersion = hlaeVersion.ToString();
            settings.LastConnectedHlaeHotVersion = hotVersion.ToString();
            settings.LastConnectedHlaeRevision = revision;
        });

        if (_latestRelease != null && _owner != null)
            _ = Dispatcher.UIThread.InvokeAsync(EvaluateUpdatesAsync);
    }

    private async Task EvaluateUpdatesAsync()
    {
        if (_latestRelease is null || _owner is null || _isShowingDialog)
            return;

        var currentHotVersion = GetCurrentVersion();
        if (currentHotVersion is null)
            return;

        if (TryParseReleaseVersion(_latestRelease.TagName, out var latestHotVersion)
            && latestHotVersion > currentHotVersion)
        {
            var settings = _settingsStorage.Load();
            if (!string.Equals(settings.SkippedUpdateVersion, latestHotVersion.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                await ShowHotUpdateAsync(currentHotVersion, latestHotVersion);
            }

            // A HOT release contains its matching HLAE package; HLAE-only checks apply only
            // once this HOT version is already current.
            return;
        }

        var latestHlaeAsset = FindLatestHlaeAsset(_latestRelease, currentHotVersion);
        if (latestHlaeAsset is null)
            return;

        var storedSettings = _settingsStorage.Load();
        if (!TryGetStoredHlaeVersion(storedSettings, out var installedHlaeVersion, out var installedHotVersion, out var installedRevision)
            || !IsHlaeUpdateAvailable(installedHlaeVersion, installedHotVersion, installedRevision, latestHlaeAsset, currentHotVersion)
            || string.Equals(storedSettings.SkippedHlaeUpdateVersion, latestHlaeAsset.Identity, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await ShowHlaeUpdateAsync(installedHlaeVersion, installedRevision, latestHlaeAsset);
    }

    private async Task ShowHotUpdateAsync(Version currentVersion, Version latestVersion)
    {
        _isShowingDialog = true;
        try
        {
            var response = await UpdateAvailableDialog.ShowAsync(
                _owner!,
                "HLAE Observer Tools",
                currentVersion,
                latestVersion,
                string.IsNullOrWhiteSpace(_latestRelease!.HtmlUrl) ? FallbackReleasePageUrl : _latestRelease.HtmlUrl,
                _latestRelease.Body);

            if (response == UpdateAvailableDialogResult.SkipVersion)
                _settingsStorage.Update(settings => settings.SkippedUpdateVersion = latestVersion.ToString());
        }
        finally
        {
            _isShowingDialog = false;
        }
    }

    private async Task ShowHlaeUpdateAsync(Version currentHlaeVersion, int currentRevision, HlaeAsset latestAsset)
    {
        _isShowingDialog = true;
        try
        {
            var response = await UpdateAvailableDialog.ShowAsync(
                _owner!,
                latestAsset.Revision == 1
                    ? $"HLAE (compatible with HOT {latestAsset.HotVersion})"
                    : $"HLAE revision {latestAsset.Revision} (compatible with HOT {latestAsset.HotVersion})",
                currentHlaeVersion,
                latestAsset.HlaeVersion,
                latestAsset.DownloadUrl,
                _latestRelease!.Body,
                "Download HLAE",
                $"HLAE {FormatHlaePackageVersion(latestAsset.HlaeVersion, latestAsset.Revision)} is available for your HOT version.\n"
                + $"You are currently using HLAE {FormatHlaePackageVersion(currentHlaeVersion, currentRevision)}.",
                showReleaseNotes: false);

            if (response == UpdateAvailableDialogResult.SkipVersion)
                _settingsStorage.Update(settings => settings.SkippedHlaeUpdateVersion = latestAsset.Identity);
        }
        finally
        {
            _isShowingDialog = false;
        }
    }

    private static bool IsHlaeUpdateAvailable(
        Version installedHlaeVersion,
        Version installedHotVersion,
        int installedRevision,
        HlaeAsset latestAsset,
        Version currentHotVersion)
    {
        return installedHotVersion < currentHotVersion
            || installedHlaeVersion < latestAsset.HlaeVersion
            || (installedHlaeVersion == latestAsset.HlaeVersion && installedRevision < latestAsset.Revision);
    }

    private static string FormatHlaePackageVersion(Version hlaeVersion, int revision)
    {
        return revision > 1 ? $"{hlaeVersion}-r{revision}" : hlaeVersion.ToString();
    }

    private static bool TryGetStoredHlaeVersion(AppSettingsData settings, out Version hlaeVersion, out Version hotVersion, out int revision)
    {
        var hasHlaeVersion = Version.TryParse(settings.LastConnectedHlaeVersion, out hlaeVersion!);
        var hasHotVersion = Version.TryParse(settings.LastConnectedHlaeHotVersion, out hotVersion!);
        revision = settings.LastConnectedHlaeRevision ?? 1;
        return hasHlaeVersion && hasHotVersion && revision >= 1;
    }

    private static HlaeAsset? FindLatestHlaeAsset(GitHubRelease release, Version currentHotVersion)
    {
        return release.Assets
            .Select(TryParseHlaeAsset)
            .Where(asset => asset is not null && asset.HotVersion == currentHotVersion)
            .Cast<HlaeAsset>()
            .OrderByDescending(asset => asset.HlaeVersion)
            .ThenByDescending(asset => asset.Revision)
            .FirstOrDefault();
    }

    private static HlaeAsset? TryParseHlaeAsset(GitHubAsset asset)
    {
        var match = HlaeAssetNamePattern.Match(asset.Name ?? string.Empty);
        if (!match.Success
            || !Version.TryParse(match.Groups["hlae"].Value, out var hlaeVersion)
            || !Version.TryParse(match.Groups["hot"].Value, out var hotVersion)
            || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            return null;
        }

        var revision = match.Groups["revision"].Success
            ? int.Parse(match.Groups["revision"].Value)
            : 1;
        return new HlaeAsset(hlaeVersion, hotVersion, revision, asset.BrowserDownloadUrl);
    }

    private static async Task<GitHubRelease?> GetLatestReleaseAsync()
    {
        var fixturePath = Environment.GetEnvironmentVariable(ReleaseFixtureEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fixturePath))
        {
            Console.WriteLine($"Using update-release fixture from '{fixturePath}'.");
            await using var fixture = File.OpenRead(fixturePath);
            return await JsonSerializer.DeserializeAsync<GitHubRelease>(fixture).ConfigureAwait(false);
        }

        using var client = new HttpClient { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HLAE-Observer-Tools-Update-Check");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var response = await client.GetAsync(LatestReleaseUrl).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(content).ConfigureAwait(false);
    }

    private static Version? GetCurrentVersion()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return Version.TryParse(informationalVersion?.Split('+')[0], out var version) ? version : null;
    }

    private static bool TryParseReleaseVersion(string? tagName, out Version version)
    {
        var value = tagName?.Trim();
        if (value?.StartsWith("v", StringComparison.OrdinalIgnoreCase) == true)
            value = value[1..];

        return Version.TryParse(value, out version!);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("assets")]
        public GitHubAsset[] Assets { get; init; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }
    }

    private sealed record HlaeAsset(Version HlaeVersion, Version HotVersion, int Revision, string DownloadUrl)
    {
        public string Identity => $"{HlaeVersion}-r{Revision}-HOT{HotVersion}";
    }
}
