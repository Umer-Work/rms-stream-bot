using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Microsoft.Extensions.Logging;
using System.IO;
using JWT.Algorithms;
using JWT.Builder;

namespace EchoBot.Bot
{
    public class WebSocketClient : IDisposable
    {
        private readonly ClientWebSocket _webSocket;
        private readonly Uri _serverUri;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly string _jwtSecret;
        private bool _isConnected;
        
        public event EventHandler ConnectionClosed;

        public WebSocketClient(string serverUrl, string jwtSecret, ILogger logger)
        {
            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            _serverUri = new Uri(serverUrl);
            _jwtSecret = jwtSecret;
            _logger = logger;
            _cancellationTokenSource = new CancellationTokenSource();
            _isConnected = false;
        }

        private string GenerateJwtToken()
        {
            try
            {
                var token = JwtBuilder.Create()
                    .WithAlgorithm(new HMACSHA256Algorithm())
                    .WithSecret(_jwtSecret)
                    .AddClaim("type", "teams-bot")
                    .AddClaim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    .AddClaim("exp", DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds())
                    .Encode();

                _logger.LogInformation("JWT token generated successfully");
                return token;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error generating JWT token: {ex.Message}");
                throw;
            }
        }

        public async Task ConnectAsync()
        {
            try
            {
                if (_webSocket.State != WebSocketState.Open)
                {
                    // Generate JWT token
                    var token = GenerateJwtToken();

                    // Add token to Authorization header
                    _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                    
                    _logger.LogInformation($"Connecting to WebSocket at {_serverUri}");
                    await _webSocket.ConnectAsync(_serverUri, _cancellationTokenSource.Token);
                    _isConnected = true;
                    _logger.LogInformation("WebSocket connected successfully");

                    // Start both monitoring and receiving
                    _ = MonitorConnectionStateAsync();
                    _ = StartReceivingAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"WebSocket connection error: {ex.Message}");
                _isConnected = false;
                OnConnectionClosed();
                throw;
            }
        }

        private async Task MonitorConnectionStateAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                if (_webSocket.State != WebSocketState.Open)
                {
                    _isConnected = false;
                    OnConnectionClosed();
                    break;
                }
                await Task.Delay(1000);
            }
        }

        private void OnConnectionClosed()
        {
            _logger.LogWarning("WebSocket connection closed");
            ConnectionClosed?.Invoke(this, EventArgs.Empty);
        }

        public async Task SendAudioDataAsync(byte[] audioData, string email, string displayName, long? speakStartTime = null, long? speakEndTime = null, long? timeSinceMeetingStart = null, string role = null)
        {
            if (!_isConnected || _webSocket.State != WebSocketState.Open)
            {
                _logger.LogWarning("Cannot send audio data - WebSocket is not connected");
                return;
            }

            try
            {
                var payload = new
                {
                    type = "audio",
                    email = email,
                    displayName = displayName,
                    buffer = Convert.ToBase64String(audioData),     
                    speakStartTime = speakStartTime,        
                    speakEndTime = speakEndTime,
                    timeSinceMeetingStart = timeSinceMeetingStart,  // Time in seconds since meeting started
                    role = role
                };

                var jsonString = System.Text.Json.JsonSerializer.Serialize(payload);
                var messageBytes = Encoding.UTF8.GetBytes(jsonString);

                await _webSocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    _cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error sending audio data: {ex.Message}");
                if (_webSocket.State != WebSocketState.Open)
                {
                    _isConnected = false;
                    OnConnectionClosed();
                }
                throw;
            }
        }

        public async Task SendVideoDataAsync(byte[] videoData, string email, string displayName)
        {
            if (!_isConnected || _webSocket.State != WebSocketState.Open)
            {
                _logger.LogWarning("Cannot send video data - WebSocket is not connected");
                return;
            }

            try
            {
                var payload = new
                {
                    type = "video",
                    email = email,
                    displayName = displayName,
                    buffer = Convert.ToBase64String(videoData)
                };

                var jsonString = System.Text.Json.JsonSerializer.Serialize(payload);
                var messageBytes = Encoding.UTF8.GetBytes(jsonString);

                await _webSocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    _cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error sending video data: {ex.Message}");
                if (_webSocket.State != WebSocketState.Open)
                {
                    _isConnected = false;
                    OnConnectionClosed();
                }
                throw;
            }
        }

        public async Task SendMeetingEventAsync(string eventType, long startTime, long? endTime = null)
        {
            if (!_isConnected || _webSocket.State != WebSocketState.Open)
            {
                _logger.LogWarning("Cannot send meeting event - WebSocket is not connected");
                return;
            }

            try
            {
                var payload = new
                {
                    type = "meeting_event",
                    eventType = eventType,
                    startTime = startTime,
                    endTime = endTime
                };

                var jsonString = System.Text.Json.JsonSerializer.Serialize(payload);
                var messageBytes = Encoding.UTF8.GetBytes(jsonString);

                await _webSocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    _cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error sending meeting event: {ex.Message}");
                if (_webSocket.State != WebSocketState.Open)
                {
                    _isConnected = false;
                    OnConnectionClosed();
                }
                throw;
            }
        }

        public async Task StartReceivingAsync()
        {
            var buffer = new byte[4096];
            try
            {
                while (_webSocket.State == WebSocketState.Open)
                {
                    var result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        _cancellationTokenSource.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            string.Empty,
                            _cancellationTokenSource.Token);
                        _isConnected = false;
                        OnConnectionClosed();
                    }
                    else
                    {
                        // Handle received data
                        var receivedData = new byte[result.Count];
                        Array.Copy(buffer, receivedData, result.Count);
                        // Process received data as needed
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error receiving data: {ex.Message}");
                _isConnected = false;
                OnConnectionClosed();
                throw;
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            if (_webSocket.State == WebSocketState.Open)
            {
                _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closing connection",
                    CancellationToken.None).Wait();
            }
            _webSocket.Dispose();
            _cancellationTokenSource.Dispose();
        }
    }
}
