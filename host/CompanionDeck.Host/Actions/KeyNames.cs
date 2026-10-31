namespace CompanionDeck.Host.Actions;

/// <summary>
/// The only keys a tile may press. A chord is written as text ("ctrl+shift+m") and parsed here,
/// so the vocabulary is closed: a name outside it is rejected when the tile is saved, not
/// when it is run.
/// </summary>
public static class KeyNames
{
    public const int MaxKeys = 5;

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12,
        ["win"] = 0x5B, ["windows"] = 0x5B,
        ["enter"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["prtsc"] = 0x2C, ["printscreen"] = 0x2C,
        ["volumemute"] = 0xAD, ["volumedown"] = 0xAE, ["volumeup"] = 0xAF,
        ["nexttrack"] = 0xB0, ["prevtrack"] = 0xB1, ["stopmedia"] = 0xB2, ["playpause"] = 0xB3,
    };

    private static readonly HashSet<ushort> Modifiers = new() { 0x11, 0x10, 0x12, 0x5B };

    /// <summary>The punctuation keys of a US layout, by the character they type.</summary>
    private static readonly Dictionary<char, ushort> Punctuation = new()
    {
        [';'] = 0xBA, ['='] = 0xBB, [','] = 0xBC, ['-'] = 0xBD, ['.'] = 0xBE, ['/'] = 0xBF,
        ['`'] = 0xC0, ['['] = 0xDB, ['\\'] = 0xDC, [']'] = 0xDD, ['\''] = 0xDE,
    };

    /// <summary>
    /// What a recorded macro may contain: the keys a person types and navigates with, plus the
    /// modifiers. Everything else (volume keys, power, browser and launcher keys, the many
    /// vendor-specific ones) is dropped from a recording rather than replayed later.
    /// </summary>
    public static bool IsRecordable(int vk) =>
        vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A      // 0-9, A-Z
           or >= 0x60 and <= 0x6F                              // numpad and its operators
           or >= 0x70 and <= 0x87                              // F1-F24
           or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDE       // punctuation
           or >= 0xA0 and <= 0xA5                              // left and right shift, ctrl, alt
           or 0x08 or 0x09 or 0x0D or 0x10 or 0x11 or 0x12 or 0x14 or 0x1B or 0x20
           or (>= 0x21 and <= 0x28) or 0x2C or 0x2D or 0x2E or 0x5B or 0x5C;

    /// <summary>Parses "ctrl+shift+m". Returns false for anything outside the vocabulary.</summary>
    public static bool TryParse(string? text, out ushort[] keys)
    {
        keys = Array.Empty<ushort>();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > MaxKeys) return false;

        var list = new List<ushort>(parts.Length);
        foreach (var raw in parts)
        {
            if (!TryKey(raw, out ushort vk)) return false;
            if (list.Contains(vk)) return false;
            list.Add(vk);
        }

        // Modifiers go down first and the real key last, whatever order they were written in,
        // or "m+ctrl" would press M before Ctrl was held.
        keys = list.OrderBy(k => Modifiers.Contains(k) ? 0 : 1).ToArray();
        return true;
    }

    private static bool TryKey(string name, out ushort vk)
    {
        vk = 0;
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { vk = c; return true; }
            return Punctuation.TryGetValue(c, out vk);
        }

        if (name.Length is 2 or 3 && (name[0] is 'f' or 'F') &&
            int.TryParse(name.AsSpan(1), out int n) && n is >= 1 and <= 24)
        {
            vk = (ushort)(0x6F + n);
            return true;
        }

        return Named.TryGetValue(name, out vk);
    }

    /// <summary>The names the editor offers, in the order it shows them.</summary>
    public static readonly string[] Palette =
    {
        "ctrl", "shift", "alt", "win",
        "enter", "esc", "tab", "space", "backspace", "delete",
        "left", "up", "right", "down", "home", "end", "pageup", "pagedown", "prtsc",
        "playpause", "nexttrack", "prevtrack", "volumemute", "volumeup", "volumedown",
    };
}
