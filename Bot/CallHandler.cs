using EchoBot.Util;
using Microsoft.Graph;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Graph.Models;
using System.Timers;
using System.Collections.Generic;
using Microsoft.Skype.Bots.Media;

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

        /// <summary>
        /// Initializes a new instance of the <see cref="CallHandler" /> class.
        /// </summary>
        /// <param name="statefulCall">The stateful call.</param>
        /// <param name="settings">The settings.</param>
        /// <param name="logger"></param>
        public CallHandler(
            ICall statefulCall,
            AppSettings settings,
            ILogger logger
        )
            : base(TimeSpan.FromMinutes(10), statefulCall?.GraphLogger)
        {
            // Console.WriteLine($"[CallHandler] Initializing for call {statefulCall.Id}");
            this.Call = statefulCall;
            
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
            this.BotMediaStream = new BotMediaStream(this.Call.GetLocalMediaSession(), this.Call.Id, this.GraphLogger, logger, settings, this.Call);
 
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
            // Console.WriteLine($"[CallHandler] Call status updated to {e.NewResource.State} - {e.NewResource.ResultInfo?.Message}");
            GraphLogger.Info($"Call status updated to {e.NewResource.State} - {e.NewResource.ResultInfo?.Message}");

            // if (e.OldResource.State != e.NewResource.State && e.NewResource.State == CallState.Established)
            // {
            //     // Console.WriteLine($"[CallHandler] Call established, getting updated roster");
                
            //     try
            //     {
            //         // Wait briefly to ensure participants are fully loaded
            //         await Task.Delay(1000);

            //         // Get participants from the call resource
            //         var resourceParticipants = e.NewResource.Participants;
            //         // Console.WriteLine($"[CallHandler] Resource participants count: {resourceParticipants?.Count ?? 0}");
                    
            //         if (resourceParticipants != null)
            //         {
            //             foreach (var participant in resourceParticipants)
            //             {
            //                 try 
            //                 {
            //                     // Get detailed participant info
            //                     var participantInfo = await this.Call.Participants.GetAsync(participant.Id);
            //                     if (participantInfo != null && CheckParticipantIsUsable(participantInfo))
            //                     {
            //                         Console.WriteLine($"[CallHandler] Adding resource participant: {participantInfo.Resource?.Info?.Identity?.User?.DisplayName ?? participantInfo.Id}");
            //                         this.updateParticipants(new List<IParticipant> { participantInfo });
            //                     }
            //                 }
            //                 catch (Exception participantEx)
            //                 {
            //                     Console.WriteLine($"[CallHandler] Error getting participant details for {participant.Id}: {participantEx.Message}");
            //                 }
            //             }
            //         }

            //         // Also check direct participants
            //         var directParticipants = this.Call.Participants;
            //         Console.WriteLine($"[CallHandler] Direct participants count: {directParticipants?.Count ?? 0}");
            //         if (directParticipants != null)
            //         {
            //             foreach (var participant in directParticipants)
            //             {
            //                 if (CheckParticipantIsUsable(participant))
            //                 {
            //                     Console.WriteLine($"[CallHandler] Adding direct participant: {participant.Resource?.Info?.Identity?.User?.DisplayName ?? participant.Id}");
            //                     this.updateParticipants(new List<IParticipant> { participant });
            //                 }
            //             }
            //         }
            //     }
            //     catch (Exception ex)
            //     {
            //         Console.WriteLine($"[CallHandler] Error getting participants after establish: {ex.Message}");
            //         GraphLogger.Error(ex, "Error getting participants after establish");
            //     }
            // }

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
        private void updateParticipants(ICollection<IParticipant> eventArgs, bool added = true)
        {
            // Console.WriteLine($"[CallHandler] updateParticipants called with {eventArgs.Count} participants, added={added}");
            foreach (var participant in eventArgs)
            {
                try
                {
                    var json = string.Empty;

                    // todo remove the cast with the new graph implementation,
                    // for now we want the bot to only subscribe to "real" participants
                    var participantDetails = participant?.Resource?.Info?.Identity?.User;
                    var participantId = participant?.Id;

                    if (participantDetails != null)
                    {
                        // Console.WriteLine($"[CallHandler] Adding participant with display name: {participantDetails.DisplayName}");
                        json = updateParticipant(this.BotMediaStream.participants, participant, added, participantDetails.DisplayName);
                        
                        // Subscribe to participant's video when they are added
                        if (added)
                        {
                            try
                            {
                                Console.WriteLine($"[CallHandler] Subscribing to video for participant: {participantDetails.DisplayName}");
                                SubscribeToParticipantVideo(participant);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[CallHandler] Error subscribing to video: {ex.Message}");
                            }
                        }
                    }
                    else if (participant?.Resource?.Info?.Identity?.AdditionalData?.Count > 0)
                    {
                        if (CheckParticipantIsUsable(participant))
                        {
                            // Console.WriteLine($"[CallHandler] Adding participant with ID: {participantId}");
                            json = updateParticipant(this.BotMediaStream.participants, participant, added);
                        }
                        else
                        {
                            // Console.WriteLine($"[CallHandler] Participant {participantId} not usable - skipping");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[CallHandler] Participant {participantId} has no identity info - skipping");
                    }
                }
                catch (Exception ex)
                {
                    // Console.WriteLine($"[CallHandler] Error processing participant: {ex.Message}");
                }
            }
            // Console.WriteLine($"[CallHandler] After update: BotMediaStream.participants.Count = {this.BotMediaStream.participants.Count}");
        }

        /// <summary>
        /// Event fired when the participants collection has been updated.
        /// </summary>
        /// <param name="sender">Participants collection.</param>
        /// <param name="args">Event args containing added and removed participants.</param>
        public void ParticipantsOnUpdated(IParticipantCollection sender, CollectionEventArgs<IParticipant> args)
        {
            // Console.WriteLine($"[CallHandler] Participants updated - Added: {args.AddedResources.Count}, Removed: {args.RemovedResources.Count}");
            // foreach (var participant in args.AddedResources)
            // {
            //     Console.WriteLine($"[CallHandler] Participant added: {participant.Resource?.Info?.Identity?.User?.DisplayName ?? participant.Id}");
            // }
            updateParticipants(args.AddedResources);
            updateParticipants(args.RemovedResources, false);
        }

        /// <summary>
        /// Subscribe to participant's video stream
        /// </summary>
        private void SubscribeToParticipantVideo(IParticipant participant, bool forceSubscribe = false)
        {
            try
            {
                // Filter for video-capable streams
                var videoStream = participant.Resource.MediaStreams?.FirstOrDefault(x => 
                    x.MediaType == Modality.Video && 
                    (x.Direction == MediaDirection.SendReceive || x.Direction == MediaDirection.SendOnly));

                if (videoStream != null)
                {
                    uint socketId = uint.MaxValue;
                    var msi = uint.Parse(videoStream.SourceId);
                    bool subscribeToVideo = false;

                    lock (this.subscriptionLock)
                    {
                        // Check if we already have this MSI mapped
                        if (!this.msiToSocketIdMapping.ContainsKey(msi))
                        {
                            // If we have available sockets, use one
                            if (this.availableSocketIds.Count > 0)
                            {
                                socketId = this.availableSocketIds.First();
                                this.availableSocketIds.Remove(socketId);
                                subscribeToVideo = true;
                                Console.WriteLine($"[CallHandler] Subscribing to video for participant {participant.Id} on socket {socketId}");
                            }
                            else if (forceSubscribe)
                            {
                                // If force subscribe, we could implement socket reuse here
                                Console.WriteLine($"[CallHandler] No available sockets for participant {participant.Id} video");
                            }
                        }
                    }

                    if (subscribeToVideo && socketId != uint.MaxValue)
                    {
                        this.msiToSocketIdMapping[msi] = socketId;
                        this.BotMediaStream.Subscribe(MediaType.Video, msi, VideoResolution.HD1080p, socketId);
                        Console.WriteLine($"[CallHandler] Successfully subscribed to video for participant {participant.Id}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CallHandler] Error subscribing to participant video: {ex.Message}");
            }
        }

        /// <summary>
        /// Unsubscribe from participant's video stream
        /// </summary>
        private void UnsubscribeFromParticipantVideo(IParticipant participant)
        {
            try
            {
                var videoStream = participant.Resource.MediaStreams?.FirstOrDefault(x => 
                    x.MediaType == Modality.Video && 
                    (x.Direction == MediaDirection.SendReceive || x.Direction == MediaDirection.SendOnly));

                if (videoStream != null)
                {
                    var msi = uint.Parse(videoStream.SourceId);
                    
                    lock (this.subscriptionLock)
                    {
                        if (this.msiToSocketIdMapping.TryGetValue(msi, out uint socketId))
                        {
                            this.msiToSocketIdMapping.Remove(msi);
                            this.availableSocketIds.Add(socketId);
                            Console.WriteLine($"[CallHandler] Unsubscribed from video for participant {participant.Id}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CallHandler] Error unsubscribing from participant video: {ex.Message}");
            }
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
