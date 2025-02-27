using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Microsoft.Extensions.Logging;
using System.IO;

namespace EchoBot.Bot
{
    public class WebSocketClient : IDisposable
    {
        private readonly ClientWebSocket _webSocket;
        private readonly Uri _serverUri;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private bool _isConnected;

        public WebSocketClient(string serverUrl, ILogger logger)
        {
            _webSocket = new ClientWebSocket();
            _serverUri = new Uri(serverUrl);
            _logger = logger;
            _cancellationTokenSource = new CancellationTokenSource();
            _isConnected = false;
        }

        public async Task ConnectAsync()
        {
            try
            {
                if (_webSocket.State != WebSocketState.Open)
                {
                    await _webSocket.ConnectAsync(_serverUri, _cancellationTokenSource.Token);
                    _isConnected = true;
                    _logger.LogInformation($"WebSocket connected to {_serverUri}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"WebSocket connection error: {ex.Message}");
                throw;
            }
        }

        public async Task SendAudioDataAsync(byte[] audioData, string email, string displayName)
        {
            if (!_isConnected) return;

            try
            {
                var payload = new
                {
                    type = "audio",
                    email = email,
                    displayName = displayName,
                    buffer = Convert.ToBase64String(audioData)
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
                throw;
            }
        }

        public async Task SendVideoDataAsync(byte[] videoData, string email, string displayName)
        {
            if (!_isConnected) return;

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
