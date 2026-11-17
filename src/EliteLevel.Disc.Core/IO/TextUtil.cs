using System.Globalization;
using System.Text;

namespace EliteLevel.Disc.Core.IO;

public static class ByteSize
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        if (unit == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }
}

public static class TextFormat
{
    public static string MiddleEllipsis(string? value, int head = 8, int tail = 8)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= head + tail + 1)
        {
            return value ?? string.Empty;
        }

        return string.Concat(value.AsSpan(0, head), "…", value.AsSpan(value.Length - tail));
    }

    public static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}

public static class Glob
{
    public static bool IsMatch(string text, string pattern)
    {
        return Match(text.AsSpan(), pattern.AsSpan());
    }

    private static bool Match(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        var t = 0;
        var p = 0;
        var star = -1;
        var mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                t++;
                p++;
                continue;
            }

            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
                continue;
            }

            if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
                continue;
            }

            return false;
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}

internal static class VirtualPath
{
    public static string Combine(string parent, string name)
    {
        if (string.IsNullOrEmpty(parent) || parent == "/")
        {
            return "/" + name;
        }

        return parent + "/" + name;
    }

    public static string ToRelative(string fullPath)
    {
        var parts = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part is "." or ".." || part.IndexOfAny(['\\', ':']) >= 0)
            {
                throw new InvalidOperationException("The image contains a path that cannot be extracted safely.");
            }
        }

        return parts.Length == 0 ? string.Empty : Path.Combine(parts);
    }

    public static bool IsInside(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class FileKinds
{
    public static FileKind FromName(string name, bool directory)
    {
        if (directory)
        {
            return FileKind.Directory;
        }

        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".dds" => FileKind.Image,
            ".txt" or ".xml" or ".json" or ".ini" or ".log" or ".csv" or ".cfg" => FileKind.Text,
            ".sfo" => FileKind.ParamSfo,
            ".wav" or ".ogg" => FileKind.Audio,
            "" => FileKind.Unknown,
            _ => FileKind.Binary
        };
    }
}

internal static class Hex
{
    public static string Format(byte[] data)
    {
        var builder = new StringBuilder(data.Length * 2);
        foreach (var b in data)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
