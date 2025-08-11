using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Control;
using System.Runtime.InteropServices.WindowsRuntime;

namespace NativeMediaSession;

public class GsmtcService : IDisposable
{
    private readonly CallbackDispatcher _dispatcher;
    private readonly MetadataCache _cache;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private FullMediaState? _last;

    public event Action<FullMediaState>? MetadataAvailable;

    public GsmtcService(CallbackDispatcher dispatcher, MetadataCache cache)
    {
        _dispatcher = dispatcher;
        _cache = cache;
    }

    public int Start()
    {
        try
        {
            _manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
            _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
            SubscribeSession();
            return 0;
        }
        catch
        {
            Stop();
            return -5; // gsmtc_error
        }
    }

    private void Manager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        SubscribeSession();
    }

    private void SubscribeSession()
    {
        var session = _manager?.GetCurrentSession();
        if (session == null)
            return;

        if (_session != null)
        {
            _session.MediaPropertiesChanged -= MediaChanged;
            _session.PlaybackInfoChanged -= PlaybackChanged;
            _session.TimelinePropertiesChanged -= TimelineChanged;
        }

        _session = session;
        _session.MediaPropertiesChanged += MediaChanged;
        _session.PlaybackInfoChanged += PlaybackChanged;
        _session.TimelinePropertiesChanged += TimelineChanged;
        HandleFullStateChange();
    }

    private void MediaChanged(GlobalSystemMediaTransportControlsSession session, MediaPropertiesChangedEventArgs args) => HandleFullStateChange();
    private void PlaybackChanged(GlobalSystemMediaTransportControlsSession session, PlaybackInfoChangedEventArgs args) => HandleFullStateChange();
    private void TimelineChanged(GlobalSystemMediaTransportControlsSession session, TimelinePropertiesChangedEventArgs args) => HandleFullStateChange();

    private void HandleFullStateChange()
    {
        var session = _session;
        if (session == null) return;
        try
        {
            var state = CreateFullMediaState(session).GetAwaiter().GetResult();
            if (_last != null && state.Equals(_last)) return;
            _last = state;
            _cache.Update(state);
            _dispatcher.Enqueue(() => MetadataAvailable?.Invoke(state));
        }
        catch
        {
            // ignore
        }
    }

    private async Task<FullMediaState> CreateFullMediaState(GlobalSystemMediaTransportControlsSession session)
    {
        var props = await session.TryGetMediaPropertiesAsync();
        var playback = session.GetPlaybackInfo().PlaybackStatus.ToString();
        var timeline = session.GetTimelineProperties();
        double duration = Math.Round(timeline.EndTime.TotalSeconds, 2);
        double position = Math.Round(timeline.Position.TotalSeconds, 2);

        string? art = null;
        try
        {
            var thumb = props.Thumbnail;
            if (thumb != null)
            {
                using var ms = new MemoryStream();
                using var stream = await thumb.OpenReadAsync();
                await stream.AsStreamForRead().CopyToAsync(ms);
                art = Convert.ToBase64String(ms.ToArray());
            }
        }
        catch
        {
        }

        return new FullMediaState
        {
            title = props.Title,
            artist = props.Artist,
            albumTitle = props.AlbumTitle,
            albumArtBase64 = art,
            status = playback,
            duration = duration,
            position = position
        };
    }

    public void Stop()
    {
        try
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= MediaChanged;
                _session.PlaybackInfoChanged -= PlaybackChanged;
                _session.TimelinePropertiesChanged -= TimelineChanged;
                _session = null;
            }
            if (_manager != null)
            {
                _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
                _manager = null;
            }
        }
        catch
        {
        }
    }

    public void Dispose() => Stop();
}

