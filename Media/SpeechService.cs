using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Skype.Bots.Media;
using System.Runtime.InteropServices;

namespace EchoBot.Media
{
    /// <summary>
    /// Class SpeechService.
    /// </summary>
    public class SpeechService
    {
        /// <summary>
        /// The is the indicator if the media stream is running
        /// </summary>
        private bool _isRunning = false;
        /// <summary>
        /// The is draining indicator
        /// </summary>
        protected bool _isDraining;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger _logger;
        private readonly PushAudioInputStream _audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        private readonly AudioOutputStream _audioOutputStream = AudioOutputStream.CreatePullStream();

        private readonly SpeechConfig _speechConfig;
        private SpeechRecognizer _recognizer = null!;
        private readonly SpeechSynthesizer _synthesizer;
        /// <summary>
        /// Initializes a new instance of the <see cref="SpeechService" /> class.
        public SpeechService(AppSettings settings, ILogger logger)
        {
            _logger = logger;

            Console.WriteLine($"[SpeechService] Initializing with language: {settings.BotLanguage}");
            Console.WriteLine($"[SpeechService] Speech region: {settings.SpeechConfigRegion}");
            
            _speechConfig = SpeechConfig.FromSubscription(settings.SpeechConfigKey, settings.SpeechConfigRegion);
            _speechConfig.SpeechSynthesisLanguage = settings.BotLanguage;
            _speechConfig.SpeechRecognitionLanguage = settings.BotLanguage;

            // Enable continuous recognition mode
            _speechConfig.EnableAudioLogging();
            _speechConfig.EnableDictation();

            var audioConfig = AudioConfig.FromStreamOutput(_audioOutputStream);
            _synthesizer = new SpeechSynthesizer(_speechConfig, audioConfig);

            Console.WriteLine("[SpeechService] Speech service initialized successfully");
        }

        /// <summary>
        /// Appends the audio buffer.
        /// </summary>
        /// <param name="audioBuffer"></param>
        public async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer)
        {
            if (!_isRunning)
            {
                Console.WriteLine("[AppendAudioBuffer] Starting speech service...");
                Start();
                await ProcessSpeech();
            }

            try
            {
                // audio for a 1:1 call
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    // Console.WriteLine($"[AppendAudioBuffer] Received audio buffer with length: {bufferLength}");
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);

                    _audioInputStream.Write(buffer);
                    // Console.WriteLine("[AppendAudioBuffer] Successfully wrote audio data to input stream");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[AppendAudioBuffer] Error processing audio buffer: {e.Message}");
                _logger.LogError(e, "Exception happened writing to input stream");
            }
        }

        public virtual void OnSendMediaBufferEventArgs(object sender, MediaStreamEventArgs e)
        {
            if (SendMediaBuffer != null)
            {
                SendMediaBuffer(this, e);
            }
        }

        public event EventHandler<MediaStreamEventArgs>? SendMediaBuffer;

        /// <summary>
        /// Ends this instance.
        /// </summary>
        /// <returns>Task.</returns>
        public async Task ShutDownAsync()
        {
            if (!_isRunning)
            {
                return;
            }

            if (_isRunning)
            {
                await _recognizer.StopContinuousRecognitionAsync();
                _recognizer.Dispose();
                _audioInputStream.Close();

                _audioInputStream.Dispose();
                _audioOutputStream.Dispose();
                _synthesizer.Dispose();

                _isRunning = false;
            }
        }

        /// <summary>
        /// Starts this instance.
        /// </summary>
        private void Start()
        {
            if (!_isRunning)
            {
                _isRunning = true;
            }
        }

        /// <summary>
        /// Processes this instance.
        /// </summary>
        private async Task ProcessSpeech()
        {
            try
            {
                Console.WriteLine("[ProcessSpeech] Starting...");
                var stopRecognition = new TaskCompletionSource<int>();

                using (var audioInput = AudioConfig.FromStreamInput(_audioInputStream))
                {
                    if (_recognizer == null)
                    {
                        Console.WriteLine("[ProcessSpeech] Initializing recognizer...");
                        _recognizer = new SpeechRecognizer(_speechConfig, audioInput);
                        Console.WriteLine("[ProcessSpeech] Recognizer created successfully");
                    }
                }

                _recognizer.Recognizing += (s, e) =>
                {
                    Console.WriteLine("[ProcessSpeech] Recognizing event triggered");
                    Console.WriteLine($"[LIVE TRANSCRIPT] {e.Result.Text}");
                    _logger.LogInformation($"RECOGNIZING: Text={e.Result.Text}");
                };

                _recognizer.Recognized += async (s, e) =>
                {
                    Console.WriteLine("[ProcessSpeech] Recognized event triggered");
                    if (e.Result.Reason == ResultReason.RecognizedSpeech)
                    {
                        if (string.IsNullOrEmpty(e.Result.Text))
                            return;

                        Console.WriteLine($"[FINAL TRANSCRIPT] {e.Result.Text}");
                        _logger.LogInformation($"RECOGNIZED: Text={e.Result.Text}");
                        // We recognized the speech
                        // Now do Speech to Text
                        await TextToSpeech(e.Result.Text);
                    }
                    else
                    {
                        Console.WriteLine($"[ProcessSpeech] Recognition result reason: {e.Result.Reason}");
                    }
                };

                // Add logging for other events
                _recognizer.Canceled += (s, e) =>
                {
                    Console.WriteLine($"[ProcessSpeech] Recognition canceled. Reason: {e.Reason}, Error Details: {e.ErrorDetails}");
                    stopRecognition.TrySetResult(0);
                };

                _recognizer.SessionStarted += (s, e) =>
                {
                    Console.WriteLine("[ProcessSpeech] Recognition session started");
                };

                _recognizer.SessionStopped += (s, e) =>
                {
                    Console.WriteLine("[ProcessSpeech] Recognition session stopped");
                    stopRecognition.TrySetResult(0);
                };

                // Start continuous recognition
                Console.WriteLine("[ProcessSpeech] Starting continuous recognition...");
                await _recognizer.StartContinuousRecognitionAsync();
                Console.WriteLine("[ProcessSpeech] Continuous recognition started");

                // Wait for recognition to finish
                await stopRecognition.Task;
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogError(ex, "The queue processing task object has been disposed.");
            }
            catch (Exception ex)
            {
                // Catch all other exceptions and log
                _logger.LogError(ex, "Caught Exception");
            }

            _isDraining = false;
        }

        private async Task TextToSpeech(string text)
        {
            // convert the text to speech
            SpeechSynthesisResult result = await _synthesizer.SpeakTextAsync(text);
            // take the stream of the result
            // create 20ms media buffers of the stream
            // and send to the AudioSocket in the BotMediaStream
            using (var stream = AudioDataStream.FromResult(result))
            {
                var currentTick = DateTime.Now.Ticks;
                MediaStreamEventArgs args = new MediaStreamEventArgs
                {
                    AudioMediaBuffers = Util.Utilities.CreateAudioMediaBuffers(stream, currentTick, _logger)
                };
                OnSendMediaBufferEventArgs(this, args);
            }
        }
    }
}
