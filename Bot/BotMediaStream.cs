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
        /// The participants
        /// </summary>
        internal List<IParticipant> participants;

        /// <summary>
        /// Dictionary to store user details including email
        /// </summary>
        internal Dictionary<string, UserDetails> userDetailsMap;

        /// <summary>
        /// The audio socket
        /// </summary>
        private readonly IAudioSocket _audioSocket;
        /// <summary>
        /// The call instance
        /// </summary>
        private readonly ICall _call;
        /// <summary>
        /// The media stream
        /// </summary>
        private readonly List<IVideoSocket> multiViewVideoSockets;
        private readonly ILogger _logger;
        private AudioVideoFramePlayer audioVideoFramePlayer;
        private readonly TaskCompletionSource<bool> audioSendStatusActive;
        private readonly TaskCompletionSource<bool> startVideoPlayerCompleted;
        private AudioVideoFramePlayerSettings audioVideoFramePlayerSettings;
        private List<AudioMediaBuffer> audioMediaBuffers = new List<AudioMediaBuffer>();
        private int shutdown;
        private readonly SpeechService _languageService;

        private readonly object _fileLock = new object();

        private async Task AppendToAudioTodayFile(string jsonData)
        {
            var filePath = Path.Combine("rawData", $"audio_data_{DateTime.Now:yyyy-MM-dd}.txt");
            lock (_fileLock)
            {
                // Ensure each JSON object is on a new line
                File.AppendAllText(filePath, jsonData + Environment.NewLine);
            }
        }

        private async Task AppendToVideoTodayFile(string jsonData)
        {
            var filePath = Path.Combine("rawData", $"video_data_{DateTime.Now:yyyy-MM-dd}.txt");
            lock (_fileLock)
            {
                // Ensure each JSON object is on a new line
                File.AppendAllText(filePath, jsonData + Environment.NewLine);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BotMediaStream" /> class.
        /// </summary>
        /// <param name="mediaSession">The media session.</param>
        /// <param name="callId">The call identity</param>
        /// <param name="graphLogger">The Graph logger.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="settings">Azure settings</param>
        /// <param name="call">The call instance</param>
        /// <exception cref="InvalidOperationException">A mediaSession needs to have at least an audioSocket</exception>
        public BotMediaStream(
            ILocalMediaSession mediaSession,
            string callId,
            IGraphLogger graphLogger,
            ILogger logger,
            AppSettings settings,
            ICall call
        )
            : base(graphLogger)
        {
            ArgumentVerifier.ThrowOnNullArgument(mediaSession, nameof(mediaSession));
            ArgumentVerifier.ThrowOnNullArgument(logger, nameof(logger));
            ArgumentVerifier.ThrowOnNullArgument(settings, nameof(settings));
            ArgumentVerifier.ThrowOnNullArgument(call, nameof(call));

            _settings = settings;
            _logger = logger;
            _call = call;

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

            // Console.WriteLine($"[BotMediaStream] Media sockets initialized for call {callId}");

            if (_settings.UseSpeechService)
            {
                _languageService = new SpeechService(_settings, _logger);
                _languageService.SendMediaBuffer += this.OnSendMediaBuffer;
            }


            this.multiViewVideoSockets = mediaSession.VideoSockets?.ToList();
            foreach (var videoSocket in this.multiViewVideoSockets)
            {
                Console.WriteLine($"Video socket initialized with ID: {videoSocket.SocketId}");
                videoSocket.VideoMediaReceived += this.OnVideoMediaReceived;
                // videoSocket.VideoReceiveStatusChanged += (s, e) => {
                //     Console.WriteLine($"Video receive status changed for socket {e.SocketId}: {e.MediaReceiveStatus}");
                // };
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
            // Console.WriteLine("Received Audio : " + e.Buffer.UnmixedAudioBuffers);
            // Console.WriteLine("Received Audio : " + e.Buffer.ActiveSpeakers);

            if (e.Buffer.UnmixedAudioBuffers != null)
            {
                foreach (var buffer in e.Buffer.UnmixedAudioBuffers)
                {
                    var length = buffer.Length;
                    var data = new byte[length];
                    Marshal.Copy(buffer.Data, data, 0, (int)length);
                    // Console.WriteLine("buffer.ActiveSpeakerId: " + buffer.ActiveSpeakerId);
                    
                    // Get participant information using the call instance
                    var participant = _call.Participants.SingleOrDefault(x => 
                        x.Resource.IsInLobby == false && 
                        x.Resource.MediaStreams.Any(y => y.SourceId == buffer.ActiveSpeakerId.ToString()));
                    
                    if (participant != null)
                    {
                        var identitySet = participant.Resource?.Info?.Identity;
                        var identity = identitySet?.User;
                        Console.WriteLine($"identity: {identity}");
                        Console.WriteLine($"Active Speaker Identity: {identity?.Id}, DisplayName: {identity?.DisplayName}");
                        
                        // Get stored user details including email
                        UserDetails userDetails = null;
                        if (identity?.Id != null && userDetailsMap != null)
                        {
                            userDetailsMap.TryGetValue(identity.Id, out userDetails);
                        }
                        
                        // Save data to file
                        // try 
                        // {
                        //     var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
                        //     var info = new
                        //     {
                        //         Timestamp = timestamp,
                        //         ActiveSpeakerId = buffer.ActiveSpeakerId,
                        //         UserId = identity?.Id,
                        //         DisplayName = identity?.DisplayName,
                        //         Email = userDetails?.Email,
                        //         AudioLength = length,
                        //         AudioData = data  // Store raw audio data instead of Base64
                        //     };
                            
                        //     var jsonData = System.Text.Json.JsonSerializer.Serialize(info);
                        //     await AppendToAudioTodayFile(jsonData);
                        // }
                        // catch (Exception ex)
                        // {
                        //     Console.WriteLine($"Error saving data to file: {ex.Message}");
                        //     _logger.LogError(ex, "Error saving audio data to file");
                        // }
                    }
                }
            }

            try
            {
                // Console.WriteLine($"Received Audio: [AudioMediaReceivedEventArgs(Data=<{e.Buffer.Data.ToString()}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp})]");
                if (!startVideoPlayerCompleted.Task.IsCompleted) { return; }

                if (_languageService != null)
                {
                    // send audio buffer to language service for processing
                    // the particpant talking will hear the bot repeat what they said
                    await _languageService.AppendAudioBuffer(e.Buffer);
                    e.Buffer.Dispose();
                }
                else
                {
                    // send audio buffer back on the audio socket
                    // the particpant talking will hear themselves
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

        /// <summary>
        /// Receive video from subscribed participant.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The video media received arguments.</param>
        private async void OnVideoMediaReceived(object sender, VideoMediaReceivedEventArgs e)
        {
            // Console.WriteLine($"[VideoMediaReceivedEventArgs(Data=<{e.Buffer.Data.ToString()}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp}, Width={e.Buffer.VideoFormat.Width}, Height={e.Buffer.VideoFormat.Height}, ColorFormat={e.Buffer.VideoFormat.VideoColorFormat}, FrameRate={e.Buffer.VideoFormat.FrameRate} MediaSourceId={e.Buffer.MediaSourceId})]");
            // e.Buffer.Dispose();
            // try 
            // {
            //     // Get participant information using MediaSourceId
            //     var participant = _call.Participants.SingleOrDefault(x => 
            //         x.Resource.IsInLobby == false && 
            //         x.Resource.MediaStreams.Any(y => y.SourceId == e.Buffer.MediaSourceId.ToString()));

            //     if (participant != null)
            //     {
            //         var identity = participant.Resource?.Info?.Identity?.User;
                    
            //         // Get stored user details including email
            //         UserDetails userDetails = null;
            //         if (identity?.Id != null && userDetailsMap != null)
            //         {
            //             userDetailsMap.TryGetValue(identity.Id, out userDetails);
            //         }

            //         // Copy video data from IntPtr to byte array
            //         var length = e.Buffer.Length;
            //         var data = new byte[length];
            //         Marshal.Copy(e.Buffer.Data, data, 0, (int)length);

            //         var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
            //         var info = new
            //         {
            //             Timestamp = timestamp,
            //             MediaSourceId = e.Buffer.MediaSourceId,
            //             UserId = identity?.Id,
            //             DisplayName = identity?.DisplayName,
            //             Email = userDetails?.Email,
            //             VideoLength = length,
            //             VideoData = data,
            //             Width = e.Buffer.VideoFormat.Width,
            //             Height = e.Buffer.VideoFormat.Height
            //         };

            //         var jsonData = System.Text.Json.JsonSerializer.Serialize(info);
            //         await AppendToVideoTodayFile(jsonData);
            //     }
            // }
            // catch (Exception ex)
            // {
            //     Console.WriteLine($"Error saving video data: {ex.Message}");
            //     _logger.LogError(ex, "Error saving video data");
            // }
            // finally
            // {
            //     e.Buffer.Dispose();
            // }
        }

        private void OnSendMediaBuffer(object? sender, Media.MediaStreamEventArgs e)
        {
            this.audioMediaBuffers = e.AudioMediaBuffers;
            var result = Task.Run(async () => await this.audioVideoFramePlayer.EnqueueBuffersAsync(this.audioMediaBuffers, new List<VideoMediaBuffer>())).GetAwaiter();
        }

        /// <summary>
        /// Subscribe to a participant's video
        /// </summary>
        public void Subscribe(MediaType mediaType, uint msi, VideoResolution resolution, uint socketId)
        {
            try
            {
                var videoSocket = this.multiViewVideoSockets.FirstOrDefault(s => s.SocketId == socketId);
                if (videoSocket != null)
                {
                    Console.WriteLine($"[BotMediaStream] Subscribing MSI {msi} to socket {socketId}");
                    videoSocket.Subscribe(resolution, msi);  
                }
                else
                {
                    Console.WriteLine($"[BotMediaStream] Socket {socketId} not found for subscription");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMediaStream] Error in Subscribe: {ex.Message}");
            }
        }
    }
}
