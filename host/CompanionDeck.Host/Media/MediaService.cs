using CompanionDeck.Host.Assets;
using CompanionDeck.Host.Protocol;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace CompanionDeck.Host.Media;

/// <summary>
/// Reads and drives Windows' global media sessions (GSMTC). This is the API the previous attempt
/// at this project missed: it went with blind media-key presses, which is why metadata, artwork
/// and seeking had to be abandoned.
/// </summary>
public sealed class MediaService : IAsyncDisposable
{
    private readonly AssetCache _assets;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _sessions = new();
    private readonly Dictionary<string, MediaSessionInfo> _info = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised when anything a client would render has actually changed.</summary>
    public event Action? Changed;

    public bool Available { get; private set; }

    public MediaService(AssetCache assets) => _assets = assets;

    public async Task<bool> InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
            await RebuildAsync();
            Available = true;
            Log.Info($"GSMTC ready ({_sessions.Count} session(s))");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"GSMTC unavailable: {ex.Message}");
            return false;
        }
    }

    private async void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        try { await RebuildAsync(); } catch (Exception ex) { Log.Debug($"sessions changed: {ex.Message}"); }
    }

    private async Task RebuildAsync()
    {
        if (_manager is null) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var live = _manager.GetSessions();
            var liveIds = new HashSet<string>();

            foreach (var s in live)
            {
                var id = s.SourceAppUserModelId;
                if (string.IsNullOrEmpty(id)) continue;
                liveIds.Add(id);
                if (_sessions.ContainsKey(id)) continue;

                _sessions[id] = s;
                s.MediaPropertiesChanged += OnSessionChanged;
                s.PlaybackInfoChanged += OnPlaybackChanged;
                s.TimelinePropertiesChanged += OnTimelineChanged;
                await RefreshSessionAsync(id).ConfigureAwait(false);
            }

            foreach (var goneId in _sessions.Keys.Where(k => !liveIds.Contains(k)).ToList())
            {
                var s = _sessions[goneId];
                s.MediaPropertiesChanged -= OnSessionChanged;
                s.PlaybackInfoChanged -= OnPlaybackChanged;
                s.TimelinePropertiesChanged -= OnTimelineChanged;
                _sessions.Remove(goneId);
                _info.Remove(goneId);
            }
        }
        finally { _gate.Release(); }

        Changed?.Invoke();
    }

    private async void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs a)
        => await SafeRefresh(sender);

    private async void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs a)
        => await SafeRefresh(sender);

    private async void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs a)
        => await SafeRefresh(sender);

    private async Task SafeRefresh(GlobalSystemMediaTransportControlsSession sender)
    {
        try
        {
            await RefreshSessionAsync(sender.SourceAppUserModelId).ConfigureAwait(false);
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Debug($"media refresh: {ex.Message}"); }
    }

    private async Task RefreshSessionAsync(string id)
    {
        if (!_sessions.TryGetValue(id, out var s)) return;

        var info = new MediaSessionInfo { Id = id, AppName = FriendlyName(id) };

        try
        {
            var pb = s.GetPlaybackInfo();
            info.Status = pb.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "stopped",
                _ => "stopped",
            };
            var c = pb.Controls;
            info.CanPlay = c.IsPlayEnabled;
            info.CanPause = c.IsPauseEnabled;
            info.CanNext = c.IsNextEnabled;
            info.CanPrevious = c.IsPreviousEnabled;
            info.CanSeek = c.IsPlaybackPositionEnabled;
            info.Shuffle = pb.IsShuffleActive;
            info.Repeat = pb.AutoRepeatMode?.ToString().ToLowerInvariant();
        }
        catch (Exception ex) { Log.Debug($"playback info {id}: {ex.Message}"); }

        try
        {
            var tl = s.GetTimelineProperties();
            var dur = (tl.EndTime - tl.StartTime).TotalSeconds;
            if (dur > 0 && dur < 60 * 60 * 24)
            {
                info.DurationSec = Math.Round(dur, 2);
                info.PositionSec = Math.Round((tl.Position - tl.StartTime).TotalSeconds, 2);
            }
        }
        catch (Exception ex) { Log.Debug($"timeline {id}: {ex.Message}"); }

        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            info.Title = Blank(props.Title);
            info.Artist = Blank(props.Artist);
            info.Album = Blank(props.AlbumTitle);

            if (props.Thumbnail is not null)
            {
                var bytes = await ReadThumbnailAsync(props.Thumbnail).ConfigureAwait(false);
                if (bytes is { Length: > 0 }) info.ArtKey = _assets.Put(bytes);
            }
        }
        catch (Exception ex) { Log.Debug($"media props {id}: {ex.Message}"); }

        _info[id] = info;
    }

    /// <summary>
    /// Decodes the session thumbnail and re-encodes it at a size that suits a 600x1024
    /// panel. Spotify hands out 640px PNGs; shrinking them keeps both the link and the
    /// tablet's very limited heap comfortable.
    /// </summary>
    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference reference)
    {
        const uint target = 384;
        try
        {
            using var src = await reference.OpenReadAsync();
            if (src.Size == 0) return null;

            var decoder = await BitmapDecoder.CreateAsync(src);

            uint w = decoder.PixelWidth, h = decoder.PixelHeight;
            double scale = Math.Min(1.0, target / (double)Math.Max(w, h));
            uint outW = Math.Max(1, (uint)Math.Round(w * scale));
            uint outH = Math.Max(1, (uint)Math.Round(h * scale));

            using var dest = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(dest, decoder);
            encoder.BitmapTransform.ScaledWidth = outW;
            encoder.BitmapTransform.ScaledHeight = outH;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();

            dest.Seek(0);
            var buffer = new Windows.Storage.Streams.Buffer((uint)dest.Size);
            await dest.ReadAsync(buffer, (uint)dest.Size, InputStreamOptions.None);

            var bytes = new byte[buffer.Length];
            using var reader = DataReader.FromBuffer(buffer);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            Log.Debug($"thumbnail decode failed: {ex.Message}");
            return null;
        }
    }

    public MediaState Snapshot()
    {
        // Having a title outranks the app's name. Plenty of applications register a media session
        // with nothing in it - Windows Sound Recorder is the one that caused this, but any
        // recorder, notifier or embedded player will do it.
        static bool HasMeta(MediaSessionInfo i) => !string.IsNullOrWhiteSpace(i.Title);

        var list = _info.Values
            .OrderByDescending(i => i.Status == "playing")
            .ThenByDescending(HasMeta)
            .ThenBy(i => i.AppName)
            .ToList();

        return new MediaState
        {
            Sessions = list,
            // Something playing that we can actually describe, then anything playing, then
            // anything we can describe, then whatever is left.
            ActiveId = list.FirstOrDefault(i => i.Status == "playing" && HasMeta(i))?.Id
                    ?? list.FirstOrDefault(i => i.Status == "playing")?.Id
                    ?? list.FirstOrDefault(HasMeta)?.Id
                    ?? list.FirstOrDefault()?.Id,
        };
    }


    public async Task<bool> ControlAsync(string sessionId, string action, double? value)
    {
        GlobalSystemMediaTransportControlsSession? s;
        if (string.IsNullOrEmpty(sessionId))
        {
            var activeId = Snapshot().ActiveId;
            s = activeId is not null && _sessions.TryGetValue(activeId, out var a) ? a : null;
        }
        else _sessions.TryGetValue(sessionId, out s);

        if (s is null) return false;

        try
        {
            return action switch
            {
                "play" => await s.TryPlayAsync(),
                "pause" => await s.TryPauseAsync(),
                "playpause" => await s.TryTogglePlayPauseAsync(),
                "next" => await s.TrySkipNextAsync(),
                "previous" => await s.TrySkipPreviousAsync(),
                "stop" => await s.TryStopAsync(),
                "shuffle" => await s.TryChangeShuffleActiveAsync(value is null or 0 ? false : true),
                "seek" => value is not null && await s.TryChangePlaybackPositionAsync((long)(value.Value * 10_000_000)),
                "repeat" => await s.TryChangeAutoRepeatModeAsync((int)(value ?? 0) switch
                {
                    1 => Windows.Media.MediaPlaybackAutoRepeatMode.List,
                    2 => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                    _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
                }),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"media control '{action}' failed: {ex.Message}");
            return false;
        }
    }


    /// <summary>
    /// Best-effort mapping from a media session to the process that owns its audio, so the media
    /// card can show that application's own level. GSMTC identifies sessions by AppUserModelId,
    /// which for desktop apps is usually just the executable ("Spotify.exe", "chrome.exe") and
    /// lines up with the process name the audio mixer uses.
    /// </summary>
    public static string ProcessNameFor(string appUserModelId)
    {
        if (string.IsNullOrEmpty(appUserModelId)) return "";

        var id = appUserModelId;
        int bang = id.IndexOf('!');
        if (bang > 0) id = id[..bang];

        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];

        int slash = id.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) id = id[(slash + 1)..];

        return id;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Turns an AppUserModelId into something a person recognises.</summary>
    private static string FriendlyName(string aumid)
    {
        var id = aumid;
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            id = id[..^4];

        int bang = id.IndexOf('!');
        if (bang > 0) id = id[..bang];

        int dot = id.LastIndexOf('.');
        if (dot > 0 && dot < id.Length - 1) id = id[(dot + 1)..];

        return id switch
        {
            "Spotify" => "Spotify",
            "chrome" => "Chrome",
            "brave" => "Brave",
            "msedge" => "Edge",
            "firefox" => "Firefox",
            "vlc" => "VLC",
            _ => id.Length > 0 ? char.ToUpperInvariant(id[0]) + id[1..] : aumid,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var s in _sessions.Values)
            {
                s.MediaPropertiesChanged -= OnSessionChanged;
                s.PlaybackInfoChanged -= OnPlaybackChanged;
                s.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            _sessions.Clear();
            if (_manager is not null) _manager.SessionsChanged -= OnSessionsChanged;
        }
        finally { _gate.Release(); }
    }
}
