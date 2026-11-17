namespace EliteLevel.Disc.Core.Safety;

/// <summary>
/// Default extract policy. Protected bytes can still be copied out unchanged when the caller opts in.
/// Nothing here decrypts.
/// </summary>
public static class SafetyPolicy
{
    public static bool IsSystemUpdateName(string name) =>
        name.Equals("$SystemUpdate", StringComparison.OrdinalIgnoreCase);

    public static bool IsProtected(string fullPath)
    {
        var segments = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment.Equals("$SystemUpdate", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("PS3_UPDATE", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("LICDIR", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
