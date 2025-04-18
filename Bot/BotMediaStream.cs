// ***********************************************************************
// Assembly         : EchoBot.Services
// Author           : JasonTheDeveloper
// Created          : 09-07-2020
//
// Last Modified By : bcage29
// Last Modified On : 10-17-2023
// ***********************************************************************
// <copyright file="BotMediaStream.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
// </copyright>
// <summary>The bot media stream.</summary>
// ***********************************************************************-
using EchoBot.Media;
using EchoBot.Util;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Skype.Bots.Media;
using Microsoft.Skype.Internal.Media.Services.Common;
using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Collections;

namespace EchoBot.Bot
{
    /// <summary>
    /// Class responsible for streaming audio and video.
    /// </summary>
    public class BotMediaStream : ObjectRootDisposable
    {
        public class UserDetails
        {
            public string Id { get; set; }
            public string DisplayName { get; set; }
            public string Email { get; set; }
        }

        private AppSettings _settings;

        /// <summary>
        /// The video stream interval in seconds
        /// </summary>
        private const int VIDEO_STREAM_INTERVAL = 5;

        /// <summary>
        /// Dictionary to track last video send time for each participant
        /// </summary>
        private readonly Dictionary<string, DateTime> _lastVideoSendTime = new Dictionary<string, DateTime>();

        /// <summary>
        /// Dictionary to track video stream state for participants
        /// </summary>
        private readonly Dictionary<string, bool> _participantVideoState = new Dictionary<string, bool>();

        /// <summary>
        /// Dictionary to track MSI history for participants
        /// </summary>
        private readonly Dictionary<string, HashSet<uint>> _participantMsiHistory = new Dictionary<string, HashSet<uint>>();

        /// <summary>
        /// The participants
        /// </summary>
        internal List<IParticipant> participants;

        /// <summary>
        /// Dictionary to store user details including email
        /// </summary>
        internal Dictionary<string, UserDetails> userDetailsMap;

        /// <summary>
        /// Gets the WebSocket client instance
        /// </summary>
        public WebSocketClient WebSocketClient => _webSocketClient;

        /// <summary>
        /// The audio socket
        /// </summary>
        private readonly IAudioSocket _audioSocket;
        /// <summary>
        /// The call instance
        /// </summary>
        private readonly ICall _call;

        private long? _meetingStartTime;
        private long? _meetingEndTime;
        private string? _candidateEmail;

        // Track which participant is the candidate
        private string? _candidateUserId;

        /// <summary>
        /// The media stream
        /// </summary>
        private readonly IVideoSocket videoSocket;
        private readonly ILogger _logger;
        private AudioVideoFramePlayer audioVideoFramePlayer;
        private readonly TaskCompletionSource<bool> audioSendStatusActive;
        private readonly TaskCompletionSource<bool> startVideoPlayerCompleted;
        private AudioVideoFramePlayerSettings audioVideoFramePlayerSettings;
        private List<AudioMediaBuffer> audioMediaBuffers = new List<AudioMediaBuffer>();
        private int shutdown;
        private readonly SpeechService _languageService;
        private readonly WebSocketClient _webSocketClient;
        private readonly object _fileLock = new object();
        private bool _isWebSocketConnected = false;

        // Dictionary to store buffers for each speaker
        private Dictionary<string, List<(byte[] buffer, long timestamp)>> _speakerBuffers = new Dictionary<string, List<(byte[] buffer, long timestamp)>>();
        private string _currentSpeakerId = null;
        private DateTime _lastBufferTime = DateTime.MinValue;
        private const int SILENCE_THRESHOLD_MS = 500; // 500ms silence threshold

        private class ParticipantInfo
        {
            public string UserId { get; set; }
            public string DisplayName { get; set; }
            public string Email { get; set; }
        }

        private Dictionary<string, ParticipantInfo> _participantInfo = new Dictionary<string, ParticipantInfo>();

