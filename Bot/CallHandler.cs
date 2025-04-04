using Azure.Core;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using EchoBot.Util;
using System.Timers;
using System.Collections.Generic;
using Microsoft.Skype.Bots.Media;
using System.Net.Http.Headers;
using Newtonsoft.Json;

namespace EchoBot.Bot
{
    /// <summary>
    /// Call Handler Logic.
    /// </summary>
    public class CallHandler : HeartbeatHandler
    {
        /// <summary>
        /// Gets the call.
        /// </summary>
        /// <value>The call.</value>
        public ICall Call { get; }

        /// <summary>
        /// Gets the bot media stream.
        /// </summary>
        /// <value>The bot media stream.</value>
        public BotMediaStream BotMediaStream { get; private set; }

        // hashSet of the available sockets
        private readonly HashSet<uint> availableSocketIds = new HashSet<uint>();

        // Mapping of MSI to socket ID
        private readonly Dictionary<uint, uint> msiToSocketIdMapping = new Dictionary<uint, uint>();

        private readonly object subscriptionLock = new object();
        private bool hasSubscribedToCandidate = false;
        private readonly ILogger _logger;
        private readonly AppSettings _settings;

        private long? _meetingStartTime;
        private long? _meetingEndTime;
        private string? _candidateEmail;

        private string? subscribedParticipantId = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="CallHandler" /> class.
        /// </summary>
        /// <param name="statefulCall">The stateful call.</param>
        /// <param name="settings">The settings.</param>
        /// <param name="logger"></param>
        /// <param name="webSocketClient">WebSocket client instance</param>
        /// <param name="meetingStartTime">Optional meeting start time</param>
        /// <param name="meetingEndTime">Optional meeting end time</param>
        /// <param name="candidateEmail">Optional candidate email</param>
        public CallHandler(
            ICall statefulCall,
            AppSettings settings,
            ILogger logger,
            WebSocketClient webSocketClient,
            long? meetingStartTime = null,
            long? meetingEndTime = null,
            string? candidateEmail = null
        )
            : base(TimeSpan.FromMinutes(10), statefulCall?.GraphLogger)
        {
            // Console.WriteLine($"[CallHandler] Initializing for call {statefulCall.Id}");
            this.Call = statefulCall;
            this._settings = settings;
            this._meetingStartTime = meetingStartTime;
            this._meetingEndTime = meetingEndTime;
            this._candidateEmail = candidateEmail;
            
            // Subscribe to call updates first
            this.Call.OnUpdated += this.CallOnUpdated;
            this.Call.Participants.OnUpdated += this.ParticipantsOnUpdated;

            // Initialize available socket IDs
            foreach (var videoSocket in this.Call.GetLocalMediaSession().VideoSockets)
            {
                this.availableSocketIds.Add((uint)videoSocket.SocketId);
                Console.WriteLine($"[CallHandler] Adding video socket with ID: {videoSocket.SocketId}");
            }

            var temp = this.Call.GetLocalMediaSession().VideoSockets?.FirstOrDefault();
            Console.WriteLine($"[CallHandler] First video socket with ID: {temp?.SocketId} ");
            var temp2 = this.Call.GetLocalMediaSession().VideoSockets?.ToList();
            foreach (var i in temp2)
            {
                Console.WriteLine($"[CallHandler] Adding video socket with ID: {i.SocketId}");
            }

            // Create BotMediaStream before subscribing to participants
            this.BotMediaStream = new BotMediaStream(
                this.Call.GetLocalMediaSession(), 
                this.Call.Id, 
                this.GraphLogger, 
                logger, 
                settings, 
                this.Call, 
                webSocketClient,
                this._meetingStartTime, 
                this._meetingEndTime, 
                this._candidateEmail
            );
        }

        /// <inheritdoc/>
        protected override Task HeartbeatAsync(ElapsedEventArgs args)
        {
            return this.Call.KeepAliveAsync();
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            this.Call.OnUpdated -= this.CallOnUpdated;
            this.Call.Participants.OnUpdated -= this.ParticipantsOnUpdated;

            this.BotMediaStream?.ShutdownAsync().ForgetAndLogExceptionAsync(this.GraphLogger);
        }

