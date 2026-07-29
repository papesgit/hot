using System;
using System.Diagnostics;

namespace HlaeObsTools.Views;

internal static class ExternalLinkLauncher
{
    public static bool TryOpen(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to open external link '{url}': {ex.Message}");
            return false;
        }
    }
}
