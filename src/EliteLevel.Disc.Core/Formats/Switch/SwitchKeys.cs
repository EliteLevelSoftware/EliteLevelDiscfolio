using System.Globalization;
using System.Text;

namespace EliteLevel.Disc.Core.Formats.Switch;

/// <summary>
/// Reads a user-supplied prod.keys and, when it sits beside it, title.keys.
/// Lines are <c>name = hex</c>. Title key lines are <c>rights-id = hex</c>.
/// The hex is the caller's own console material. Nothing in this file is a key.
/// </summary>
internal sealed class SwitchKeys
{
    public const int MaxBytes = 2 * 1024 * 1024;

    private readonly Dictionary<string, byte[]> _values;
    private readonly Dictionary<string, byte[]> _titleKeys;

    private SwitchKeys(Dictionary<string, byte[]> values, Dictionary<string, byte[]> titleKeys, string? problem, bool requested)
    {
        _values = values;
        _titleKeys = titleKeys;
        Problem = problem;
        Requested = requested;
    }

    public static SwitchKeys None { get; } = new(new Dictionary<string, byte[]>(StringComparer.Ordinal), new Dictionary<string, byte[]>(StringComparer.Ordinal), null, false);

    public string? Problem { get; }

    public bool Requested { get; }

    public bool HasHeaderKey => HeaderKey is not null;

    public byte[]? HeaderKey
    {
        get
        {
            var key = Get("header_key");
            return key is { Length: 32 } ? key : null;
        }
    }

    public static SwitchKeys Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return None;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim());
            string? prod;
            string? titles;
            if (Directory.Exists(full))
            {
                prod = Find(full, "prod.keys");
                titles = Find(full, "title.keys");
                if (prod is null)
                {
                    return Missing("That folder has no prod.keys, so NCA headers stay encrypted.");
                }
            }
            else if (File.Exists(full))
            {
                prod = full;
                titles = Find(Path.GetDirectoryName(full) ?? "", "title.keys");
                if (titles is not null && string.Equals(Path.GetFullPath(titles), Path.GetFullPath(prod), StringComparison.OrdinalIgnoreCase))
                {
                    titles = null;
                }
            }
            else
            {
                return Missing("The Switch keys path could not be read, so NCAs stay encrypted.");
            }

            var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var titleKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (!TryRead(prod, values, titleKeys: false))
            {
                return Missing("The keys file could not be read, so NCAs stay encrypted.");
            }

            if (titles is not null && !TryRead(titles, titleKeys, titleKeys: true))
            {
                return new SwitchKeys(values, titleKeys, "title.keys could not be read. NCA headers can still be decrypted when prod.keys has a header key.", true);
            }

            return new SwitchKeys(values, titleKeys, null, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Missing("The Switch keys path could not be read, so NCAs stay encrypted.");
        }
    }

    private static SwitchKeys Missing(string problem) =>
        new(new Dictionary<string, byte[]>(StringComparer.Ordinal), new Dictionary<string, byte[]>(StringComparer.Ordinal), problem, true);

    public byte[]? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public byte[]? KeyAreaKey(int index, int master)
    {
        var kind = index switch
        {
            0 => "application",
            1 => "ocean",
            2 => "system",
            _ => null
        };
        if (kind is null || master < 0 || master > 0xFF)
        {
            return null;
        }

        var key = Get("key_area_key_" + kind + "_" + master.ToString("x2", CultureInfo.InvariantCulture));
        return key is { Length: 16 } ? key : null;
    }

    /// <summary>
    /// Title keys are stored either already decrypted, or encrypted with <c>titlekek_XX</c>.
    /// Both forms are returned; the caller keeps the one that opens the section.
    /// </summary>
    public void TitleKeyCandidates(ReadOnlySpan<byte> rightsId, int master, List<byte[]> into)
    {
        if (rightsId.Length != 16)
        {
            return;
        }

        var hex = Convert.ToHexString(rightsId).ToLowerInvariant();
        if (!_titleKeys.TryGetValue(hex, out var stored) && hex.Length >= 16)
        {
            _titleKeys.TryGetValue(hex[..16], out stored);
        }

        if (stored is not { Length: 16 })
        {
            return;
        }

        var kekName = "titlekek_" + master.ToString("x2", CultureInfo.InvariantCulture);
        var kek = Get(kekName);
        if (kek is { Length: 16 })
        {
            var decrypted = stored.ToArray();
            NintendoAes.Ecb(decrypted, kek, encrypt: false);
            into.Add(decrypted);
        }

        into.Add(stored.ToArray());
    }

    private static bool TryRead(string path, Dictionary<string, byte[]> destination, bool titleKeys)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes)
            {
                return info.Exists && info.Length == 0;
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line[0] is '#' or ';')
                {
                    continue;
                }

                var split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                var name = line[..split].Trim().ToLowerInvariant();
                var value = ParseHex(line[(split + 1)..]);
                if (name.Length == 0 || value is null)
                {
                    continue;
                }

                if (titleKeys)
                {
                    if (value.Length == 16 && name.All(Uri.IsHexDigit) && name.Length is 16 or 32)
                    {
                        destination[name] = value;
                    }

                    continue;
                }

                destination[name] = value;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[]? ParseHex(string text)
    {
        var filtered = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (filtered.Length == 0 || (filtered.Length & 1) != 0)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(filtered);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Find(string directory, string name)
    {
        if (directory.Length == 0)
        {
            return null;
        }

        var direct = Path.Combine(directory, name);
        if (File.Exists(direct))
        {
            return direct;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