        /// <summary>
        /// Event fired when the call has been updated.
        /// </summary>
        /// <param name="sender">The call.</param>
        /// <param name="e">The event args containing call changes.</param>
        private async void CallOnUpdated(ICall sender, ResourceEventArgs<Call> e)
        {
            GraphLogger.Info($"Call status updated to {e.NewResource.State} - {e.NewResource.ResultInfo?.Message}");

            // Check for participant media state changes
            foreach (var participant in sender.Participants)
            {
                if (participant.Id == subscribedParticipantId)
                {
                    var videoStream = participant.Resource.MediaStreams?.FirstOrDefault(x => 
                        x.MediaType == Modality.Video && 
                        (x.Direction == MediaDirection.SendReceive || x.Direction == MediaDirection.SendOnly));

                    if (videoStream != null)
                    {
                        // Re-subscribe if this is our candidate and they have video available
                        SubscribeToParticipantVideo(participant);
                    }
                }
            }

            // Handle call establishment
            if (e.OldResource.State != CallState.Established && e.NewResource.State == CallState.Established)
            {
                _meetingStartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                // No need to send meeting_started event as we already sent meeting_details
            }

            // Handle call termination
            if (e.NewResource.State == CallState.Terminated)
            {
                Console.WriteLine($"[CallHandler] Call terminated. Reason: {e.NewResource.ResultInfo?.Message}");
                
                // Send meeting ended event with start and end times
                if (_meetingStartTime.HasValue)
                {
                    var endTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    await BotMediaStream.WebSocketClient.SendMeetingEventAsync("meeting_ended", _meetingStartTime.Value, endTime);
                }

                if (BotMediaStream != null)
                {
                    Console.WriteLine($"[CallHandler] Shutting down media stream");
                    await BotMediaStream.ShutdownAsync().ForgetAndLogExceptionAsync(GraphLogger);
                }
            }

            if ((e.OldResource.State == CallState.Established) && (e.NewResource.State == CallState.Terminated))
            {
                if (BotMediaStream != null)
                {
                    Console.WriteLine($"[CallHandler] Call terminated, shutting down media stream");
                    await BotMediaStream.ShutdownAsync().ForgetAndLogExceptionAsync(GraphLogger);
                }
            }
        }

        /// <summary>
        /// Creates the participant update json.
        /// </summary>
        /// <param name="participantId">The participant identifier.</param>
        /// <param name="participantDisplayName">Display name of the participant.</param>
        /// <returns>System.String.</returns>
        private string createParticipantUpdateJson(string participantId, string participantDisplayName = "")
        {
            if (participantDisplayName.Length == 0)
                return "{" + String.Format($"\"Id\": \"{participantId}\"") + "}";
            else
                return "{" + String.Format($"\"Id\": \"{participantId}\", \"DisplayName\": \"{participantDisplayName}\"") + "}";
        }

        /// <summary>
        /// Updates the participant.
        /// </summary>
        /// <param name="participants">The participants.</param>
        /// <param name="participant">The participant.</param>
        /// <param name="added">if set to <c>true</c> [added].</param>
        /// <param name="participantDisplayName">Display name of the participant.</param>
        /// <returns>System.String.</returns>
        private string updateParticipant(List<IParticipant> participants, IParticipant participant, bool added, string participantDisplayName = "")
        {
            if (added)
                participants.Add(participant);
            else
                participants.Remove(participant);
            return createParticipantUpdateJson(participant.Id, participantDisplayName);
        }

