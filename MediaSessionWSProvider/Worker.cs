using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.Channels;
using Windows.Media.Control;

namespace MediaSessionWSProvider
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private HttpListener _httpListener;
        private GlobalSystemMediaTransportControlsSessionManager _sessionManager;
        private readonly List<WebSocket> _clients = new();
        private readonly Channel<string> _messageChannel = Channel.CreateUnbounded<string>();
        private readonly object _clientsLock = new();
        private readonly FftService _fftService;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic),
            WriteIndented = false
        };

        private FullMediaState _lastFullState;
        private CancellationTokenSource _internalCts = new();

        public Worker(ILogger<Worker> logger, FftService fftService)
        {
            _logger = logger;
            _fftService = fftService;
            _fftService.SpectrumAvailable += OnSpectrum;
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("StopAsync called. Cleaning up...");
            _internalCts.Cancel();
            
            try
            {
                _httpListener?.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при закрытии HttpListener");
            }

            List<WebSocket> clientsSnapshot;
            lock (_clientsLock)
            {
                clientsSnapshot = _clients.ToList();
                _clients.Clear();
            }

            if (clientsSnapshot.Any())
            {
                var closeTasks = clientsSnapshot
                    .Select(ws => CloseClientWithTimeoutAsync(ws, TimeSpan.FromSeconds(5)))
                    .ToArray();
                Task.WaitAll(closeTasks); // ждём не дольше 5 секунд
            }
            
            _messageChannel.Writer.Complete();
            _logger.LogInformation("Cleanup finished. Возвращаю управление из StopAsync.");
            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        { 
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _internalCts.Token);
            var linkedToken = linkedCts.Token;
            
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add("http://localhost:5001/ws/");
            _httpListener.Start();
            _ = AcceptWebSocketClientsAsync(linkedToken);
            _ = ProcessMessageQueueAsync(linkedToken);
            
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
                _logger.LogInformation("MediaPropertiesChanged event");
                await HandleFullStateChangeAsync(token);
            }
            async void PlaybackInfoChangedHandler(GlobalSystemMediaTransportControlsSession mSession, PlaybackInfoChangedEventArgs args)
            {
                _logger.LogInformation("PlaybackInfoChanged event");
                await HandleFullStateChangeAsync(token);
            }
            async void TimelinePropertiesChangedHandler(GlobalSystemMediaTransportControlsSession mSession, TimelinePropertiesChangedEventArgs args)
            {
                _logger.LogInformation("TimelinePropertiesChanged event");
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

                var envelope = new { type = "metadata", data = fullState };
                var json = JsonSerializer.Serialize(envelope, _jsonOptions);
                await _messageChannel.Writer.WriteAsync(json, token);

                _logger.LogInformation("Broadcasted metadata: {Title} - {Artist}", fullState.title, fullState.artist);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling full state");
            }
        }

        private async Task AcceptWebSocketClientsAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync().ConfigureAwait(false);
                    if (!context.Request.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        continue;
                    }

                    var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                    var ws = wsContext.WebSocket;
                    lock (_clientsLock) _clients.Add(ws);
                    _logger.LogInformation("WebSocket client connected");

                    // Отправка последнего состояния сразу же новому клиенту
                    if (_lastFullState != null && ws.State == WebSocketState.Open)
                    {
                        var metaEnvelope = new { type = "metadata", data = _lastFullState };
                        var metaJson = JsonSerializer.Serialize(metaEnvelope, _jsonOptions);
                        var buffer = Encoding.UTF8.GetBytes(metaJson);

                        var ok = await TrySendWithTimeoutAsync(ws, buffer, token, _sendTimeout).ConfigureAwait(false);
                        if (!ok)
                        {
                            _logger.LogWarning("Failed to send initial metadata, removing client");
                            lock (_clientsLock) _clients.Remove(ws);
                            try { ws.Abort(); ws.Dispose(); } catch { }
                            continue;
                        }
                    }
                }
                catch (HttpListenerException)
                {
                    _logger.LogInformation("WebSocket listener stopped (HttpListenerException).");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    _logger.LogInformation("HttpListener disposed, прекращаем прием клиентов.");
                    break;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Отмена AcceptWebSocketClientsAsync по токену.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error accepting WebSocket client");
                }
            }
        }

        private async Task ProcessMessageQueueAsync(CancellationToken token)
        {
            var reader = _messageChannel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var message))
                    {
                        await BroadcastAsync(message, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("ProcessMessageQueueAsync отменен по токену.");
            }
        }

        // Timeout for WebSocket send operations to avoid hanging on dead clients
        private static readonly TimeSpan _sendTimeout = TimeSpan.FromSeconds(10);

        private async Task BroadcastAsync(string message, CancellationToken token)
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            var dead = new List<WebSocket>();
            List<WebSocket> clientsSnapshot;
            lock (_clientsLock) clientsSnapshot = _clients.Where(ws => ws.State == WebSocketState.Open).ToList();

            foreach (var ws in clientsSnapshot)
            {
                if (ws.State != WebSocketState.Open)
                {
                    dead.Add(ws);
                    continue;
                }

                var sendOk = await TrySendWithTimeoutAsync(ws, buffer, token, _sendTimeout);
                if (!sendOk)
                {
                    _logger.LogWarning("WebSocket send timed out, removing client");
                    dead.Add(ws);
                }
            }

            if (dead.Any())
            {
                lock (_clientsLock)
                {
                    foreach (var ws in dead)
                    {
                        _clients.Remove(ws);
                        try
                        {
                            ws.Abort();
                            ws.Dispose();
                        }
                        catch { /* ignore */ }
                    }
                }
            }
        }

        private static async Task<bool> TrySendWithTimeoutAsync(WebSocket ws, byte[] buffer, CancellationToken globalToken, TimeSpan timeout)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(globalToken);
            cts.CancelAfter(timeout);
            try
            {
                await ws.SendAsync(buffer, WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!globalToken.IsCancellationRequested)
            {
                ws.Abort();
                return false;
            }
            catch (WebSocketException)
            {
                return false;
            }
        }

        private async void OnSpectrum(float[] data)
        {
            List<WebSocket> clientsSnapshot;
            lock (_clientsLock) clientsSnapshot = _clients.Where(ws => ws.State == WebSocketState.Open).ToList();
            if (!clientsSnapshot.Any()) return;

            try
            {
                var envelope = new { type = "fft", data };
                var json = JsonSerializer.Serialize(envelope, _jsonOptions);
                await _messageChannel.Writer.WriteAsync(json);
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
        
        private async Task CloseClientWithTimeoutAsync(WebSocket ws, TimeSpan timeout)
        {
            if (ws.State != WebSocketState.Open && ws.State != WebSocketState.CloseReceived)
            {
                ws.Abort();
                ws.Dispose();
                return;
            }

            using var cts = new CancellationTokenSource(timeout);

            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Service stopping", cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("CloseAsync для WebSocket-клиента превысил таймаут {Timeout}. Принудительно убиваем.", timeout);
                ws.Abort();
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning(ex, "WebSocketException при попытке CloseAsync, принудительно Abort.");
                ws.Abort();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Иная ошибка при CloseAsync, Abort.");
                ws.Abort();
            }
            finally
            {
                ws.Dispose();
            }
        }

        public override void Dispose()
        {
            try
            {
                _httpListener?.Close();
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
