using System.Runtime.InteropServices;

namespace HlaeObsTools.Services.Gsi;

public sealed record PlayerFilterRule
{
    public string Name { get; init; } = string.Empty;
    public string Match { get; init; } = "exact";

    // Names are literal: preserve spaces, commas, pipes and other punctuation.
    public unsafe bool Matches(string name)
    {
        if (string.IsNullOrEmpty(Name) || Name.Length > name.Length) return false;
        if (Match != "contains" && Name.Length != name.Length) return false;
        // Use the same Unicode ordinal comparison as the Windows HLAE fork.
        // .NET OrdinalIgnoreCase differs for some characters (for example final sigma).
        fixed (char* candidate = name)
        fixed (char* pattern = Name)
        {
            var lastOffset = Match == "contains" ? name.Length - Name.Length : 0;
            for (var offset = 0; offset <= lastOffset; offset++)
                if (CompareStringOrdinal(candidate + offset, Name.Length, pattern, Name.Length, true) == 2)
                    return true;
        }
        return false;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern unsafe int CompareStringOrdinal(char* first, int firstLength,
        char* second, int secondLength, [MarshalAs(UnmanagedType.Bool)] bool ignoreCase);
}
