using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using WebSocketSharp.Server;
using Windows.Media.Control;
// The Worker manages websocket broadcasting and keeps track of the latest metadata

namespace MediaSessionWSProvider
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private WebSocketServer _wsServer;
        private GlobalSystemMediaTransportControlsSessionManager _sessionManager;
        private readonly FftService _fftService;
        private readonly MetadataCache _metadataCache;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic),
            WriteIndented = false
        };

        private FullMediaState? _lastFullState;
        private CancellationTokenSource _internalCts = new();

        public Worker(ILogger<Worker> logger, FftService fftService, MetadataCache metadataCache)
        {
            _logger = logger;
            _fftService = fftService;
            _metadataCache = metadataCache;
            _fftService.SpectrumAvailable += OnSpectrum;
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("StopAsync called. Cleaning up...");
            _internalCts.Cancel();

            try
            {
                _wsServer?.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при остановке WebSocketServer");
            }

            _logger.LogInformation("Cleanup finished. Возвращаю управление из StopAsync.");
            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        { 
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _internalCts.Token);
            var linkedToken = linkedCts.Token;

            _wsServer = new WebSocketServer("ws://localhost:5001");
            _wsServer.AddWebSocketService("/ws", () => new MediaBroadcast(_metadataCache));
            _wsServer.KeepClean   = true;
            _wsServer.WaitTime    = TimeSpan.FromSeconds(10);
            _wsServer.Start();
            
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _sessionManager.CurrentSessionChanged += async (_, __) =>
            {
                _logger.LogInformation("CurrentSessionChanged event");
                await SubscribeToSessionAsync(linkedToken);
            };
            
            await SubscribeToSessionAsync(linkedToken);

            try
            {
                await Task.Delay(Timeout.Infinite, linkedToken);
            }
            catch (OperationCanceledException)
            {
                
            }
        }

        private async Task SubscribeToSessionAsync(CancellationToken token)
        {
            var session = _sessionManager.GetCurrentSession();
            if (session == null)
            {
                _logger.LogWarning("No active media session found.");
                return;
            }

            _logger.LogInformation("Subscribed to session: {AppId}", session.SourceAppUserModelId);
            
            session.MediaPropertiesChanged -= MediaPropertiesChangedHandler;
            session.PlaybackInfoChanged -= PlaybackInfoChangedHandler;
            session.TimelinePropertiesChanged -= TimelinePropertiesChangedHandler;
            
            session.MediaPropertiesChanged += MediaPropertiesChangedHandler;
            session.PlaybackInfoChanged += PlaybackInfoChangedHandler;
            session.TimelinePropertiesChanged += TimelinePropertiesChangedHandler;

            // Первоначальная отправка состояния
            await HandleFullStateChangeAsync(token);

            async void MediaPropertiesChangedHandler(GlobalSystemMediaTransportControlsSession mSession, MediaPropertiesChangedEventArgs args)
            {
                await HandleFullStateChangeAsync(token);
            }
            async void PlaybackInfoChangedHandler(GlobalSystemMediaTransportControlsSession mSession, PlaybackInfoChangedEventArgs args)
            {
                await HandleFullStateChangeAsync(token);
            }
            async void TimelinePropertiesChangedHandler(GlobalSystemMediaTransportControlsSession mSession, TimelinePropertiesChangedEventArgs args)
            {
                await HandleFullStateChangeAsync(token);
            }
        }

        private async Task HandleFullStateChangeAsync(CancellationToken token)
        {
            var session = _sessionManager.GetCurrentSession();
            if (session == null) return;

            try
            {
                var fullState = await CreateFullMediaStateAsync(session);
                if (_lastFullState != null && fullState.Equals(_lastFullState))
                {
                    _logger.LogTrace("Full state unchanged, skipping.");
                    return;
                }
                _lastFullState = fullState;
                _metadataCache.Update(fullState);

                var envelope = new { type = "metadata", data = fullState };
                var json = JsonSerializer.Serialize(envelope, _jsonOptions);
                _wsServer.WebSocketServices["/ws"].Sessions.Broadcast(json);

                _logger.LogInformation("Broadcasted metadata: {Title} - {Artist}", fullState.title, fullState.artist);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling full state");
            }
        }

        private void OnSpectrum(float[] data)
        {
            try
            {
                var envelope = new { type = "fft", data };
                var json = JsonSerializer.Serialize(envelope, _jsonOptions);
                _wsServer.WebSocketServices["/ws"].Sessions.Broadcast(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending FFT data");
            }
        }

        private async Task<FullMediaState> CreateFullMediaStateAsync(GlobalSystemMediaTransportControlsSession session)
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading album art");
            }

            return new FullMediaState
            {
                title = props.Title,
                artist = props.Artist,
                albumTitle = props.AlbumTitle,
                albumArtBase64 = art,
                status = playback,
                duration = duration,
                position = position,
            };
        }
        
        public override void Dispose()
        {
            try
            {
                _wsServer?.Stop();
            }
            catch { }
            base.Dispose();
        }
    }

    public record FullMediaState
    {
        public string title { get; init; }
        public string artist { get; init; }
        public string albumTitle { get; init; }
        public string? albumArtBase64 { get; init; }
        public string status { get; init; }
        public double duration { get; init; }
        public double position { get; init; }
    }
}
