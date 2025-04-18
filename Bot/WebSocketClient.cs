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
        private readonly string _companyId;
        private readonly ILogger _logger;
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isConnected;

        // Store interview details
        private string _interviewId;
        private long? _interviewStartTime;
        private long? _interviewEndTime;
        private string _candidateEmail;
        private VISTAQuestions _vistaQuestions;

        // Video streaming related fields
        private int _frameIndex = 0;

        public bool IsConnected => _isConnected && _webSocket?.State == WebSocketState.Open;

        public event EventHandler ConnectionClosed;

        public WebSocketClient(
            string serverUrl,
            string jwtSecret,
            string companyId,
            ILogger logger,
            string interviewId = null,
            long? interviewStartTime = null,
            long? interviewEndTime = null,
            string candidateEmail = null,
            VISTAQuestions vistaQuestions = null)
      {
            _serverUrl = serverUrl;
            _jwtSecret = jwtSecret;
            _companyId = companyId;
            _logger = logger;
            _interviewId = interviewId;
            _interviewStartTime = interviewStartTime;
            _interviewEndTime = interviewEndTime;
            _candidateEmail = candidateEmail;
            _vistaQuestions = vistaQuestions;
        }

        private string GenerateJwtToken()
        {
            try
            {
                var token = JwtBuilder.Create()
                    .WithAlgorithm(new HMACSHA256Algorithm())
                    .WithSecret(_jwtSecret)
                    .AddClaim("type", "teams-bot")
                    .AddClaim("companyId", _companyId)
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
                    type = "interview_details",
                    interviewId = _interviewId,
                    interviewStartTime = _interviewStartTime,
                    interviewEndTime = _interviewEndTime,
                    candidateEmail = _candidateEmail ?? "",
                    vistaQuestions = _vistaQuestions ?? new VISTAQuestions()
                };

                var message = System.Text.Json.JsonSerializer.Serialize(payload);
                var messageBytes = Encoding.UTF8.GetBytes(message);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    _cancellationTokenSource.Token);

               Console.WriteLine($"Sent meeting details event for meeting {_interviewId}");
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

        public async Task SendAudioDataAsync(byte[] audioData, string email, string displayName, long speakStartTime, long speakEndTime, string role)
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

        /// <summary>
        /// Sends a talk_alert event to the WebSocket server.
        /// </summary>
        /// <param name="alertPayload">The payload object containing talk alert details.</param>
        public async Task SendTalkAlertEventAsync(object alertPayload)
        {
            if (!_isConnected || _webSocket.State != WebSocketState.Open)
            {
                Console.WriteLine("Cannot send talk_alert event - WebSocket is not connected");
                return;
            }

            try
            {
                // Ensure type is set to talk_alert if not present
                var payloadWithType = alertPayload;
                if (alertPayload is not null && !alertPayload.GetType().GetProperty("type")?.GetValue(alertPayload)?.Equals("talk_alert") == true)
                {
                    // If type is not set, wrap the payload
                    payloadWithType = new { type = "talk_ratio_alert", data = alertPayload };
                }
                var jsonString = System.Text.Json.JsonSerializer.Serialize(payloadWithType);
                var messageBytes = Encoding.UTF8.GetBytes(jsonString);

                await _webSocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    _cancellationTokenSource.Token);
                Console.WriteLine($"Sent talk_alert event: {jsonString}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending talk_alert event: {ex.Message}");
                if (_webSocket.State != WebSocketState.Open)
                {
                    _isConnected = false;
                    OnConnectionClosed();
                }
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