        // Mapping from speakerId (ActiveSpeakerId/sourceId) to email
        private Dictionary<string, string> _speakerIdToEmail = new Dictionary<string, string>();

        // --- BEGIN NEW TALK ALERT LOGIC ---
        // Configurable parameters (should be loaded from AppSettings)
        private int TALK_WINDOW_MINUTES;
        private const long CANDIDATE_ALERT_TIME_MS = 60000; // 1 minute
        private readonly object _talkMonitorLock = new object();
        // Maps speakerId to a list of (start, end) timestamps
        private readonly Dictionary<string, List<(long start, long end)>> _speakingSegments = new Dictionary<string, List<(long, long)>>();
        // Track last alert time per speaker to avoid spamming
        private readonly Dictionary<string, long> _lastAlertTime = new Dictionary<string, long>();
        private readonly Dictionary<string, long> _panelistAlertSent = new Dictionary<string, long>();

        /// <summary>
        /// Initializes a new instance of the <see cref="BotMediaStream" /> class.
        /// </summary>
        /// <param name="mediaSession">The media session.</param>
        /// <param name="callId">The call identity</param>
        /// <param name="graphLogger">The Graph logger.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="settings">Azure settings</param>
        /// <param name="call">The call instance</param>
        /// <param name="webSocketClient">WebSocket client instance</param>
        /// <param name="meetingStartTime">Optional meeting start time</param>
        /// <param name="meetingEndTime">Optional meeting end time</param>
        /// <param name="candidateEmail">Optional candidate email</param>
        /// <exception cref="InvalidOperationException">A mediaSession needs to have at least an audioSocket</exception>
        public BotMediaStream(
            ILocalMediaSession mediaSession,
            string callId,
            IGraphLogger graphLogger,
            ILogger logger,
            AppSettings settings,
            ICall call,
            WebSocketClient webSocketClient,
            long? meetingStartTime = null,
            long? meetingEndTime = null,
            string? candidateEmail = null
        )
            : base(graphLogger)
        {
            ArgumentVerifier.ThrowOnNullArgument(mediaSession, nameof(mediaSession));
            ArgumentVerifier.ThrowOnNullArgument(logger, nameof(logger));
            ArgumentVerifier.ThrowOnNullArgument(settings, nameof(settings));
            ArgumentVerifier.ThrowOnNullArgument(call, nameof(call));
            ArgumentVerifier.ThrowOnNullArgument(webSocketClient, nameof(webSocketClient));

            _settings = settings;
            _logger = logger;
            _call = call;
            _meetingStartTime = meetingStartTime;
            _meetingEndTime = meetingEndTime;
            _candidateEmail = candidateEmail;
            _webSocketClient = webSocketClient;
            _webSocketClient.ConnectionClosed += WebSocketClient_ConnectionClosed;
            _isWebSocketConnected = true;  // Set initial connection status

            Console.WriteLine($"[BotMediaStream] Meeting Start Time: {meetingStartTime}, Meeting End Time: {meetingEndTime}, Candidate Email: {candidateEmail}");

            // Initialize participants list
            this.participants = new List<IParticipant>();
            this.audioSendStatusActive = new TaskCompletionSource<bool>();
            this.startVideoPlayerCompleted = new TaskCompletionSource<bool>();

            // Subscribe to the audio media.
            this._audioSocket = mediaSession.AudioSocket;
            if (this._audioSocket == null)
            {
                throw new InvalidOperationException("A mediaSession needs to have at least an audioSocket");
            }
            var ignoreTask = this.StartAudioVideoFramePlayerAsync().ForgetAndLogExceptionAsync(this.GraphLogger, "Failed to start the player");

            this._audioSocket.AudioSendStatusChanged += OnAudioSendStatusChanged;            
            this._audioSocket.AudioMediaReceived += this.OnAudioMediaReceived;

            // Get single video socket
            this.videoSocket = mediaSession.VideoSockets?.FirstOrDefault();
            if (this.videoSocket != null)
            {
                Console.WriteLine($"[BotMediaStream] Initialized single video socket with ID: {videoSocket.SocketId}");
                videoSocket.VideoMediaReceived += this.OnVideoMediaReceived;
            }

            TALK_WINDOW_MINUTES = _settings.SpeakingTimeWindowMinutes > 0 ? _settings.SpeakingTimeWindowMinutes : 5;
        }

