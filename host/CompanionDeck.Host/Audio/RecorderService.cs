using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CompanionDeck.Host.Audio;

/// <summary>
/// Captures what the laptop is playing - which is to say, what the person is hearing: a call, a
/// game, a video. The rolling buffer is the point.
/// </summary>
public sealed class RecorderService : IDisposable
{
    /// <summary>How much history the rolling buffer holds. ~23MB at 48k stereo float.</summary>
    private const int BufferSeconds = 60;

    private readonly SoundboardService _soundboard;
    private readonly MicCastService _cast;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();

    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _raw;
    private ISampleProvider? _converted;
    private Thread? _pump;
    private volatile bool _running;

    // Ring buffer of 48k stereo float, oldest overwritten.
    private float[] _ring = Array.Empty<float>();
    private int _write;
    private long _total;

    private WaveFileWriter? _writer;
    private string? _writerPath;
    private DateTime _writerStarted;
    private string? _deviceId;

    /// <summary>
    /// Re-binds to the output if it has moved since arming. The device is chosen once when the
    /// capture is built, so plugging in a headset used to leave the recorder tapping speakers
    /// nobody was using - it stayed armed, the buffer stayed full of silence, and nothing said
    /// so.
    /// </summary>
    public void CheckDevice()
    {
        if (!_running) return;
        try
        {
            using var want = _cast.ResolveRender();
            if (want is null || want.ID == _deviceId) return;

            Log.Info($"recorder: output moved to {want.FriendlyName}; re-arming");
            bool wasRecording;
            lock (_gate) wasRecording = _writer is not null;

            Disarm();
            if (Arm() && wasRecording) StartRecording();
        }
        catch (Exception ex) { Log.Debug($"recorder device check: {ex.Message}"); }
    }

    public RecorderService(SoundboardService soundboard, MicCastService cast)
    {
        _soundboard = soundboard;
        _cast = cast;
    }

    public bool Armed => _running;
    public bool Recording { get { lock (_gate) return _writer is not null; } }
    public int BufferedSeconds
    {
        get
        {
            long frames = Math.Min(_total, _ring.Length) / MixFormat.Channels;
            return (int)(frames / MixFormat.SampleRate);
        }
    }
    public int RecordingSeconds
    {
        get { lock (_gate) return _writer is null ? 0 : (int)(DateTime.UtcNow - _writerStarted).TotalSeconds; }
    }
    public int Capacity => BufferSeconds;
    public string? LastError { get; private set; }
    public string? LastSaved { get; private set; }

    public bool Arm()
    {
        lock (_gate)
        {
            if (_running) return true;
            LastError = null;
            try
            {
                // The output the Voice page is pointed at, so a clip captures what the
                // person is actually hearing.
                var device = _cast.ResolveRender()
                    ?? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                _capture = new WasapiLoopbackCapture(device);
                _deviceId = device.ID;
                _raw = new BufferedWaveProvider(_capture.WaveFormat)
                {
                    ReadFully = false,
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(3),
                };
                _converted = MixFormat.Convert(_raw);

                _ring = new float[BufferSeconds * MixFormat.SampleRate * MixFormat.Channels];
                _write = 0;
                _total = 0;

                _capture.DataAvailable += (_, e) => _raw?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                _capture.RecordingStopped += (_, e) =>
                {
                    if (!_running) return;
                    LastError = e.Exception?.Message ?? "capture stopped";
                    Log.Warn($"recorder: {LastError}");
                    ThreadPool.QueueUserWorkItem(_ => Disarm());
                };
                _capture.StartRecording();

                _running = true;
                // A dedicated pump rather than converting inside DataAvailable: resampling on
                // the capture callback thread stalls WASAPI's own delivery and drops audio.
                _pump = new Thread(Pump) { IsBackground = true, Name = "deck-recorder" };
                _pump.Start();

                Log.Info($"recorder armed on {device.FriendlyName} ({BufferSeconds}s buffer)");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Warn($"recorder arm failed: {ex.Message}");
                TeardownLocked();
                return false;
            }
        }
    }

    public void Disarm()
    {
        lock (_gate)
        {
            if (!_running && _capture is null) return;
            StopRecordingLocked();
            TeardownLocked();
            Log.Info("recorder disarmed");
        }
    }

    private void TeardownLocked()
    {
        _running = false;
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null; _raw = null; _converted = null; _deviceId = null;
        _ring = Array.Empty<float>();
        _total = 0; _write = 0;
    }

