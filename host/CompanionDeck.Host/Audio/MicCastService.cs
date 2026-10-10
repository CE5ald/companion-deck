using CompanionDeck.Host.Protocol;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CompanionDeck.Host.Audio;

/// <summary>
/// Sends what the laptop is playing - and optionally the microphone alongside it - into a virtual
/// cable, so that whatever the person is talking on picks it up as their input. There is no way
/// to inject audio into a real microphone.
/// </summary>
public sealed class MicCastService : IDisposable
{
    /// <summary>Everything converges here: the one format the mixer runs at.</summary>
    private static WaveFormat MixFormat => Audio.MixFormat.Format;

    /// <summary>
    /// How far the capture buffers may run ahead before they are dropped back. Two endpoints
    /// clocked by different hardware drift apart, so a buffer between them grows without bound -
    /// slowly, but it grows, and every millisecond of it is added to the delay before friends
    /// hear you.
    /// </summary>
    private const int MaxBufferedMs = 150;

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();

    private WasapiLoopbackCapture? _loopback;
    private WasapiCapture? _mic;
    private WasapiOut? _out;
    private SyncMixer? _mixer;

    private BufferedWaveProvider? _loopbackBuf;
    private BufferedWaveProvider? _micBuf;
    private SmoothVolume? _loopbackVol;
    private VolumeSampleProvider? _micVol;

    /// <summary>How far desktop audio is pulled down while a soundboard clip is playing.</summary>
    private const float DuckTo = 0.25f;
    private int _ducking;

    /// <summary>The format anything injected into the cast must already be in.</summary>
    public static WaveFormat CastFormat => MixFormat;

    // Held for the life of the graph: NAudio keeps using the MMDevice it was built with, so
    // these cannot be disposed at the end of Arm - but they must be disposed on teardown.
    private MMDevice? _renderDevice;
    private MMDevice? _micDevice;
    private MMDevice? _cableRenderDevice;

    private string? _restoreCaptureId;
    private string? _micName;
    private volatile bool _armed;
    private volatile bool _stopping;

    /// <summary>Peak levels since the last read, so the tablet can show the cast is live.</summary>
    private float _peakDesktop;
    private float _peakMic;

    public bool Armed => _armed;
    public string? MicName => _micName;
    public string? LastError { get; private set; }

    /// <summary>Levels as percentages, 0-100. Reading resets the peak.</summary>
    public (int desktop, int mic) ReadPeaks()
    {
        float d = Interlocked.Exchange(ref _peakDesktop, 0f);
        float m = Interlocked.Exchange(ref _peakMic, 0f);
        return ((int)Math.Round(Math.Clamp(d, 0f, 1f) * 100), (int)Math.Round(Math.Clamp(m, 0f, 1f) * 100));
    }

    // Gains, 0-100. Desktop sits below the microphone by default: the point is to let people
    // hear what you are hearing, not to bury your voice under a game.
    private int _desktopGain = 55;
    private int _micGain = 100;
    private bool _micEnabled = true;

    public int DesktopGain => _desktopGain;
    public int MicGain => _micGain;
    public bool MicEnabled => _micEnabled;

    public void SetDesktopGain(int percent)
    {
        _desktopGain = Math.Clamp(percent, 0, 100);
        ApplyDesktopGain();
    }

    private void ApplyDesktopGain()
    {
        if (_loopbackVol is not { } v) return;
        float g = _desktopGain / 100f;
        v.Target = _ducking > 0 ? g * DuckTo : g;
    }

    /// <summary>
    /// Pulls desktop audio down while a clip plays, and lets it back up afterwards.
    ///
    /// Counted rather than boolean: clips overlap, and a plain flag would let the first one
    /// to finish restore the volume underneath the ones still playing.
    /// </summary>
    public void PushDuck()
    {
        Interlocked.Increment(ref _ducking);
        ApplyDesktopGain();
    }

    public void PopDuck()
    {
        if (Interlocked.Decrement(ref _ducking) < 0) Interlocked.Exchange(ref _ducking, 0);
        ApplyDesktopGain();
    }