        /// <summary>
        /// Gets the participants.
        /// </summary>
        /// <returns>List&lt;IParticipant&gt;.</returns>
        public List<IParticipant> GetParticipants()
        {
            return participants;
        }

        /// <summary>
        /// Shut down.
        /// </summary>
        /// <returns><see cref="Task" />.</returns>
        public async Task ShutdownAsync()
        {
            if (Interlocked.CompareExchange(ref this.shutdown, 1, 1) == 1)
            {
                return;
            }

            // First unsubscribe from video socket to stop video streaming
            try
            {
                if (videoSocket != null)
                {
                    videoSocket.Unsubscribe();
                    Console.WriteLine($"[BotMediaStream] Unsubscribed from video socket during shutdown");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMediaStream] Error unsubscribing from video socket during shutdown: {ex.Message}");
            }

            await this.startVideoPlayerCompleted.Task.ConfigureAwait(false);

            // Clear video states
            _participantVideoState.Clear();

            // unsubscribe
            if (this._audioSocket != null)
            {
                this._audioSocket.AudioSendStatusChanged -= this.OnAudioSendStatusChanged;
            }

            // shutting down the players
            if (this.audioVideoFramePlayer != null)
            {
                await this.audioVideoFramePlayer.ShutdownAsync().ConfigureAwait(false);
            }

            // make sure all the audio and video buffers are disposed
            foreach (var audioMediaBuffer in this.audioMediaBuffers)
            {
                audioMediaBuffer.Dispose();
            }

            _logger.LogInformation($"disposed {this.audioMediaBuffers.Count} audioMediaBUffers.");

            this.audioMediaBuffers.Clear();

            // Set WebSocket as disconnected
            _isWebSocketConnected = false;

            // Dispose WebSocket client
            _webSocketClient?.Dispose();
        }

        /// <summary>
        /// Initialize AV frame player.
        /// </summary>
        /// <returns>Task denoting creation of the player with initial frames enqueued.</returns>
        private async Task StartAudioVideoFramePlayerAsync()
        {
            try
            {
                _logger.LogInformation("Send status active for audio and video Creating the audio video player");
                this.audioVideoFramePlayerSettings =
                    new AudioVideoFramePlayerSettings(new AudioSettings(20), new VideoSettings(), 1000);
                this.audioVideoFramePlayer = new AudioVideoFramePlayer(
                    (AudioSocket)_audioSocket,
                    null,
                    this.audioVideoFramePlayerSettings);

                _logger.LogInformation("created the audio video player");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create the audioVideoFramePlayer with exception");
            }
            finally
            {
                this.startVideoPlayerCompleted.TrySetResult(true);
            }
        }

        /// <summary>
        /// Callback for informational updates from the media plaform about audio status changes.
        /// Once the status becomes active, audio can be loopbacked.
        /// </summary>
        /// <param name="sender">The audio socket.</param>
        /// <param name="e">Event arguments.</param>
        private void OnAudioSendStatusChanged(object? sender, AudioSendStatusChangedEventArgs e)
        {
            _logger.LogTrace($"[AudioSendStatusChangedEventArgs(MediaSendStatus={e.MediaSendStatus})]");

            if (e.MediaSendStatus == MediaSendStatus.Active)
            {
                this.audioSendStatusActive.TrySetResult(true);
            }
        }

