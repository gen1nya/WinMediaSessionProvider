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

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic),
            WriteIndented = false
        };

        private FullMediaState _lastFullState;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Запуск WebSocket-сервера
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add("http://localhost:5001/ws/");
            _httpListener.Start();
            _ = AcceptWebSocketClientsAsync(stoppingToken);

            // Запуск рассылки сообщений
            _ = ProcessMessageQueueAsync(stoppingToken);

            // Инициализация менеджера медиа-сессий
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

            // Подписка на смену текущей сессии
            _sessionManager.CurrentSessionChanged += async (_, __) =>
            {
                _logger.LogInformation("CurrentSessionChanged event");
                await SubscribeToSessionAsync(stoppingToken);
            };

            // Первичная подписка на уже активную сессию
            await SubscribeToSessionAsync(stoppingToken);

            // Поддержание жизненного цикла
            await Task.Delay(Timeout.Infinite, stoppingToken);
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

            // Подписка на события
            session.MediaPropertiesChanged += async (_, __) =>
            {
                _logger.LogInformation("MediaPropertiesChanged event");
                await HandleFullStateChangeAsync(token);
            };
            session.PlaybackInfoChanged += async (_, __) =>
            {
                _logger.LogInformation("PlaybackInfoChanged event");
                await HandleFullStateChangeAsync(token);
            };
            session.TimelinePropertiesChanged += async (_, __) =>
            {
                _logger.LogInformation("TimelinePropertiesChanged event");
                await HandleFullStateChangeAsync(token);
            };

            // Первоначальная отправка состояния
            await HandleFullStateChangeAsync(token);
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
                    var context = await _httpListener.GetContextAsync();
                    if (!context.Request.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        continue;
                    }

                    var wsContext = await context.AcceptWebSocketAsync(null);
                    var ws = wsContext.WebSocket;
                    lock (_clientsLock) _clients.Add(ws);
                    _logger.LogInformation("WebSocket client connected");

                    // Отправка последнего состояния клиенту
                    if (_lastFullState != null && ws.State == WebSocketState.Open)
                    {
                        var metaEnvelope = new { type = "metadata", data = _lastFullState };
                        var metaJson = JsonSerializer.Serialize(metaEnvelope, _jsonOptions);
                        await ws.SendAsync(Encoding.UTF8.GetBytes(metaJson), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException || ex is ObjectDisposedException)
                {
                    _logger.LogInformation("WebSocket listener stopped.");
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
            while (await reader.WaitToReadAsync(token))
            {
                while (reader.TryRead(out var message))
                {
                    await BroadcastAsync(message);
                }
            }
        }

        private async Task BroadcastAsync(string message)
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            var dead = new List<WebSocket>();
            List<WebSocket> clientsSnapshot;
            lock (_clientsLock) clientsSnapshot = _clients.Where(ws => ws.State == WebSocketState.Open).ToList();

            foreach (var ws in clientsSnapshot)
            {
                try
                {
                    await ws.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (WebSocketException ex)
                {
                    _logger.LogWarning(ex, "WebSocket send error, removing client");
                    dead.Add(ws);
                }
            }

            if (dead.Any())
            {
                lock (_clientsLock)
                {
                    foreach (var ws in dead) _clients.Remove(ws);
                }
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
            _httpListener?.Close();
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
