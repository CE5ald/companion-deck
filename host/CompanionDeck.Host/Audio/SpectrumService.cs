using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace CompanionDeck.Host.Audio;

/// <summary>Turns a block of audio into bars. Pure, so it can be checked without a sound card.</summary>
public static class SpectrumAnalyser
{
    public const int FftSize = 1024;
    public const int Bands = 32;
    private const double LowHz = 40, HighHz = 16000;
    private const double FloorDb = -72;

    private static readonly float[] Window = Enumerable.Range(0, FftSize)
        .Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)))).ToArray();

    /// <summary>The band a frequency falls in, on the same log scale the bars use.</summary>
    public static int BandOf(double hz) =>
        (int)Math.Clamp(Bands * Math.Log(hz / LowHz) / Math.Log(HighHz / LowHz), 0, Bands - 1);

    /// <summary>Levels 0 to 100 for each band, from <see cref="FftSize"/> mono samples.</summary>
    public static float[] Compute(ReadOnlySpan<float> mono, int sampleRate)
    {
        var buf = new Complex[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            buf[i].X = i < mono.Length ? mono[i] * Window[i] : 0;
            buf[i].Y = 0;
        }
        FastFourierTransform.FFT(true, 10, buf);

        double nyquist = sampleRate / 2.0;
        double top = Math.Min(HighHz, nyquist * 0.98);
        var result = new float[Bands];
        for (int b = 0; b < Bands; b++)
        {
            double f0 = LowHz * Math.Pow(top / LowHz, (double)b / Bands);
            double f1 = LowHz * Math.Pow(top / LowHz, (double)(b + 1) / Bands);
            int k0 = Math.Max(1, (int)(f0 * FftSize / sampleRate));
            int k1 = Math.Max(k0, Math.Min(FftSize / 2 - 1, (int)(f1 * FftSize / sampleRate)));

            // The loudest bin in the band, not the average: a band that is wide and mostly empty
            // would otherwise hide a clear tone sitting in it.
            double peak = 0;
            for (int k = k0; k <= k1; k++)
                peak = Math.Max(peak, Math.Sqrt(buf[k].X * buf[k].X + buf[k].Y * buf[k].Y));

            // NAudio's forward transform divides by the size, so a full-scale sine through a Hann
            // window peaks at 0.25 here. That is 0 dB; the floor is -72.
            double db = 20 * Math.Log10(peak / 0.25 + 1e-9);
            result[b] = (float)Math.Clamp((db - FloorDb) / -FloorDb * 100, 0, 100);
        }
        return result;
    }
}

/// <summary>
/// Listens to whatever the PC is playing and reports 32 bars about twenty times a second. It exists
/// only while a tablet is looking at the visualiser: the capture is opened on subscribe and closed
/// the moment the page changes or the screen goes off.
/// </summary>
public sealed class SpectrumService : IDisposable
{
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly float[] _smooth = new float[SpectrumAnalyser.Bands];
    private readonly float[] _ring = new float[SpectrumAnalyser.FftSize];

    private WasapiLoopbackCapture? _capture;
    private string? _deviceId;
    private Timer? _decay;
    private int _filled;
    private long _lastDataMs, _lastEmitMs;
    private bool _silentSent;

    public bool Running { get { lock (_gate) return _capture is not null; } }
    public event Action<int[]>? Frame;

    public void Start()
    {
        lock (_gate)
        {
            if (_capture is not null) return;
            try
            {
                using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var cap = new WasapiLoopbackCapture(device);
                cap.DataAvailable += OnData;
                cap.RecordingStopped += (_, e) =>
                {
                    if (e.Exception is not null) Log.Debug($"spectrum capture stopped: {e.Exception.Message}");
                };
                cap.StartRecording();
                _capture = cap;
                _deviceId = device.ID;
                _filled = 0;
                // Loopback produces nothing at all while nothing plays, so bars would freeze at their last
                // height. This lets them fall.
                _decay = new Timer(_ => Decay(), null, 80, 80);
                Log.Debug("spectrum capture started");
            }
            catch (Exception ex) { Log.Warn($"spectrum capture could not start: {ex.Message}"); _capture = null; }
        }
    }

    public void Stop()
    {
        WasapiLoopbackCapture? cap;
        lock (_gate)
        {
            cap = _capture;
            _capture = null;
            _decay?.Dispose(); _decay = null;
            Array.Clear(_smooth);
            _filled = 0;
        }
        if (cap is null) return;
        try { cap.StopRecording(); } catch { }
        try { cap.Dispose(); } catch { }
        Log.Debug("spectrum capture stopped");
    }

    /// <summary>Follows the default output if it moved, as when a headset is plugged in.</summary>
    public void CheckDevice()
    {
        string? current;
        lock (_gate) { if (_capture is null) return; current = _deviceId; }
        try
        {
            using var d = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (d.ID == current) return;
        }
        catch { return; }
        Stop();
        Start();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var cap = _capture;
        if (cap is null || e.BytesRecorded == 0) return;
        var fmt = cap.WaveFormat;
        int channels = Math.Max(1, fmt.Channels);
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);
        int frameBytes = fmt.BlockAlign;

        lock (_gate)
        {
            _lastDataMs = Environment.TickCount64;
            _silentSent = false;
            for (int off = 0; off + frameBytes <= e.BytesRecorded; off += frameBytes)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    sum += isFloat ? BitConverter.ToSingle(e.Buffer, off + c * 4)
                                   : BitConverter.ToInt16(e.Buffer, off + c * 2) / 32768f;
                }
                _ring[_filled++] = sum / channels;

                if (_filled == _ring.Length)
                {
                    _filled = 0;
                    var levels = SpectrumAnalyser.Compute(_ring, fmt.SampleRate);
                    for (int b = 0; b < levels.Length; b++)
                        // Up at once, down gradually: bars that follow the music rather than flicker with it.
                        _smooth[b] = levels[b] > _smooth[b] ? levels[b] : _smooth[b] * 0.82f + levels[b] * 0.18f;
                    EmitLocked();
                }
            }
        }
    }

    private void Decay()
    {
        lock (_gate)
        {
            if (_capture is null) return;
            if (Environment.TickCount64 - _lastDataMs < 150) return;
            bool any = false;
            for (int b = 0; b < _smooth.Length; b++) { _smooth[b] *= 0.8f; if (_smooth[b] < 1) _smooth[b] = 0; else any = true; }
            if (!any && _silentSent) return;
            _silentSent = !any;
            EmitLocked(force: true);
        }
    }

    private void EmitLocked(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastEmitMs < 50) return;
        _lastEmitMs = now;
        var bars = new int[_smooth.Length];
        for (int i = 0; i < bars.Length; i++) bars[i] = (int)Math.Round(_smooth[i]);
        // Outside the lock would be tidier, but this runs on the audio thread and the handler only queues a send.
        Frame?.Invoke(bars);
    }

    public void Dispose() { Stop(); try { _enumerator.Dispose(); } catch { } }
}