        /// <summary>
        /// Receive audio from subscribed participant.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The audio media received arguments.</param>
        private async void OnAudioMediaReceived(object sender, AudioMediaReceivedEventArgs e)
        {
            // Console.WriteLine($"[OnAudioMediaReceived] Called at {DateTime.Now:HH:mm:ss.fff}");
            if (!_isWebSocketConnected) {
                Console.WriteLine("[OnAudioMediaReceived] WebSocket not connected, returning early.");
                return;
            }
            // Console.WriteLine("Audio Media Received: " + JsonConvert.SerializeObject(e, Formatting.Indented));

            if (e.Buffer.UnmixedAudioBuffers != null)
            {
                foreach (var buffer in e.Buffer.UnmixedAudioBuffers)
                {
                    var length = buffer.Length;
                    var data = new byte[length];
                    Marshal.Copy(buffer.Data, data, 0, (int)length);
                    
                    var speakerId = buffer.ActiveSpeakerId.ToString();
                    var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    
                    if (_currentSpeakerId != null && _currentSpeakerId != speakerId)
                    {
                        await ProcessAndSendBufferedAudio(_currentSpeakerId);
                    }

                    // Initialize buffer list for new speaker if needed
                    if (!_speakerBuffers.ContainsKey(speakerId))
                    {
                        _speakerBuffers[speakerId] = new List<(byte[] buffer, long timestamp)>();
                    }

                    // Add current buffer and timestamp to speaker's buffer list
                    _speakerBuffers[speakerId].Add((data, currentTimestamp));
                    _currentSpeakerId = speakerId;
                    _lastBufferTime = DateTime.Now;

                    // === Real-time panelist alert logic: check on every buffer ===
                    if (_participantInfo.TryGetValue(speakerId, out var info))
                    {
                        UserDetails userDetails = null;
                        if (userDetailsMap != null && info.UserId != null)
                        {
                            userDetailsMap.TryGetValue(info.UserId, out userDetails);
                        }
                        var email = info.Email ?? userDetails?.Email ?? _candidateEmail ?? "";
                        var displayName = info.DisplayName ?? "Unknown";
                        var role = email == _candidateEmail ? "Candidate" : "Panelist";
                        if (role == "Panelist")
                        {
                            // Calculate total speaking time in window, including ongoing segment
                            long nowMs_panelist = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            var windowMinutes = _settings.SpeakingTimeWindowMinutes > 0 ? _settings.SpeakingTimeWindowMinutes : 5;
                            long windowStart = nowMs_panelist - windowMinutes * 60 * 1000;
                            // Gather all segments for this panelist
                            if (_speakingSegments.TryGetValue(info.UserId, out var segments))
                            {
                                segments.RemoveAll(seg => seg.Item2 < windowStart);
                            } else {
                                segments = new List<(long, long)>();
                                _speakingSegments[info.UserId] = segments;
                            }
                            // Update the end of the last segment to now (do not add a new segment on every buffer)
                            if (segments.Count > 0)
                            {
                                // Update the end time of the ongoing segment
                                segments[segments.Count - 1] = (segments.Last().Item1, nowMs_panelist);
                            }
                            else
                            {
                                // Start a new segment
                                segments.Add((currentTimestamp, nowMs_panelist));
                            }
                            long totalMs = segments.Sum(seg => Math.Min(seg.Item2, nowMs_panelist) - Math.Max(seg.Item1, windowStart));
                            if (totalMs >= 60 * 1000)
                            {
                                if (!_panelistAlertSent.TryGetValue(info.UserId, out long lastAlert) || lastAlert < windowStart)
                                {
                                    var panelist = userDetailsMap != null && info.UserId != null && userDetailsMap.TryGetValue(info.UserId, out var ud) ? ud : new UserDetails { Id = info.UserId, DisplayName = displayName, Email = email };
                                    await SendPanelistSpokeAlert(panelist, totalMs, windowStart, nowMs_panelist);
                                    _panelistAlertSent[info.UserId] = nowMs_panelist;
                                }
                            }
                        }
                    }
                    // === END real-time panelist alert logic ===
                    // Store the participant info for when we need to send
                    if (!_participantInfo.ContainsKey(speakerId))
                    {
                        var participant = _call.Participants.SingleOrDefault(x => 
                            x.Resource.IsInLobby == false && 
                            x.Resource.MediaStreams.Any(y => y.SourceId == speakerId));
                        
                        if (participant != null)
                        {
                            var identitySet = participant.Resource?.Info?.Identity;
                            var identity = identitySet?.User;
                            
                            UserDetails userDetails = null;
                            if (identity?.Id != null && userDetailsMap != null)
                            {
                                userDetailsMap.TryGetValue(identity.Id, out userDetails);
                            }

                            _participantInfo[speakerId] = new ParticipantInfo 
                            {
                                UserId = identity?.Id,
                                DisplayName = identity?.DisplayName,
                                Email = userDetails?.Email
                            };
                            // Store mapping from speakerId to email for role assignment
                            if (!_speakerIdToEmail.ContainsKey(speakerId) && userDetails?.Email != null)
                                _speakerIdToEmail[speakerId] = userDetails.Email;
                        }
                    }
                }
            }

            try
            {
                if (!startVideoPlayerCompleted.Task.IsCompleted) { return; }

                // Check for silence timeout and process any pending buffers
                if (_currentSpeakerId != null)
                {
                    var timeSinceLastBuffer = DateTime.Now - _lastBufferTime;
                    if (timeSinceLastBuffer.TotalMilliseconds > SILENCE_THRESHOLD_MS)
                    {
                        await ProcessAndSendBufferedAudio(_currentSpeakerId);
                        _currentSpeakerId = null;
                    }
                }
            }
            catch (Exception ex)
            {
                this.GraphLogger.Error(ex);
                _logger.LogError(ex, "OnAudioMediaReceived error");
            }
            finally
            {
                e.Buffer.Dispose();
            }

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Now check panelist speaking time instead of candidate
            await MonitorPanelistSpeakingAsync(nowMs);
        }

