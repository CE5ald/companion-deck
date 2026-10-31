namespace CompanionDeck.Host.Tiles;

/// <summary>
/// One thing a tile does. The kinds are a closed set, and each carries only the fields that
/// kind reads; <see cref="TileStore"/> rejects anything else when a tile is saved.
/// </summary>
public sealed class TileStep
{
    /// <summary>sys | key | launch | audio | media | sound | wait | timer</summary>
    public string Do { get; set; } = "";
    /// <summary>An action, launcher entry, clip or output device, depending on the kind.</summary>
    public string? Id { get; set; }
    /// <summary>A chord such as "ctrl+shift+m".</summary>
    public string? Keys { get; set; }
    public string? Verb { get; set; }
    public int? Value { get; set; }
    /// <summary>Milliseconds, for a wait.</summary>
    public int? Ms { get; set; }
}

public sealed class Tile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "i-bolt";
    /// <summary>A palette name, or null for the accent.</summary>
    public string? Color { get; set; }
    /// <summary>Must be held to run. Forced on for any tile that can end work in progress.</summary>
    public bool Confirm { get; set; }
    public List<TileStep> Steps { get; set; } = new();
}

public sealed class TileProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>
    /// Process names that bring this profile forward when one is in front, or "@gaming",
    /// "@music", "@video" for the context service's own verdict. Empty for the default.
    /// </summary>
    public List<string> Match { get; set; } = new();
    public List<Tile> Tiles { get; set; } = new();
}

public sealed class TileDocument
{
    public int Version { get; set; } = 1;
    public List<TileProfile> Profiles { get; set; } = new();
    /// <summary>A profile held in front regardless of what is running.</summary>
    public string? Pinned { get; set; }
}
