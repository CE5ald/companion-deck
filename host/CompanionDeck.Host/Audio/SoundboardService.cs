using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompanionDeck.Host.Protocol;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CompanionDeck.Host.Audio;

/// <summary>
/// Saved clips, fired from the tablet, sent to the call and to the person firing them. The
/// catalogue is a folder, not a database.
/// </summary>
public sealed class SoundboardService : IDisposable
{
    private static readonly string[] Extensions = { ".wav", ".mp3", ".m4a", ".ogg", ".flac", ".wma", ".aac" };

    /// <summary>
    /// Ceiling on decoded audio held in memory. A minute of 48k stereo float is about 23MB,
    /// so without a cap a folder of long clips would quietly cost hundreds of megabytes on a
    /// machine whose whole point is to stay out of the way while a game runs.
    /// </summary>
    private const long CacheBudgetBytes = 64L * 1024 * 1024;

    private readonly MicCastService _cast;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();

    private readonly Dictionary<string, Clip> _clips = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Meta> _meta = new(StringComparer.Ordinal);
    private readonly List<ClipPlayer> _playing = new();

    private FileSystemWatcher? _watcher;
    private WasapiOut? _monitor;
    private SyncMixer? _monitorMix;
    /// <summary>Which endpoint the monitor is bound to, so a headset swap can be noticed.</summary>
    private string? _monitorDeviceId;

    public SoundboardService(MicCastService cast) => _cast = cast;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CompanionDeck", "soundboard");

    private static string ManifestPath => Path.Combine(Folder, "soundboard.json");

    /// <summary>Raised when the folder changes, so the server can republish without polling.</summary>
    public event Action? Changed;

    private sealed class Clip
    {
        public string Id = "";
        public string File = "";
        public string Name = "";
        public float[]? Samples;      // decoded, 48k stereo interleaved; null until first play
        public double Seconds;
        public bool Broken;
        public float Norm = 1f;       // loudness-matching gain, computed at decode time
        public bool Silent;           // decoded and found to be all but silent
    }

    /// <summary>
    /// Target level for normalisation, and the ceiling it may not push a clip past. Matching on
    /// RMS rather than peak is what makes clips sound equally loud: peak normalisation lines up
    /// the single loudest sample, so a quiet clip with one click in it stays quiet while a
    /// compressed one becomes deafening.
    /// </summary>
    private const float TargetRms = 0.1f;      // -20 dBFS
    private const float PeakCeiling = 0.89f;   // -1 dBFS
    private const float MaxBoost = 8f;         // +18 dB, past which noise is all that is left

    private bool _normalise = true;
    public bool Normalise => _normalise;
    public void SetNormalise(bool on) { _normalise = on; Changed?.Invoke(); }

    /// <summary>
    /// Works out the gain that brings a clip to the target level without clipping it.
    ///
    /// Silence is left alone rather than amplified: a clip of nothing has an RMS near zero,
    /// and the naive ratio would ask for enormous gain and produce a wall of noise.
    /// </summary>
    private static float ComputeNorm(float[] s)
    {
        if (s.Length == 0) return 1f;

        double sum = 0;
        float peak = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float a = Math.Abs(s[i]);
            if (a > peak) peak = a;
            sum += (double)s[i] * s[i];
        }

        float rms = (float)Math.Sqrt(sum / s.Length);
        if (rms < 1e-4f || peak < 1e-4f) return 1f;