        private async Task ProcessAndSendBufferedAudio(string speakerId)
        {
            if (_speakerBuffers.ContainsKey(speakerId) && _speakerBuffers[speakerId].Count > 0)
            {
                try
                {
                    var bufferList = _speakerBuffers[speakerId];
                    // Get the start timestamp in milliseconds
                    var speakStartTimeMs = bufferList.First().timestamp;
                    var speakEndTimeMs = bufferList.Last().timestamp;

                    // Convert speak times from milliseconds to seconds
                    var speakStartTimeSec = speakStartTimeMs / 1000;
                    var speakEndTimeSec = speakEndTimeMs / 1000;

                    // Combine all buffers for this speaker
                    var totalLength = bufferList.Sum(b => b.buffer.Length);
                    var combinedBuffer = new byte[totalLength];
                    var offset = 0;

                    foreach (var (buffer, _) in bufferList)
                    {
                        Buffer.BlockCopy(buffer, 0, combinedBuffer, offset, buffer.Length);
                        offset += buffer.Length;
                    }

                    // Get participant info
                    if (_participantInfo.TryGetValue(speakerId, out var info))
                    {
                        UserDetails userDetails = null;
                        if (userDetailsMap != null && info.UserId != null)
                        {
                            userDetailsMap.TryGetValue(info.UserId, out userDetails);
                        }

                        var email = info.Email ?? userDetails?.Email ?? _candidateEmail ?? "";
                        var displayName = info.DisplayName ?? "Unknown";
                        var role = email == _candidateEmail ? "Candidate" : "Panelist";

                        // Track panelist speaking segments for alerting
                        if (role == "Panelist")
                        {
                            TrackSpeakerSegment(info.UserId, speakStartTimeMs, speakEndTimeMs);

                            // Immediate alert check: sum all segments (including current)
                            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            var windowMinutes = _settings.SpeakingTimeWindowMinutes > 0 ? _settings.SpeakingTimeWindowMinutes : 5;
                            long windowStart = nowMs - windowMinutes * 60 * 1000;
                            if (_speakingSegments.TryGetValue(info.UserId, out var segments))
                            {
                                // Remove old segments
                                segments.RemoveAll(seg => seg.Item2 < windowStart);
                                // Add current segment if not already present (avoid double-counting)
                                if (segments.Count == 0 || segments.Last().Item2 != speakEndTimeMs)
                                    segments.Add((speakStartTimeMs, speakEndTimeMs));
                                long totalMs = segments.Sum(seg => Math.Min(seg.Item2, nowMs) - Math.Max(seg.Item1, windowStart));
                                if (totalMs >= 60 * 1000)
                                {
                                    if (!_panelistAlertSent.TryGetValue(info.UserId, out long lastAlert) || lastAlert < windowStart)
                                    {
                                        // Find UserDetails for alert
                                        var panelist = userDetailsMap != null && info.UserId != null && userDetailsMap.TryGetValue(info.UserId, out var ud) ? ud : new UserDetails { Id = info.UserId, DisplayName = displayName, Email = email };
                                        await SendPanelistSpokeAlert(panelist, totalMs, windowStart, nowMs);
                                        _panelistAlertSent[info.UserId] = nowMs;
                                    }
                                }
                            }
                        }
                        await _webSocketClient.SendAudioDataAsync(
                            combinedBuffer,
                            email,
                            displayName,
                            speakStartTimeMs,
                            speakEndTimeMs,
                            role
                        );
                    }

                    // Clear the processed buffers to prevent duplication
                    _speakerBuffers[speakerId].Clear();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing and sending buffered audio");
                }
            }
        }

