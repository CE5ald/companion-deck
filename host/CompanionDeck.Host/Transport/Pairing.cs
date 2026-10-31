using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CompanionDeck.Host.Transport;

/// <summary>
/// The secret that lets a tablet use the network fallback. The cable is the trust anchor: whoever
/// holds the cable is holding the machine, so over USB the PC simply hands the tablet the key. Over
/// the network each side then proves it has the key without sending it, in both directions, so a
/// stranger on the same Wi-Fi can neither drive the PC nor pose as it.
/// </summary>
public sealed class Pairing
{
    private readonly string _path;
    private readonly object _gate = new();
    private byte[] _key = Array.Empty<byte>();

    public Pairing(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CompanionDeck", "pairing.json");
    }

    public string FilePath => _path;

    public string KeyBase64 { get { lock (_gate) return Convert.ToBase64String(_key); } }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
                    var bytes = Convert.FromBase64String(doc.RootElement.GetProperty("key").GetString() ?? "");
                    if (bytes.Length == 32) { _key = bytes; return; }
                }
            }
            catch (Exception ex) { Log.Warn($"pairing.json unreadable ({ex.Message}); making a new key"); }

            NewKeyLocked();
        }
    }

    /// <summary>Replaces the key. Every tablet but the one on the cable is locked out of Wi-Fi.</summary>
    public void Rotate()
    {
        lock (_gate) NewKeyLocked();
        Log.Info("pairing key replaced; other tablets must be reconnected by cable once");
    }

    private void NewKeyLocked()
    {
        _key = RandomNumberGenerator.GetBytes(32);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new
            {
                key = Convert.ToBase64String(_key),
                created = DateTime.UtcNow.ToString("o"),
            }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"could not save pairing.json: {ex.Message}"); }
    }

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    /// <summary>What the PC sends to show the tablet it holds the key.</summary>
    public string HostProof(string tabletNonce, string hostNonce) => Proof("host", tabletNonce, hostNonce);

    /// <summary>What the PC expects back from a tablet that holds the key.</summary>
    public bool VerifyTablet(string? tabletNonce, string? hostNonce, string? proof)
    {
        if (string.IsNullOrEmpty(tabletNonce) || string.IsNullOrEmpty(hostNonce) || string.IsNullOrEmpty(proof)) return false;
        try
        {
            var expected = Convert.FromBase64String(Proof("tablet", hostNonce, tabletNonce));
            var given = Convert.FromBase64String(proof);
            return CryptographicOperations.FixedTimeEquals(expected, given);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>
    /// Role-tagged so a proof captured from one side cannot be replayed as the other, and over
    /// both nonces so it only ever answers the connection it was made for.
    /// </summary>
    public string Proof(string role, string first, string second)
    {
        byte[] key;
        lock (_gate) key = _key;
        using var h = new HMACSHA256(key);
        return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes($"{role}|{first}|{second}")));
    }
}
