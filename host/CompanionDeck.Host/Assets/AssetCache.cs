using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CompanionDeck.Host.Assets;

/// <summary>
/// Content-addressed store for the only large payloads in this protocol: album artwork,
/// application icons and game cover art. Keying by content hash means identical images collapse
/// to one entry, and the tablet can tell us on connect which keys it already has.
/// </summary>
public sealed class AssetCache
{
    private sealed class Entry
    {
        public required byte[] Bytes { get; init; }
        public long LastUsedTicks;
    }

    private readonly ConcurrentDictionary<string, Entry> _items = new();

    /// <summary>Keys we know the currently connected tablet already holds on disk.</summary>
    private readonly ConcurrentDictionary<string, byte> _peerHas = new();

    private const int MaxEntries = 400;

    public string Put(byte[] bytes)
    {
        var key = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();

        var entry = _items.GetOrAdd(key, _ => new Entry { Bytes = bytes });
        Interlocked.Exchange(ref entry.LastUsedTicks, DateTime.UtcNow.Ticks);

        if (_items.Count > MaxEntries) Evict();
        return key;
    }

    public byte[]? Get(string key) =>
        _items.TryGetValue(key, out var e) ? e.Bytes : null;

    /// <summary>True when the bytes still need to cross the link.</summary>
    public bool PeerNeeds(string key) => !_peerHas.ContainsKey(key);

    public void MarkPeerHas(string key) => _peerHas[key] = 1;

    public void MarkPeerHas(IEnumerable<string> keys)
    {
        foreach (var k in keys) _peerHas[k] = 1;
    }

    /// <summary>Called when a tablet disconnects; a different device may cache different keys.</summary>
    public void ResetPeer() => _peerHas.Clear();

    private void Evict()
    {
        var victims = _items
            .OrderBy(kv => Interlocked.Read(ref kv.Value.LastUsedTicks))
            .Take(_items.Count - (MaxEntries * 3 / 4))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var k in victims)
        {
            _items.TryRemove(k, out _);
            _peerHas.TryRemove(k, out _);
        }
    }
}