        private void TrackSpeakerSegment(string speakerId, long start, long end)
        {
            lock (_talkMonitorLock)
            {
                if (!_speakingSegments.ContainsKey(speakerId))
                    _speakingSegments[speakerId] = new List<(long, long)>();
                _speakingSegments[speakerId].Add((start, end));
            }
        }

        private async Task MonitorPanelistSpeakingAsync(long nowMs)
        {
            // 1. Find all panelist participants (role == "panelist")
            var panelists = userDetailsMap?.Values?.Where(u => u != null && u.Id != null && u.Email != null && u.Id != _candidateUserId).ToList();
            if (panelists == null || panelists.Count == 0)
                return;

            // 2. For each panelist, calculate total speaking time in window
            var windowMinutes = _settings.SpeakingTimeWindowMinutes > 0 ? _settings.SpeakingTimeWindowMinutes : 5;
            var thresholdSeconds = 60; // Always 60 seconds (1 minute) as per user requirement
            long windowStart = nowMs - windowMinutes * 60 * 1000;
            foreach (var panelist in panelists)
            {
                if (!_speakingSegments.ContainsKey(panelist.Id))
                    continue;
                var segments = _speakingSegments[panelist.Id];
                // Remove old segments
                segments.RemoveAll(seg => seg.Item2 < windowStart);
                // Calculate total speaking time in window
                long totalMs = segments.Sum(seg => Math.Min(seg.Item2, nowMs) - Math.Max(seg.Item1, windowStart));
                if (totalMs >= thresholdSeconds * 1000)
                {
                    // Only send alert if not already sent for this window
                    if (!_panelistAlertSent.TryGetValue(panelist.Id, out long lastAlert) || lastAlert < windowStart)
                    {
                        await SendPanelistSpokeAlert(panelist, totalMs, windowStart, nowMs);
                        _panelistAlertSent[panelist.Id] = nowMs;
                    }
                }
            }
        }