        /// <summary>
        /// Updates the participants.
        /// </summary>
        /// <param name="eventArgs">The event arguments.</param>
        /// <param name="added">if set to <c>true</c> [added].</param>
        private async void updateParticipants(ICollection<IParticipant> eventArgs, bool added = true)
        {
            // Console.WriteLine($"[CallHandler] updateParticipants called with {eventArgs.Count} participants, added={added}");
            foreach (var participant in eventArgs)
            {
                try
                {
                    var json = string.Empty;
                    var participantDetails = participant?.Resource?.Info?.Identity?.User;
                    var participantId = participant?.Id;

                    if (participantDetails != null)
                    {
                        var isCandidate = false;
                        try 
                        {
                            var credentials = new ClientSecretCredential(
                                _settings.AadTenantId,
                                _settings.AadAppId,
                                _settings.AadAppSecret
                            );

                            var graphServiceClient = new GraphServiceClient(credentials);
                            var user = await graphServiceClient.Users[participantDetails.Id].GetAsync();
                            if (user != null)
                            {
                                Console.WriteLine($"[CallHandler] Graph API User Details: ID={user.Id}, Name={user.DisplayName}, Email={user.Mail ?? "No email"}");
                                
                                // Store user details in the BotMediaStream's dictionary
                                this.BotMediaStream.userDetailsMap ??= new Dictionary<string, BotMediaStream.UserDetails>();
                                this.BotMediaStream.userDetailsMap[participantDetails.Id] = new BotMediaStream.UserDetails
                                {
                                    Id = user.Id,
                                    DisplayName = user.DisplayName,
                                    Email = user.Mail ?? "No email"
                                };
                            }
                            else
                            {
                                // If we can't get user details, they might be external/guest
                                isCandidate = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CallHandler] Error fetching user details from Graph: {ex.Message}");
                            // If we can't fetch user details, they might be external/guest
                            isCandidate = true;
                        }

                        json = updateParticipant(this.BotMediaStream.participants, participant, added, participantDetails.DisplayName);
                        
                        if (added && isCandidate)
                        {
                            Console.WriteLine($"[CallHandler] Found candidate participant: {participantDetails.DisplayName}");
                            subscribedParticipantId = participant.Id;
                            SubscribeToParticipantVideo(participant);
                        }
                    }
                    else if (participant?.Resource?.Info?.Identity?.AdditionalData?.Count > 0)
                    {
                        if (CheckParticipantIsUsable(participant))
                        {
                            json = updateParticipant(this.BotMediaStream.participants, participant, added);
                            
                            if (added)
                            {
                                Console.WriteLine($"[CallHandler] Found potential candidate participant without user details");
                                subscribedParticipantId = participant.Id;
                                SubscribeToParticipantVideo(participant);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CallHandler] Error processing participant: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Event fired when the participants collection has been updated.
        /// </summary>
        /// <param name="sender">Participants collection.</param>
        /// <param name="args">Event args containing added and removed participants.</param>
        public void ParticipantsOnUpdated(IParticipantCollection sender, CollectionEventArgs<IParticipant> args)
        {
            // Handle added participants
            foreach (var participant in args.AddedResources)
            {
                Console.WriteLine($"[CallHandler] Participant added: {participant.Resource?.Info?.Identity?.User?.DisplayName ?? participant.Id}");
                // Monitor participant's media streams
                participant.OnUpdated += Participant_OnUpdated;
            }

            // Handle removed participants
            foreach (var participant in args.RemovedResources)
            {
                Console.WriteLine($"[CallHandler] Participant removed: {participant.Resource?.Info?.Identity?.User?.DisplayName ?? participant.Id}");
                participant.OnUpdated -= Participant_OnUpdated;
                
                if (participant.Id == subscribedParticipantId)
                {
                    hasSubscribedToCandidate = false;
                    subscribedParticipantId = null;
                }
            }

            updateParticipants(args.AddedResources);
            updateParticipants(args.RemovedResources, false);
        }

        /// <summary>
        /// Handle participant updates (including media state changes)
        /// </summary>
        private void Participant_OnUpdated(IParticipant sender, ResourceEventArgs<Participant> args)
        {
            try
            {
                var participantDetails = sender.Resource?.Info?.Identity?.User;
                bool isCandidate = false;

                // If this is already our subscribed candidate, treat them as candidate
                if (sender.Id == subscribedParticipantId)
                {
                    isCandidate = true;
                }
                // Check if we need to determine if this is a candidate
                else if (!hasSubscribedToCandidate)
                {
                    // First check if we already have user details
                    if (BotMediaStream.userDetailsMap?.ContainsKey(participantDetails?.Id) == true)
                    {
                        // We have user details, not a candidate
                        isCandidate = false;
                    }
                    else if (participantDetails != null)
                    {
                        // Try to get user details from Graph API
                        try
                        {
                            var credentials = new ClientSecretCredential(
                                _settings.AadTenantId,
                                _settings.AadAppId,
                                _settings.AadAppSecret
                            );

                            var graphServiceClient = new GraphServiceClient(credentials);
                            // Use Wait() to make this synchronous - we need to know if this is a candidate before proceeding
                            var user = Task.Run(async () => await graphServiceClient.Users[participantDetails.Id].GetAsync()).Result;
                            
                            if (user != null)
                            {
                                Console.WriteLine($"[CallHandler] Graph API User Details in OnUpdated: ID={user.Id}, Name={user.DisplayName}, Email={user.Mail ?? "No email"}");
                                
                                // Store user details in the BotMediaStream's dictionary
                                this.BotMediaStream.userDetailsMap ??= new Dictionary<string, BotMediaStream.UserDetails>();
                                this.BotMediaStream.userDetailsMap[participantDetails.Id] = new BotMediaStream.UserDetails
                                {
                                    Id = user.Id,
                                    DisplayName = user.DisplayName,
                                    Email = user.Mail ?? "No email"
                                };

                                // Since we got valid user details, this is not a candidate
                                isCandidate = false;
                            }
                            else
                            {
                                // If we can't get user details, they might be external/guest
                                isCandidate = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CallHandler] Error fetching user details from Graph in OnUpdated: {ex.Message}");
                            // If we can't fetch user details, they might be external/guest
                            isCandidate = true;
                        }
                    }
                    else
                    {
                        // No user details available, might be external/guest
                        isCandidate = true;
                    }

                    // If this is a candidate and we haven't subscribed yet, mark them
                    if (isCandidate && !hasSubscribedToCandidate)
                    {
                        subscribedParticipantId = sender.Id;
                    }
                }

                // Check for video stream changes if this is our candidate
                if (isCandidate)
                {
                    var oldMediaStreams = args.OldResource?.MediaStreams;
                    var newMediaStreams = args.NewResource?.MediaStreams;

                    var oldVideoStream = oldMediaStreams?.FirstOrDefault(x =>
                        x.MediaType == Modality.Video &&
                        (x.Direction == MediaDirection.SendReceive || x.Direction == MediaDirection.SendOnly));

                    var newVideoStream = newMediaStreams?.FirstOrDefault(x =>
                        x.MediaType == Modality.Video &&
                        (x.Direction == MediaDirection.SendReceive || x.Direction == MediaDirection.SendOnly));

                    // Log the change in video state
                    Console.WriteLine($"[CallHandler] Video state change for candidate {sender.Id}:");
                    Console.WriteLine($"  Old video stream: {(oldVideoStream != null ? oldVideoStream.SourceId : "none")}");
                    Console.WriteLine($"  New video stream: {(newVideoStream != null ? newVideoStream.SourceId : "none")}");

                    // If video stream becomes available or changes
                    if (newVideoStream != null && (oldVideoStream == null || oldVideoStream.SourceId != newVideoStream.SourceId))
                    {
                        Console.WriteLine($"[CallHandler] Detected new video stream for candidate, attempting to subscribe");
                        SubscribeToParticipantVideo(sender);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CallHandler] Error in Participant_OnUpdated: {ex.Message}");
            }
        }

        /// <summary>
        /// Subscribe to participant's video stream
        /// </summary>
        private void SubscribeToParticipantVideo(IParticipant participant)
        {
            try
            {
                var videoStream = participant.Resource.MediaStreams?.FirstOrDefault(x => 
                    x.MediaType == Modality.Video && 
                    (x.Direction == MediaDirection.SendReceive || x.Direction == MediaDirection.SendOnly));

                if (videoStream != null)
                {
                    var msi = uint.Parse(videoStream.SourceId);
                    Console.WriteLine($"[CallHandler] Subscribing to candidate video for participant {participant.Id} with MSI {msi}");
                    this.BotMediaStream.Subscribe(MediaType.Video, msi, VideoResolution.HD1080p);
                    subscribedParticipantId = participant.Id;
                    hasSubscribedToCandidate = true;
                }
                else
                {
                    Console.WriteLine($"[CallHandler] No video stream available for participant {participant.Id}, will retry when stream becomes available");
                    // Don't set hasSubscribedToCandidate to true until we actually get the stream
                    hasSubscribedToCandidate = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CallHandler] Error subscribing to participant video: {ex.Message}");
                hasSubscribedToCandidate = false;
            }
        }

        /// <summary>
        /// Unsubscribe from participant's video stream
        /// </summary>
        private void UnsubscribeFromParticipantVideo(IParticipant participant)
        {
            // Reset subscription flag when unsubscribing
            hasSubscribedToCandidate = false;
        }

        /// <summary>
        /// Checks the participant is usable.
        /// </summary>
        /// <param name="p">The p.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise.</returns>
        private bool CheckParticipantIsUsable(IParticipant p)
        {
            foreach (var i in p.Resource.Info.Identity.AdditionalData)
                if (i.Key != "applicationInstance" && i.Value is Identity)
                    return true;

            return false;
        }
    }
}