    /// <summary>
    /// Adds a source to the outgoing mix. Returns false when the cast is not armed, which
    /// the soundboard treats as "this clip is for my ears only" rather than as an error -
    /// firing a pad with nobody listening should still play it locally.
    /// </summary>
    public bool TryAddToCast(ISampleProvider source)
    {
        lock (_gate)
        {
            if (!_armed || _mixer is null) return false;
            if (!source.WaveFormat.Equals(MixFormat)) return false;
            _mixer.Add(source);
            return true;
        }
    }

    public void RemoveFromCast(ISampleProvider source)
    {
        lock (_gate)
        {
            try { _mixer?.Remove(source); } catch { }
        }
    }

    public void SetMicGain(int percent)
    {
        _micGain = Math.Clamp(percent, 0, 100);
        ApplyMicGain();
    }

    public void SetMicEnabled(bool on)
    {
        _micEnabled = on;
        ApplyMicGain();
    }

    private void ApplyMicGain()
    {
        if (_micVol is { } v) v.Volume = _micEnabled ? _micGain / 100f : 0f;
    }

    // Chosen sources. Null means "whatever the machine is using", which is the right default:
    // plug in a headset and the cast follows it without being reconfigured. An explicit id is
    // for the case that default gets in the way - tapping the speakers while listening on a
    // headset, or mixing a proper microphone rather than the one in the lid.
    private string? _renderId;
    private string? _captureId;

    public string? RenderId => _renderId;
    public string? CaptureId => _captureId;

    /// <summary>
    /// Picks which output is tapped and which microphone is mixed. Changing either while live
    /// rebuilds the graph, because the devices are bound when it is constructed.
    /// </summary>
    public bool SetSource(string kind, string? deviceId)
    {
        if (deviceId is { Length: 0 }) deviceId = null;
        bool render = kind == "render";
        if (!render && kind != "capture") return false;

        lock (_gate)
        {
            if (render) { if (_renderId == deviceId) return true; _renderId = deviceId; }
            else { if (_captureId == deviceId) return true; _captureId = deviceId; }
        }

        if (!_armed) return true;
        Log.Info($"cast source changed ({kind}); rebuilding");
        Disarm();
        return Arm();
    }