        private async Task SendPanelistSpokeAlert(UserDetails panelist, long totalMs, long windowStart, long windowEnd)
        {
            var alertPayload = new
            {
                type = "panelist_speaking_alert",
                panelistId = panelist.Id,
                panelistEmail = panelist.Email,
                panelistName = panelist.DisplayName,
                speakingTimeSeconds = totalMs / 1000,
                windowStart,
                windowEnd,
                message = $"Panelist {panelist.DisplayName} spoke for more than 60 seconds in the last {_settings.SpeakingTimeWindowMinutes} minutes."
            };
            try
            {
                if (_isWebSocketConnected)
                {
                    await _webSocketClient.SendTalkAlertEventAsync(alertPayload);
                    _logger.LogInformation($"[PanelistAlert] Sent alert for panelist {panelist.DisplayName}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[PanelistAlert] Failed to send alert for panelist {panelist.DisplayName}.");
            }
        }

        private async void OnVideoMediaReceived(object sender, VideoMediaReceivedEventArgs e)
        {
            if (!_isWebSocketConnected) 
            {
                Console.WriteLine("[OnVideoMediaReceived] WebSocket not connected, skipping video frame");
                return;
            }

            try 
            {
                // Track that we're receiving video for this MSI
                string msiKey = e.Buffer.MediaSourceId.ToString();
                _participantVideoState[msiKey] = true;

                byte[] buffer = new byte[e.Buffer.Length];
                Marshal.Copy(e.Buffer.Data, buffer, 0, (int)e.Buffer.Length);
                
                // Send to WebSocket server
                await _webSocketClient.SendVideoDataAsync(buffer, e.Buffer.VideoFormat, e.Buffer.OriginalVideoFormat);
          
                // Log video frame details
                Console.WriteLine($"[BotMediaStream] Received video frame: MSI={e.Buffer.MediaSourceId}, Format={e.Buffer.VideoFormat}, Size={buffer.Length} bytes");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing video data: {ex.Message}");
                Console.WriteLine($"[BotMediaStream] Video processing error: {ex.Message}");
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }

        private void WebSocketClient_ConnectionClosed(object sender, EventArgs e)
        {
            _isWebSocketConnected = false;
            _logger.LogWarning("WebSocket connection closed - audio/video streaming will be paused");

            // Clear video states and unsubscribe from video socket
            try
            {
                _participantVideoState.Clear();
                if (videoSocket != null)
                {
                    videoSocket.Unsubscribe();
                    Console.WriteLine($"[BotMediaStream] Unsubscribed from video socket due to WebSocket disconnection");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMediaStream] Error cleaning up video socket: {ex.Message}");
            }
        }

        /// <summary>
        /// Subscribe to a participant's video
        /// </summary>
        public void Subscribe(MediaType mediaType, uint msi, VideoResolution resolution)
        {
            // Don't subscribe if WebSocket is not connected
            if (!_isWebSocketConnected)
            {
                Console.WriteLine($"[BotMediaStream] WebSocket not connected, skipping video subscription");
                return;
            }

            try
            {
                if (videoSocket != null)
                {
                    // Log subscription attempt
                    Console.WriteLine($"[BotMediaStream] Attempting to subscribe to MSI {msi} on socket {videoSocket.SocketId}");

                    // Unsubscribe from any existing subscription on this socket
                    try
                    {
                        videoSocket.Unsubscribe();
                        Console.WriteLine($"[BotMediaStream] Unsubscribed from previous stream on socket {videoSocket.SocketId}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BotMediaStream] Error unsubscribing from previous stream: {ex.Message}");
                    }

                    // Subscribe to the new MSI
                    videoSocket.Subscribe(resolution, msi);
                    
                    // Track that we're subscribed to this MSI
                    string msiKey = msi.ToString();
                    _participantVideoState[msiKey] = true;
                    
                    Console.WriteLine($"[BotMediaStream] Successfully subscribed to video stream MSI {msi}");
                }
                else
                {
                    Console.WriteLine($"[BotMediaStream] No video socket available for subscription");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMediaStream] Error in Subscribe: {ex.Message}");
                Console.WriteLine($"[BotMediaStream] Stack trace: {ex.StackTrace}");
            }
        }
    }
}
