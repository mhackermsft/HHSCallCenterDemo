using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Logging;
using System.Text;

namespace AudioTranscriptionFunction
{
    public class AudioTranscriptionTrigger
    {
        private readonly ILogger<AudioTranscriptionTrigger> _logger;

        public AudioTranscriptionTrigger(ILogger<AudioTranscriptionTrigger> logger)
        {
            _logger = logger;
        }

        [Function(nameof(AudioTranscriptionTrigger))]
        public async Task Run(
            [BlobTrigger("audio-input/{name}", Connection = "AzureWebJobsStorage")] Stream audioStream,
            string name)
        {
            _logger.LogInformation($"Processing audio file: {name}");

            try
            {
                // Get configuration from environment variables
                var speechKey = Environment.GetEnvironmentVariable("SpeechServiceKey");
                var speechRegion = Environment.GetEnvironmentVariable("SpeechServiceRegion");
                var storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");

                if (string.IsNullOrEmpty(speechKey) || string.IsNullOrEmpty(speechRegion))
                {
                    _logger.LogError("Speech service configuration is missing. Please set SpeechServiceKey and SpeechServiceRegion.");
                    return;
                }

                if (string.IsNullOrEmpty(storageConnectionString))
                {
                    _logger.LogError("Storage connection string is missing.");
                    return;
                }

                // Save the stream to a temporary file for speech processing
                var tempAudioPath = Path.Combine(Path.GetTempPath(), name);
                using (var fileStream = File.Create(tempAudioPath))
                {
                    await audioStream.CopyToAsync(fileStream);
                }

                _logger.LogInformation($"Audio file saved to: {tempAudioPath}");

                // Transcribe the audio with speaker diarization
                var transcript = await TranscribeAudioWithDiarization(tempAudioPath, speechKey, speechRegion);

                _logger.LogInformation($"Transcription completed. Length: {transcript.Length} characters");

                // Upload the transcript to the output container
                await UploadTranscript(storageConnectionString, name, transcript);

                // Clean up the temporary file
                File.Delete(tempAudioPath);

                _logger.LogInformation($"Successfully processed audio file: {name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing audio file {name}: {ex.Message}");
                throw;
            }
        }

        private async Task<string> TranscribeAudioWithDiarization(string audioFilePath, string speechKey, string speechRegion)
        {
            var config = SpeechConfig.FromSubscription(speechKey, speechRegion);
            
            // Configure for speech recognition
            config.SpeechRecognitionLanguage = "en-US";
            
            // Enable speaker diarization
            config.SetProperty(PropertyId.SpeechServiceConnection_EnableAudioLogging, "false");
            config.SetProperty("DiarizeIntermediateResults", "true");
            config.SetProperty("DiarizationMode", "Identity");
            config.RequestWordLevelTimestamps();

            using var audioConfig = AudioConfig.FromWavFileInput(audioFilePath);
            using var recognizer = new SpeechRecognizer(config, audioConfig);

            var transcriptBuilder = new StringBuilder();
            var tcs = new TaskCompletionSource<int>();

            recognizer.Recognized += (s, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech)
                {
                    // For basic implementation, we use Speaker1/Speaker2 format
                    // In production with proper diarization service, you would get actual speaker IDs
                    string speakerId = "Speaker1";
                    
                    transcriptBuilder.AppendLine($"{speakerId}: {e.Result.Text}");
                    _logger.LogInformation($"Transcribed: {speakerId}: {e.Result.Text}");
                }
                else if (e.Result.Reason == ResultReason.NoMatch)
                {
                    _logger.LogWarning($"NOMATCH: Speech could not be recognized.");
                }
            };

            recognizer.Canceled += (s, e) =>
            {
                _logger.LogError($"Transcription canceled: {e.Reason}");
                if (e.Reason == CancellationReason.Error)
                {
                    _logger.LogError($"Error details: {e.ErrorCode} - {e.ErrorDetails}");
                }
                tcs.TrySetResult(0);
            };

            recognizer.SessionStopped += (s, e) =>
            {
                _logger.LogInformation("Transcription session stopped.");
                tcs.TrySetResult(0);
            };

            await recognizer.StartContinuousRecognitionAsync();
            await tcs.Task;
            await recognizer.StopContinuousRecognitionAsync();

            return transcriptBuilder.ToString();
        }

        private async Task UploadTranscript(string connectionString, string originalFileName, string transcript)
        {
            var blobServiceClient = new BlobServiceClient(connectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient("transcripts");
            
            // Ensure the container exists
            await containerClient.CreateIfNotExistsAsync();

            // Create a blob name based on the original file name
            var transcriptFileName = Path.ChangeExtension(originalFileName, ".txt");
            var blobClient = containerClient.GetBlobClient(transcriptFileName);

            // Upload the transcript
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(transcript));
            await blobClient.UploadAsync(stream, overwrite: true);

            _logger.LogInformation($"Transcript uploaded to blob: {transcriptFileName}");
        }
    }
}