    /// <summary>The endpoints the tablet may choose between, the cable itself excluded.</summary>
    public List<AudioDeviceInfo> ListDevices(bool render)
    {
        var flow = render ? DataFlow.Render : DataFlow.Capture;
        var chosen = render ? _renderId : _captureId;
        var list = new List<AudioDeviceInfo>();
        try
        {
            string? cableId = FindCableId(flow);
            string? defId = null;
            try
            {
                using var def = _enumerator.GetDefaultAudioEndpoint(flow, render ? Role.Console : Role.Communications);
                defId = def.ID;
            }
            catch { }

            foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                // Offering the cable here would only ever produce a feedback loop.
                if (d.ID == cableId) { d.Dispose(); continue; }
                list.Add(new AudioDeviceInfo
                {
                    Id = d.ID,
                    Name = d.FriendlyName,
                    IsDefault = chosen is null ? d.ID == defId : d.ID == chosen,
                });
                d.Dispose();
            }
        }
        catch (Exception ex) { Log.Debug($"cast device list: {ex.Message}"); }
        return list;
    }

    private string? _boundRenderId;
    private string? _boundCaptureId;

    /// <summary>
    /// Rebuilds the graph if the endpoints have moved since arming. Devices are bound when the
    /// graph is constructed, so with the sources left on "default" - which is the normal case -
    /// plugging in a headset left the cast tapping an output nobody was listening to and mixing a
    /// microphone nobody was talking into.
    /// </summary>
    public void CheckDevices()
    {
        if (!_armed) return;
        try
        {
            string? wantRender, wantCapture;
            using (var r = Pick(DataFlow.Render, _renderId, Role.Console)) wantRender = r.ID;
            using (var c = Pick(DataFlow.Capture, _captureId, Role.Communications)) wantCapture = c.ID;

            // The capture default is the cable while armed - that is this service's own doing
            // - so only an explicitly chosen microphone is worth comparing.
            bool renderMoved = wantRender != _boundRenderId;
            bool captureMoved = _captureId is not null && wantCapture != _boundCaptureId;
            if (!renderMoved && !captureMoved) return;

            Log.Info("cast: audio endpoints moved; rebuilding");
            Disarm();
            Arm();
        }
        catch (Exception ex) { Log.Debug($"cast device check: {ex.Message}"); }
    }

    /// <summary>Where soundboard clips should be heard: the chosen output, or the default.</summary>
    public MMDevice? ResolveRender()
    {
        try
        {
            if (_renderId is { } id)
            {
                foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (d.ID == id) return d;
                    d.Dispose();
                }
            }
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch { return null; }
    }

    private MMDevice Pick(DataFlow flow, string? id, Role role)
    {
        if (id is not null)
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                if (d.ID == id) return d;
                d.Dispose();
            }
            Log.Info($"cast: chosen {flow} device is gone; falling back to the default");
        }
        return _enumerator.GetDefaultAudioEndpoint(flow, role);
    }

    // Whether the cable exists changes only when a driver is installed or removed, which is to
    // say almost never - but this property is read on every publish, and the Voice page publishes
    // eight times a second. It used to enumerate every endpoint on both flows on each read and
    // drop the two MMDevice objects it found without disposing them.
    private DateTime _cableCheckedAt = DateTime.MinValue;
    private bool _cableAvailable;

    /// <summary>True when a VB-Audio cable is installed; without one this cannot work at all.</summary>
    public bool Available
    {
        get
        {
            if ((DateTime.UtcNow - _cableCheckedAt).TotalSeconds < 10) return _cableAvailable;
            _cableCheckedAt = DateTime.UtcNow;
            try { _cableAvailable = FindCableId(DataFlow.Render) is not null && FindCableId(DataFlow.Capture) is not null; }
            catch { _cableAvailable = false; }
            return _cableAvailable;
        }
    }

    /// <summary>
    /// The id of the cable's half for this flow. "CABLE Input" is a render endpoint you play
    /// into; "CABLE Output" is the capture endpoint applications record from.
    /// </summary>
    private string? FindCableId(DataFlow flow)
    {
        var want = flow == DataFlow.Render ? "CABLE Input" : "CABLE Output";
        foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (d)
                if (d.FriendlyName.Contains(want, StringComparison.OrdinalIgnoreCase)) return d.ID;
        }
        return null;
    }

    /// <summary>The cable device itself. The caller owns it and must dispose it.</summary>
    private MMDevice? FindCable(DataFlow flow)
    {
        var want = flow == DataFlow.Render ? "CABLE Input" : "CABLE Output";
        foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            if (d.FriendlyName.Contains(want, StringComparison.OrdinalIgnoreCase)) return d;
            d.Dispose();
        }
        return null;
    }

    /// <summary>
    /// Where the microphone to go back to is written down while the cast is armed. Disarming
    /// restores it from memory, but a crash or a force-kill never gets to run that path - and the
    /// machine would then be left recording from a cable with nothing on the other end, silently,
    /// with no clue as to why.
    /// </summary>
    private static string RestoreFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CompanionDeck", "cast-restore.txt");

    /// <summary>
    /// Puts the microphone back if a previous run died while armed. Only acts when the
    /// default input really is the cable, so it can never override a deliberate choice
    /// made between then and now.
    /// </summary>
    public void RecoverIfStranded()
    {
        try
        {
            if (!File.Exists(RestoreFile)) return;
            string id = File.ReadAllText(RestoreFile).Trim();
            File.Delete(RestoreFile);
            if (id.Length == 0) return;

            string? cableCaptureId = FindCableId(DataFlow.Capture);
            if (cableCaptureId is null) return;

            using var current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            if (current.ID != cableCaptureId)
            {
                Log.Debug("cast restore file found, but the default input is not the cable - leaving it alone");
                return;
            }

            if (PolicyConfig.SetDefault(id))
                Log.Warn("a previous run left the cast armed; the default microphone has been restored");
        }
        catch (Exception ex) { Log.Debug($"cast recovery: {ex.Message}"); }
    }

    private static void WriteRestore(string? id)
    {
        try
        {
            if (id is null) { if (File.Exists(RestoreFile)) File.Delete(RestoreFile); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(RestoreFile)!);
            File.WriteAllText(RestoreFile, id);
        }
        catch (Exception ex) { Log.Debug($"cast restore file: {ex.Message}"); }
    }

    public bool Arm()
    {
        lock (_gate)
        {
            if (_armed) return true;
            LastError = null;

            try
            {
                // These four are held for the life of the graph - NAudio keeps using the
                // MMDevice it was constructed with - so they are fields, released in
                // TeardownLocked rather than leaked on every arm.
                var cableRender = FindCable(DataFlow.Render);
                using var cableCapture = FindCable(DataFlow.Capture);
                if (cableRender is null || cableCapture is null)
                {
                    cableRender?.Dispose();
                    LastError = "VB-Audio Virtual Cable is not installed";
                    Log.Warn("mic cast: " + LastError);
                    return false;
                }
                _cableRenderDevice = cableRender;

                var render = Pick(DataFlow.Render, _renderId, Role.Console);
                var mic = Pick(DataFlow.Capture, _captureId, Role.Communications);
                _renderDevice = render;
                _micDevice = mic;

                // Refuse rather than build a loop. If the machine is already outputting to
                // the cable, or already recording from it, arming would feed this graph's
                // own output back into its input.
                if (render.ID == cableRender.ID)
                {
                    LastError = "default output is the cable itself";
                    Log.Warn("mic cast: " + LastError);
                    return false;
                }
                if (mic.ID == cableCapture.ID)
                {
                    LastError = "default input is already the cable - unplug the previous cast first";
                    Log.Warn("mic cast: " + LastError);
                    return false;
                }

                _micName = mic.FriendlyName;
                _boundRenderId = render.ID;
                _boundCaptureId = mic.ID;
                _mixer = new SyncMixer(MixFormat);

                // Desktop: a loopback tap on the output, which observes without rerouting -
                // the speakers carry on exactly as they were.
                _loopback = new WasapiLoopbackCapture(render);
                _loopbackBuf = NewBuffer(_loopback.WaveFormat);
                _loopback.DataAvailable += (_, e) =>
                {
                    Track(ref _peakDesktop, e.Buffer, e.BytesRecorded, _loopback!.WaveFormat);
                    Feed(_loopbackBuf, e);
                };
                _loopback.RecordingStopped += (_, e) => OnCaptureStopped("desktop", e);
                _loopbackVol = new SmoothVolume(Audio.MixFormat.Convert(_loopbackBuf), _desktopGain / 100f);
                _mixer.Add(_loopbackVol);

                // Microphone: resolved above by id, before the default is moved.
                _mic = new WasapiCapture(mic);
                _micBuf = NewBuffer(_mic.WaveFormat);
                _mic.DataAvailable += (_, e) =>
                {
                    Track(ref _peakMic, e.Buffer, e.BytesRecorded, _mic!.WaveFormat);
                    Feed(_micBuf, e);
                };
                _mic.RecordingStopped += (_, e) => OnCaptureStopped("microphone", e);
                _micVol = new VolumeSampleProvider(Audio.MixFormat.Convert(_micBuf)) { Volume = _micEnabled ? _micGain / 100f : 0f };
                _mixer.Add(_micVol);

                // Short latency: this is a live conversation, not playback.
                _out = new WasapiOut(cableRender, AudioClientShareMode.Shared, true, 60);
                _out.PlaybackStopped += (_, e) => OnCaptureStopped("cable output", e);
                _out.Init(_mixer);

                _loopback.StartRecording();
                _mic.StartRecording();
                _out.Play();

                // Last, and only once the graph is actually running: move the machine's
                // default input to the cable so anything set to "Default" follows along.
                _restoreCaptureId = mic.ID;
                WriteRestore(mic.ID);
                if (!PolicyConfig.SetDefault(cableCapture.ID))
                {
                    LastError = "could not make the cable the default input";
                    Log.Warn("mic cast: " + LastError);
                    TeardownLocked(restore: true);
                    return false;
                }

                _armed = true;
                Log.Info($"mic cast armed: {mic.FriendlyName} + desktop -> {cableRender.FriendlyName}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Warn($"mic cast arm failed: {ex.Message}");
                TeardownLocked(restore: true);
                return false;
            }
        }
    }

    public void Disarm()
    {
        lock (_gate)
        {
            if (!_armed && _out is null) return;
            TeardownLocked(restore: true);
            Log.Info("mic cast disarmed");
        }
    }

    /// <summary>
    /// Anything in the graph stopping means the cast is no longer carrying audio. Staying
    /// armed at that point would leave someone talking into a dead input without knowing,
    /// so the microphone goes back immediately rather than after a diagnosis.
    /// </summary>
    private void OnCaptureStopped(string what, StoppedEventArgs e)
    {
        if (_stopping || !_armed) return;
        LastError = e.Exception?.Message ?? $"{what} stopped";
        Log.Warn($"mic cast: {what} stopped ({LastError}) - restoring the microphone");
        // Off the callback thread: NAudio raises this from its own, and tearing the graph
        // down from inside its own event can deadlock.
        ThreadPool.QueueUserWorkItem(_ => Disarm());
    }

    private void TeardownLocked(bool restore)
    {
        _stopping = true;
        _armed = false;
        // Clips still playing when the cast goes down would otherwise leave the counter
        // raised, and the next arm would start already ducked.
        Interlocked.Exchange(ref _ducking, 0);
        try
        {
            try { _out?.Stop(); } catch { }
            try { _loopback?.StopRecording(); } catch { }
            try { _mic?.StopRecording(); } catch { }

            try { _out?.Dispose(); } catch { }
            try { _loopback?.Dispose(); } catch { }
            try { _mic?.Dispose(); } catch { }

            _out = null; _loopback = null; _mic = null;
            _mixer = null; _loopbackBuf = null; _micBuf = null;
            _loopbackVol = null; _micVol = null;
            _micName = null;
            _boundRenderId = null; _boundCaptureId = null;

            try { _renderDevice?.Dispose(); } catch { }
            try { _micDevice?.Dispose(); } catch { }
            try { _cableRenderDevice?.Dispose(); } catch { }
            _renderDevice = null; _micDevice = null; _cableRenderDevice = null;

            if (restore && _restoreCaptureId is { } id)
            {
                if (PolicyConfig.SetDefault(id)) Log.Info("default input restored");
                else Log.Warn("could not restore the default input - check the Sound panel");
            }
            _restoreCaptureId = null;
            WriteRestore(null);
        }
        finally { _stopping = false; }
    }

    private static BufferedWaveProvider NewBuffer(WaveFormat fmt) => new(fmt)
    {
        // Silence rather than starvation when a source is briefly late: the mixer must
        // always have something to read or the whole graph stutters.
        ReadFully = true,
        DiscardOnBufferOverflow = true,
        BufferDuration = TimeSpan.FromMilliseconds(600),
    };

    private static void Feed(BufferedWaveProvider? buf, WaveInEventArgs e)
    {
        if (buf is null) return;
        if (buf.BufferedDuration.TotalMilliseconds > MaxBufferedMs) buf.ClearBuffer();
        buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    /// <summary>Peak of this block, kept as a running maximum until the tablet reads it.</summary>
    private static void Track(ref float slot, byte[] buffer, int bytes, WaveFormat fmt)
    {
        if (fmt.Encoding != WaveFormatEncoding.IeeeFloat || bytes < 4) return;
        float peak = 0f;
        for (int i = 0; i + 4 <= bytes; i += 4)
        {
            float v = Math.Abs(BitConverter.ToSingle(buffer, i));
            if (v > peak) peak = v;
        }
        float cur = slot;
        if (peak > cur) Interlocked.Exchange(ref slot, peak);
    }

    public void Dispose()
    {
        // The default input must come back even if the agent is being torn down, otherwise
        // the machine is left recording from a cable nothing is feeding.
        lock (_gate) TeardownLocked(restore: true);
        try { _enumerator.Dispose(); } catch { }
    }
}