        float wanted = TargetRms / rms;
        float allowed = PeakCeiling / peak;
        return Math.Clamp(Math.Min(wanted, allowed), 0.05f, MaxBoost);
    }

    /// <summary>Below this there is nothing a person would hear at any sane volume.</summary>
    private static bool IsSilent(float[] s)
    {
        for (int i = 0; i < s.Length; i++)
            if (Math.Abs(s[i]) > 1e-4f) return false;
        return true;
    }

    private sealed class Meta
    {
        public string? Name { get; set; }
        public string? Route { get; set; }   // both | mic | ears
        public int? Gain { get; set; }       // 0-100
        public int? Order { get; set; }
    }

    public void Initialize()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            LoadManifest();
            Rescan();

            // Live: a file dropped in while the tablet is showing the page should appear
            // without anyone having to think about refreshing anything.
            _watcher = new FileSystemWatcher(Folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler onChange = (_, _) => Debounce();
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Renamed += (_, _) => Debounce();

            Log.Info($"soundboard: {_clips.Count} clip(s) in {Folder}");
        }
        catch (Exception ex) { Log.Warn($"soundboard init: {ex.Message}"); }
    }

    // Editors and copy operations produce several events for one logical change; without
    // this the catalogue would be rebuilt three or four times per dropped file.
    private Timer? _debounce;
    private void Debounce()
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => { Rescan(); Changed?.Invoke(); }, null, 400, Timeout.Infinite);
    }

    public void Rescan()
    {
        try
        {
            var found = new Dictionary<string, Clip>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(Folder))
            {
                if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                string id = IdFor(path);
                lock (_gate)
                {
                    // Keep the decoded samples across a rescan: a rename must not throw away
                    // audio that is already in memory and possibly playing.
                    if (_clips.TryGetValue(id, out var existing) && existing.File == path)
                    {
                        found[id] = existing;
                        continue;
                    }
                }
                found[id] = new Clip { Id = id, File = path, Name = Path.GetFileNameWithoutExtension(path) };
            }

            lock (_gate)
            {
                _clips.Clear();
                foreach (var (k, v) in found) _clips[k] = v;
            }
        }
        catch (Exception ex) { Log.Warn($"soundboard scan: {ex.Message}"); }
    }

    /// <summary>Stable across restarts and independent of position in the folder.</summary>
    private static string IdFor(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(Path.GetFileName(path).ToLowerInvariant());
        return System.Convert.ToHexString(MD5.HashData(bytes))[..12].ToLowerInvariant();
    }

    public SoundboardState Snapshot()
    {
        lock (_gate)
        {
            var items = _clips.Values.Select(c =>
            {
                _meta.TryGetValue(c.Id, out var m);
                return new SoundClipInfo
                {
                    Id = c.Id,
                    Name = m?.Name ?? c.Name,
                    Route = m?.Route ?? "both",
                    Gain = m?.Gain ?? 100,
                    Order = m?.Order ?? int.MaxValue,
                    Seconds = Math.Round(c.Seconds, 1),
                    Broken = c.Broken,
                    Silent = c.Silent,
                    Playing = _playing.Any(p => p.ClipId == c.Id && !p.Finished),
                };
            })
            .OrderBy(i => i.Order)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

            return new SoundboardState { Items = items, Folder = Folder };
        }
    }


    public bool Play(string id)
    {
        Clip? clip;
        Meta? meta;
        lock (_gate)
        {
            if (!_clips.TryGetValue(id, out clip)) { Log.Warn($"soundboard: unknown clip '{id}'"); return false; }
            _meta.TryGetValue(id, out meta);
        }

        if (!EnsureDecoded(clip)) return false;

        string route = meta?.Route ?? "both";
        // The pad's own level multiplies the normalisation rather than replacing it, so
        // turning one clip down still leaves it matched relative to the others.
        float gain = Math.Clamp((meta?.Gain ?? 100) / 100f, 0f, 1f)
                   * (_normalise ? clip.Norm : 1f);
        bool wantMic = route is "both" or "mic";
        bool wantEars = route is "both" or "ears";

        bool sentToMic = false;
        if (wantMic)
        {
            var p = new ClipPlayer(clip.Id, clip.Samples!, gain);
            if (_cast.TryAddToCast(p))
            {
                sentToMic = true;
                Track(p, () => _cast.RemoveFromCast(p));

                // Only duck for audio that is actually going out. Ducking for an ears-only
                // clip would quieten the game for the person firing it and nobody else.
                _cast.PushDuck();
                p.Completed += () => _cast.PopDuck();
            }
        }

        // "mic" routing still plays locally when the cast is not armed, rather than
        // producing silence and looking broken.
        if (wantEars || (wantMic && !sentToMic)) PlayToEars(clip, gain);

        Changed?.Invoke();
        return true;
    }

    private void PlayToEars(Clip clip, float gain)
    {
        try
        {
            // Resolved before taking the lock: enumerating endpoints is COM work and has no
            // business happening inside it.
            var device = _cast.ResolveRender()
                ?? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            // Rebuilt when the endpoint moves underneath us. The monitor used to be created
            // once and kept forever, so plugging in a headset after the first clip left every
            // later clip playing to the speakers nobody was listening to - audible to the
            // room, silent to the person holding the tablet.
            bool moved;
            lock (_gate) moved = _monitor is not null && _monitorDeviceId != device.ID;
            if (moved)
            {
                Log.Info($"soundboard: output moved to {device.FriendlyName}; rebuilding monitor");
                StopMonitor();
            }

            lock (_gate)
            {
                if (_monitor is null)
                {
                    _monitorMix = new SyncMixer(MixFormat.Format);
                    _monitor = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
                    _monitorDeviceId = device.ID;
                    // A device that disappears mid-clip stops the stream; drop it so the next
                    // clip builds a fresh one rather than pushing into a dead output.
                    _monitor.PlaybackStopped += (_, e) =>
                    {
                        if (e.Exception is not null) Log.Warn($"soundboard monitor stopped: {e.Exception.Message}");
                        ThreadPool.QueueUserWorkItem(_ => StopMonitor());
                    };
                    _monitor.Init(_monitorMix);
                    _monitor.Play();
                    Log.Info($"soundboard monitor on {device.FriendlyName}");
                }

                var p = new ClipPlayer(clip.Id, clip.Samples!, gain);
                _monitorMix!.Add(p);
                Track(p, () => { try { _monitorMix?.Remove(p); } catch { } });
            }
        }
        catch (Exception ex) { Log.Warn($"soundboard monitor: {ex.Message}"); }
    }

    private void Track(ClipPlayer p, Action onDone)
    {
        lock (_gate) _playing.Add(p);
        p.Completed += () =>
        {
            onDone();
            lock (_gate) _playing.Remove(p);
            ScheduleMonitorClose();
            Changed?.Invoke();
        };
    }

    /// <summary>
    /// How long the monitor output stays open after the last clip finishes. It used to stay open
    /// forever, which is fine on speakers and audibly wrong on a Bluetooth headset: an open
    /// render stream keeps the A2DP link and its codec active, and the headset fills the silence
    /// with hiss.
    /// </summary>
    private const int MonitorIdleMs = 2500;
    private Timer? _idleTimer;

    private void ScheduleMonitorClose()
    {
        lock (_gate)
        {
            _idleTimer?.Dispose();
            _idleTimer = new Timer(_ =>
            {
                bool close;
                lock (_gate) close = _playing.Count == 0 && _monitor is not null;
                if (!close) return;

                Log.Info("soundboard monitor idle; releasing the output device");
                // Outside the lock - see StopMonitor. Closing a Bluetooth endpoint can take
                // seconds, and doing it under _gate wedged the whole agent.
                StopMonitor();
            }, null, MonitorIdleMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Stops everything, and makes sure every stopped clip is actually accounted for. Cancelling
    /// alone was not enough.
    /// </summary>
    public void StopAll()
    {
        ClipPlayer[] snapshot;
        lock (_gate) snapshot = _playing.ToArray();

        foreach (var p in snapshot) { p.Cancel(); p.ForceComplete(); }

        // Belt and braces: anything that somehow survived its own completion handler goes
        // now, so this list can never accumulate.
        lock (_gate) _playing.Clear();

        ScheduleMonitorClose();
        Log.Info($"soundboard: stopped {snapshot.Length} clip(s)");
        Changed?.Invoke();
    }

    private bool EnsureDecoded(Clip clip)
    {
        lock (_gate) { if (clip.Samples is not null) return true; if (clip.Broken) return false; }

        try
        {
            using var reader = new MediaFoundationReader(clip.File);
            var sp = MixFormat.Convert(reader.ToSampleProvider());

            var all = new List<float>(1 << 18);
            var buf = new float[16384];
            int n;
            while ((n = sp.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++) all.Add(buf[i]);
                // A clip is a clip. Something enormous in the folder is almost certainly not
                // meant as a pad, and decoding it whole would blow the budget on its own.
                if (all.Count > MixFormat.SampleRate * MixFormat.Channels * 120) break;
            }

            var samples = all.ToArray();
            float norm = ComputeNorm(samples);
            // A capture made while nothing was playing is digitally silent, and a pad that
            // plays silence is indistinguishable from a pad that is broken. Saying so is the
            // difference between "this clip has nothing in it" and "the soundboard is
            // broken" - which is exactly the wrong conclusion it invited.
            bool silent = IsSilent(samples);
            lock (_gate)
            {
                Evict(samples.Length * 4L);
                clip.Samples = samples;
                clip.Norm = norm;
                clip.Silent = silent;
                clip.Seconds = samples.Length / (double)(MixFormat.SampleRate * MixFormat.Channels);
            }
            Log.Debug($"soundboard: {Path.GetFileName(clip.File)} normalised x{norm:F2}");
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate) clip.Broken = true;
            Log.Warn($"soundboard: cannot decode {Path.GetFileName(clip.File)}: {ex.Message}");
            Changed?.Invoke();
            return false;
        }
    }

    /// <summary>Frees decoded audio until the newcomer fits, never touching anything playing.</summary>
    private void Evict(long incoming)
    {
        long used = _clips.Values.Sum(c => (long)(c.Samples?.Length ?? 0) * 4);
        if (used + incoming <= CacheBudgetBytes) return;

        foreach (var c in _clips.Values.Where(c => c.Samples is not null))
        {
            if (_playing.Any(p => p.ClipId == c.Id && !p.Finished)) continue;
            used -= c.Samples!.Length * 4L;
            c.Samples = null;
            if (used + incoming <= CacheBudgetBytes) return;
        }
    }


    public bool SetName(string id, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40) return false;
        return Mutate(id, m => m.Name = name);
    }

    public bool SetRoute(string id, string route)
    {
        if (route is not ("both" or "mic" or "ears")) return false;
        return Mutate(id, m => m.Route = route);
    }

    public bool SetGain(string id, int gain) => Mutate(id, m => m.Gain = Math.Clamp(gain, 0, 100));

    private bool Mutate(string id, Action<Meta> apply)
    {
        lock (_gate)
        {
            if (!_clips.ContainsKey(id)) return false;
            if (!_meta.TryGetValue(id, out var m)) _meta[id] = m = new Meta();
            apply(m);
        }
        SaveManifest();
        Changed?.Invoke();
        return true;
    }

    private void LoadManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Meta>>(File.ReadAllText(ManifestPath));
            if (parsed is null) return;
            lock (_gate) { _meta.Clear(); foreach (var (k, v) in parsed) _meta[k] = v; }
        }
        catch (Exception ex) { Log.Warn($"soundboard manifest: {ex.Message}"); }
    }

    private void SaveManifest()
    {
        try
        {
            Dictionary<string, Meta> copy;
            lock (_gate) copy = new Dictionary<string, Meta>(_meta);
            Directory.CreateDirectory(Folder);
            File.WriteAllText(ManifestPath,
                JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Warn($"soundboard manifest save: {ex.Message}"); }
    }

    /// <summary>Called by the recorder when it drops a new file in, so the pad appears at once.</summary>
    public void NoteNewFile() { Rescan(); Changed?.Invoke(); }

    /// <summary>
    /// Closes the monitor output, doing the device teardown outside the service lock. This is not
    /// a style preference.
    /// </summary>
    private void StopMonitor()
    {
        WasapiOut? mon;
        SyncMixer? mix;
        ClipPlayer[] orphans;

        lock (_gate)
        {
            mon = _monitor;
            mix = _monitorMix;
            orphans = _playing.ToArray();
            _monitor = null; _monitorMix = null; _monitorDeviceId = null;
        }

        // Anything still queued on this output is about to be orphaned - it will never be
        // read again, so it has to be completed or it leaks along with its duck.
        foreach (var p in orphans)
        {
            p.Cancel();
            try { mix?.Remove(p); } catch { }
            p.ForceComplete();
        }

        try { mon?.Stop(); } catch (Exception ex) { Log.Debug($"monitor stop: {ex.Message}"); }
        try { mon?.Dispose(); } catch (Exception ex) { Log.Debug($"monitor dispose: {ex.Message}"); }
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { }
        try { _debounce?.Dispose(); } catch { }
        try { _idleTimer?.Dispose(); } catch { }
        StopMonitor();
        try { _enumerator.Dispose(); } catch { }
    }

    /// <summary>
    /// Plays one cached clip once. Deliberately a value-free reader over a shared array: the
    /// samples belong to the catalogue, and two players over the same clip - one to the
    /// cable, one to the speakers - must not disturb each other.
    /// </summary>
    private sealed class ClipPlayer : ISampleProvider
    {
        private readonly float[] _samples;
        private readonly float _gain;
        private int _pos;
        private volatile bool _cancelled;
        private int _done;

        public string ClipId { get; }
        public bool Finished => _done != 0;
        public event Action? Completed;

        public ClipPlayer(string clipId, float[] samples, float gain)
        {
            ClipId = clipId; _samples = samples; _gain = gain;
        }

        public WaveFormat WaveFormat => MixFormat.Format;

        public void Cancel() => _cancelled = true;

        /// <summary>
        /// Runs the completion handler without waiting to be read again. Needed because a player
        /// that is no longer in any mixer will never be read, and its handler is what releases
        /// the duck and removes it from the playing list.
        /// </summary>
        public void ForceComplete() => Complete();

        public int Read(float[] buffer, int offset, int count)
        {
            if (_cancelled) { Complete(); return 0; }

            int left = _samples.Length - _pos;
            if (left <= 0) { Complete(); return 0; }

            int n = Math.Min(count, left);
            for (int i = 0; i < n; i++) buffer[offset + i] = _samples[_pos + i] * _gain;
            _pos += n;

            if (_pos >= _samples.Length) Complete();
            return n;
        }

        /// <summary>
        /// Fires once. A mixer may read a finished provider more than once before dropping
        /// it, and the duck counter must not be decremented twice for one clip.
        /// </summary>
        private void Complete()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;

            // Off the caller's thread, always. Read runs on the WASAPI render thread inside
            // the mixer's lock, and this handler removes mixer inputs, takes the service lock
            // and publishes to the tablet - re-entering the audio path and putting a network
            // send on the thread that has to refill the buffer in time. Neither belongs there.
            var handler = Completed;
            if (handler is not null) ThreadPool.QueueUserWorkItem(_ => handler());
        }
    }
}
