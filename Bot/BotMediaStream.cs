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

            if (_settings.UseSpeechService)
            {
                _languageService = new SpeechService(_settings, _logger);
                _languageService.SendMediaBuffer += this.OnSendMediaBuffer;
            }

            // Get single video socket
            this.videoSocket = mediaSession.VideoSockets?.FirstOrDefault();
            if (this.videoSocket != null)
            {
                Console.WriteLine($"[BotMediaStream] Initialized single video socket with ID: {videoSocket.SocketId}");
                videoSocket.VideoMediaReceived += this.OnVideoMediaReceived;
            }
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

            await this.startVideoPlayerCompleted.Task.ConfigureAwait(false);

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

            // make sure all the audio and video buffers are disposed, it can happen that,
            // the buffers were not enqueued but the call was disposed if the caller hangs up quickly
            foreach (var audioMediaBuffer in this.audioMediaBuffers)
            {
                audioMediaBuffer.Dispose();
            }

            _logger.LogInformation($"disposed {this.audioMediaBuffers.Count} audioMediaBUffers.");

            this.audioMediaBuffers.Clear();

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

            // Console.WriteLine("Audio Media Received: " + JsonConvert.SerializeObject(e, Formatting.Indented));

            if (!_isWebSocketConnected) return;

            if (e.Buffer.UnmixedAudioBuffers != null)
            {
                foreach (var buffer in e.Buffer.UnmixedAudioBuffers)
                {
                    var length = buffer.Length;
                    var data = new byte[length];
                    Marshal.Copy(buffer.Data, data, 0, (int)length);
                    
                    var speakerId = buffer.ActiveSpeakerId.ToString();
                    var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    
                    // If speaker changed, process and send the previous speaker's buffered audio
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

                if (_languageService != null)
                {
                    await _languageService.AppendAudioBuffer(e.Buffer);
                }
                else
                {
                    var length = e.Buffer.Length;
                    if (length > 0)
                    {
                        var buffer = new byte[length];
                        Marshal.Copy(e.Buffer.Data, buffer, 0, (int)length);

                        var currentTick = DateTime.Now.Ticks;
                        this.audioMediaBuffers = Util.Utilities.CreateAudioMediaBuffers(buffer, currentTick, _logger);
                        await this.audioVideoFramePlayer.EnqueueBuffersAsync(this.audioMediaBuffers, new List<VideoMediaBuffer>());
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

                        // Calculate minutes since meeting start
                        long? timeSinceMeetingStart = null;
                        if (_meetingStartTime.HasValue)
                        {
                            // Convert meeting start time from milliseconds to seconds
                            var meetingStartTimeSec = _meetingStartTime.Value / 1000;
                            
                            // Calculate seconds since meeting start
                            var secondsSinceMeetingStart = speakStartTimeSec - meetingStartTimeSec;
                            
                            // Convert to minutes
                            timeSinceMeetingStart = secondsSinceMeetingStart / 60;
                        }

                        // Send both the audio data and timing information
                        await _webSocketClient.SendAudioDataAsync(
                            combinedBuffer, 
                            userDetails?.Email ?? _candidateEmail, 
                            info.DisplayName,
                            speakStartTimeSec * 1000,
                            speakEndTimeSec * 1000,
                            timeSinceMeetingStart,
                            string.IsNullOrEmpty(userDetails?.Email) ? "Candidate" : "Panelist"
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing clubbed audio data");
                }
                finally
                {
                    // Clear the buffers after sending
                    _speakerBuffers[speakerId].Clear();
                }
            }
        }

        private async void OnVideoMediaReceived(object sender, VideoMediaReceivedEventArgs e)
        {
            if (!_isWebSocketConnected) return;

            try 
            {
                byte[] buffer = new byte[e.Buffer.Length];
                Marshal.Copy(e.Buffer.Data, buffer, 0, (int)e.Buffer.Length);
                // Send to WebSocket server
                await _webSocketClient.SendVideoDataAsync(buffer, e.Buffer.VideoFormat, e.Buffer.OriginalVideoFormat);
          
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing video data");
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }

        private void OnSendMediaBuffer(object? sender, Media.MediaStreamEventArgs e)
        {
            this.audioMediaBuffers = e.AudioMediaBuffers;
            var result = Task.Run(async () => await this.audioVideoFramePlayer.EnqueueBuffersAsync(this.audioMediaBuffers, new List<VideoMediaBuffer>())).GetAwaiter();
        }

        private void WebSocketClient_ConnectionClosed(object sender, EventArgs e)
        {
            _isWebSocketConnected = false;
            _logger.LogWarning("WebSocket connection closed - audio/video streaming will be paused");
        }

        /// <summary>
        /// Subscribe to a participant's video
        /// </summary>
        public void Subscribe(MediaType mediaType, uint msi, VideoResolution resolution)
        {
            try
            {
                if (videoSocket != null)
                {
                    Console.WriteLine($"[BotMediaStream] Subscribing MSI {msi} to socket {videoSocket.SocketId}");
                    videoSocket.Subscribe(resolution, msi);  
                }
                else
                {
                    Console.WriteLine($"[BotMediaStream] No video socket available for subscription");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMediaStream] Error in Subscribe: {ex.Message}");
            }
        }
    }
}
