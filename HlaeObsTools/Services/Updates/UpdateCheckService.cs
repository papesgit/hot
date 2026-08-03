using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Controls;
using HlaeObsTools.Services.Settings;
using HlaeObsTools.Views;

namespace HlaeObsTools.Services.Updates;

public sealed class UpdateCheckService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/papesgit/hot/releases/latest";
    private const string FallbackReleasePageUrl = "https://github.com/papesgit/hot/releases/latest";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public async Task CheckForUpdatesAsync(Window owner)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var settingsStorage = new SettingsStorage();
            var shouldCheck = false;

            // Update the timestamp through a locked read-modify-write. This service has its
            // own SettingsStorage instance, unlike the main dock factory's shared settings.
            settingsStorage.Update(settings =>
            {
                if (settings.LastUpdateCheckUtc is { } lastCheck && now - lastCheck < CheckInterval)
                    return;

                shouldCheck = true;
                // Store attempts too, so repeated restarts cannot repeatedly hit GitHub during an outage.
                settings.LastUpdateCheckUtc = now;
            });

            if (!shouldCheck)
                return;

            var release = await GetLatestReleaseAsync();
            if (release is null || !TryParseReleaseVersion(release.TagName, out var latestVersion))
                return;

            var currentVersion = GetCurrentVersion();
            if (currentVersion is null || latestVersion <= currentVersion)
                return;

            var settings = settingsStorage.Load();
            if (string.Equals(settings.SkippedUpdateVersion, latestVersion.ToString(), StringComparison.OrdinalIgnoreCase))
                return;

            var response = await UpdateAvailableDialog.ShowAsync(
                owner,
                currentVersion,
                latestVersion,
                string.IsNullOrWhiteSpace(release.HtmlUrl) ? FallbackReleasePageUrl : release.HtmlUrl,
                release.Body);

            if (response == UpdateAvailableDialogResult.SkipVersion)
            {
                settingsStorage.Update(latestSettings =>
                    latestSettings.SkippedUpdateVersion = latestVersion.ToString());
            }
        }
        catch (Exception ex)
        {
            // Update checks must never affect startup, including when offline or rate limited.
            Console.WriteLine($"Update check failed: {ex.Message}");
        }
    }

    private static async Task<GitHubRelease?> GetLatestReleaseAsync()
    {
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
    }
}