    private void Pump()
    {
        var buf = new float[8192];
        while (_running)
        {
            int n = 0;
            try
            {
                var src = _converted;
                if (src is not null) n = src.Read(buf, 0, buf.Length);
            }
            catch (Exception ex) { Log.Debug($"recorder pump: {ex.Message}"); }

            if (n <= 0) { Thread.Sleep(15); continue; }

            lock (_gate)
            {
                var ring = _ring;
                if (ring.Length > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        ring[_write] = buf[i];
                        if (++_write >= ring.Length) _write = 0;
                    }
                    _total += n;
                }

                if (_writer is not null)
                {
                    try { _writer.WriteSamples(buf, 0, n); }
                    catch (Exception ex) { Log.Warn($"recorder write: {ex.Message}"); StopRecordingLocked(); }
                }
            }
        }
    }

    /// <summary>
    /// Writes the last <paramref name="seconds"/> of history to the soundboard folder.
    ///
    /// This is the whole reason the buffer exists: the decision to keep something is made
    /// after it has been heard.
    /// </summary>
    public string? SaveClip(int seconds)
    {
        seconds = Math.Clamp(seconds, 3, BufferSeconds);

        float[] copy;
        lock (_gate)
        {
            if (!_running || _ring.Length == 0) { LastError = "the recorder is not armed"; return null; }

            int want = seconds * MixFormat.SampleRate * MixFormat.Channels;
            int have = (int)Math.Min(_total, _ring.Length);
            int take = Math.Min(want, have);
            if (take <= 0) { LastError = "nothing captured yet"; return null; }

            // Align to a frame boundary, or the channels swap and the clip plays inverted.
            take -= take % MixFormat.Channels;

            copy = new float[take];
            int start = ((_write - take) % _ring.Length + _ring.Length) % _ring.Length;
            int first = Math.Min(take, _ring.Length - start);
            Array.Copy(_ring, start, copy, 0, first);
            if (take > first) Array.Copy(_ring, 0, copy, first, take - first);
        }

        return WriteWav(copy, $"clip {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
    }

    public bool StartRecording()
    {
        lock (_gate)
        {
            if (!_running) { LastError = "the recorder is not armed"; return false; }
            if (_writer is not null) return true;
            try
            {
                Directory.CreateDirectory(SoundboardService.Folder);
                _writerPath = UniquePath($"recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
                _writer = new WaveFileWriter(_writerPath, MixFormat.Format);
                _writerStarted = DateTime.UtcNow;
                Log.Info($"recording to {Path.GetFileName(_writerPath)}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Warn($"recorder start: {ex.Message}");
                return false;
            }
        }
    }

    public string? StopRecording()
    {
        lock (_gate)
        {
            var path = _writerPath;
            StopRecordingLocked();
            if (path is null) return null;
            LastSaved = Path.GetFileName(path);
            _soundboard.NoteNewFile();
            return path;
        }
    }

    private void StopRecordingLocked()
    {
        if (_writer is null) return;
        try { _writer.Flush(); _writer.Dispose(); } catch (Exception ex) { Log.Warn($"recorder close: {ex.Message}"); }
        _writer = null;
        if (_writerPath is not null) Log.Info($"recording saved: {Path.GetFileName(_writerPath)}");
        _writerPath = null;
    }

    private string? WriteWav(float[] samples, string baseName)
    {
        try
        {
            Directory.CreateDirectory(SoundboardService.Folder);
            string path = UniquePath(baseName);
            using (var w = new WaveFileWriter(path, MixFormat.Format))
                w.WriteSamples(samples, 0, samples.Length);

            LastSaved = Path.GetFileName(path);
            Log.Info($"clip saved: {LastSaved} ({samples.Length / (double)(MixFormat.SampleRate * MixFormat.Channels):F1}s)");
            _soundboard.NoteNewFile();
            return path;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Warn($"clip save: {ex.Message}");
            return null;
        }
    }

    private static string UniquePath(string baseName)
    {
        string path = Path.Combine(SoundboardService.Folder, baseName + ".wav");
        int n = 2;
        while (File.Exists(path))
            path = Path.Combine(SoundboardService.Folder, $"{baseName} ({n++}).wav");
        return path;
    }

    public void Dispose()
    {
        Disarm();
        try { _enumerator.Dispose(); } catch { }
    }
}
