using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Microsoft.Extensions.Logging;
using System.IO;
using JWT.Algorithms;
using JWT.Builder;
using EchoBot.Models;
using Microsoft.Skype.Bots.Media;

namespace EchoBot.Bot
{
    public class WebSocketClient : IDisposable
    {
        private readonly string _serverUrl;
        private readonly string _jwtSecret;
        private readonly string _companyDomain;
        private readonly ILogger _logger;
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isConnected;

        // Store meeting details
        private string _meetingId;
        private long? _meetingStartTime;
        private long? _meetingEndTime;
        private string _candidateEmail;
        private MOATSQuestions _moatsQuestions;

        // Video streaming related fields
        private int _frameIndex = 0;

        public bool IsConnected => _isConnected && _webSocket?.State == WebSocketState.Open;

        public event EventHandler ConnectionClosed;

        public WebSocketClient(
            string serverUrl,
            string jwtSecret,
            string companyDomain,
            ILogger logger,
            string meetingId = null,
            long? meetingStartTime = null,
            long? meetingEndTime = null,
            string candidateEmail = null,
            MOATSQuestions moatsQuestions = null)
        {
            _serverUrl = serverUrl;
            _jwtSecret = jwtSecret;
            _companyDomain = companyDomain;
            _logger = logger;
            _meetingId = meetingId;
            _meetingStartTime = meetingStartTime;
            _meetingEndTime = meetingEndTime;
            _candidateEmail = candidateEmail;
            _moatsQuestions = moatsQuestions;
        }

        private string GenerateJwtToken()
        {
            try
            {
                var token = JwtBuilder.Create()
                    .WithAlgorithm(new HMACSHA256Algorithm())
                    .WithSecret(_jwtSecret)
                    .AddClaim("type", "teams-bot")
                    .AddClaim("companyDomain", _companyDomain)
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
                _webSocket = new ClientWebSocket();
                _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                _cancellationTokenSource = new CancellationTokenSource();
                var uri = new Uri(_serverUrl);

                // Generate JWT token
                var token = GenerateJwtToken();
                _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                
                Console.WriteLine($"Connecting to WebSocket at {uri}");
                await _webSocket.ConnectAsync(uri, _cancellationTokenSource.Token);
                _isConnected = true;
                Console.WriteLine("WebSocket connected successfully");

                // Send meeting details immediately after connection
                await SendMeetingDetailsEvent();

                // Start receiving messages
                _ = StartReceivingAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError($"WebSocket connection error: {ex.Message}");
                _isConnected = false;
                throw;
            }
        }

        private async Task SendMeetingDetailsEvent()
        {
            try
            {
                var payload = new
                {
                    type = "meeting_details",
                    meetingId = _meetingId,
                    meetingStartTime = _meetingStartTime,
                    meetingEndTime = _meetingEndTime,
                    candidateEmail = _candidateEmail ?? "",
                    moatsQuestions = _moatsQuestions ?? new MOATSQuestions()
                };

                var message = System.Text.Json.JsonSerializer.Serialize(payload);
                var messageBytes = Encoding.UTF8.GetBytes(message);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    _cancellationTokenSource.Token);

                Console.WriteLine($"Sent meeting details event for meeting {_meetingId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to send meeting details event: {ex.Message}");
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

        public async Task SendAudioDataAsync(byte[] audioData, string email, string displayName, long speakStartTime, long speakEndTime, long? timeSinceMeetingStart = null, string role = null)
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
                    email = email ?? "",
                    displayName = displayName ?? "",
                    buffer = Convert.ToBase64String(audioData),
                    speakStartTime = speakStartTime.ToString(),
                    speakEndTime = speakEndTime.ToString(),
                    // timeSinceMeetingStart = timeSinceMeetingStart?.ToString(),
                    role = string.IsNullOrEmpty(role) ? "Unknown" : role
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

        public async Task SendVideoDataAsync(byte[] videoData, VideoFormat format, VideoFormat originalFormat)
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
                    buffer = Convert.ToBase64String(videoData),
                    metadata = new
                    {
                        format = new
                        {
                            Width = format.Width,
                            Height = format.Height,
                            FrameRate = format.FrameRate
                        },
                        originalFormat = originalFormat != null ? new
                        {
                            Width = originalFormat.Width,
                            Height = originalFormat.Height,
                            FrameRate = originalFormat.FrameRate
                        } : null,
                        timestamp = DateTime.Now,
                        frameIndex = _frameIndex++
                    }
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
                    type = eventType,
                    startTime = startTime.ToString(),
                    endTime = endTime?.ToString()
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

        public async Task SendMeetingDetailsAsync(string meetingId, long startTime, long endTime, string candidateEmail)
        {
            if (!IsConnected)
            {
                _logger.LogWarning("Cannot send meeting details - WebSocket is not connected");
                return;
            }

            var payload = new
            {
                type = "meeting_details",
                meetingId,
                meetingStartTime = startTime,
                meetingEndTime = endTime,
                candidateEmail = candidateEmail ?? ""
            };

            var message = System.Text.Json.JsonSerializer.Serialize(payload);
            await SendMessageAsync(message);
            _logger.LogInformation($"Sent meeting details for meeting {meetingId}");
        }

        private async Task SendMessageAsync(string message)
        {
            var messageBytes = Encoding.UTF8.GetBytes(message);
            await _webSocket.SendAsync(
                new ArraySegment<byte>(messageBytes),
                WebSocketMessageType.Text,
                true,
                _cancellationTokenSource.Token);
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
            _cancellationTokenSource?.Cancel();
            if (_webSocket?.State == WebSocketState.Open)
            {
                _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closing connection",
                    CancellationToken.None).Wait();
            }
            _webSocket?.Dispose();
            _cancellationTokenSource?.Dispose();
        }
    }
}
