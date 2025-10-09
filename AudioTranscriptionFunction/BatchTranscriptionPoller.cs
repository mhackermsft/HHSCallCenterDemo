using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Queues; // added for re-enqueue
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AudioTranscriptionFunction
{
    public class BatchTranscriptionPoller
    {
        private readonly ILogger<BatchTranscriptionPoller> _logger;
        private static readonly HttpClient _http = new HttpClient();
        private const string TranscriptOutputContainer = "transcript-output";
        private const string TranscriptionJobsQueue = "transcription-jobs";
        private const int MaxPollingAttempts = 240; // safety cap 

        /// <summary>
        /// Creates a new poller instance used by the Azure Function runtime. The logger is injected
        /// by dependency injection and reused for all operations inside this function execution.
        /// </summary>
        /// <param name="logger">Logging abstraction for diagnostic output.</param>
        public BatchTranscriptionPoller(ILogger<BatchTranscriptionPoller> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Function entry point triggered by messages appearing on the <c>transcription-jobs</c> queue.
        /// Each message represents a transcription job previously submitted to Azure Speech.
        /// The method:
        /// 1. Decodes the queue message (supports raw JSON or base64 encoded JSON).
        /// 2. Fetches current job status from the Speech REST API.
        /// 3. Reacts based on status:
        ///    - NotStarted/Running: re-enqueues the message with a visibility delay to poll again later.
        ///    - Failed: logs detailed error information and throws so the message can move toward the poison queue.
        ///    - Succeeded: downloads transcription files, extracts phrases, builds a plain text transcript, and stores it in Blob Storage.
        /// Any transient issue throws a custom <see cref="RetryPollingException"/> so the runtime retries the message.
        /// </summary>
        /// <param name="message">Queue payload containing job metadata such as JobId and BlobName (may be base64).</param>
        /// <exception cref="InvalidOperationException">Thrown for unrecoverable problems (e.g. bad payload, exceeded attempts).</exception>
        /// <remarks>Retry semantics are controlled by throwing; the Azure Functions runtime will handle retries / poison routing.</remarks>
        [Function(nameof(BatchTranscriptionPoller))]
        public async Task Run([QueueTrigger("transcription-jobs", Connection = "AzureWebJobsStorage")] string message)
        {
            _logger.LogInformation("[Poller] Start raw length={Len} snippet='{Snippet}'", message?.Length, Truncate(message, 150));

            if (!string.IsNullOrWhiteSpace(message) && IsLikelyBase64(message))
            {
                try
                {
                    var bytes = Convert.FromBase64String(message);
                    var decoded = Encoding.UTF8.GetString(bytes);
                    if (decoded.StartsWith("{")) message = decoded;
                }
                catch { }
            }

            var payload = TryDeserializeMessage(message);
            if (payload == null)
            {
                _logger.LogError("[Poller] Deserialize failed; abandoning message.");
                // Throw so the message is retried (could also move to poison eventually)
                throw new InvalidOperationException("Invalid queue message payload");
            }

            try
            {
                var speechKey = Environment.GetEnvironmentVariable("SpeechServiceKey");
                var speechRegion = Environment.GetEnvironmentVariable("SpeechServiceRegion");
                var storage = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
                if (string.IsNullOrWhiteSpace(speechKey) || string.IsNullOrWhiteSpace(speechRegion) || string.IsNullOrWhiteSpace(storage))
                {
                    _logger.LogError("[Poller {JobId}] Missing configuration.", payload.JobId);
                    throw new InvalidOperationException("Missing configuration");
                }

                var jobUrl = $"https://{speechRegion}.api.cognitive.microsoft.com/speechtotext/v3.1/transcriptions/{payload.JobId}";
                using var req = new HttpRequestMessage(HttpMethod.Get, jobUrl);
                req.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);
                using var resp = await _http.SendAsync(req);
                var raw = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[Poller {JobId}] Job GET HTTP {Status}; will retry", payload.JobId, (int)resp.StatusCode);
                    ThrowRetry(payload, "Job HTTP non-success");
                }

                JsonElement root;
                try
                { using var doc = JsonDocument.Parse(raw); root = doc.RootElement.Clone(); }
                catch
                { _logger.LogWarning("[Poller {JobId}] Could not parse job JSON; retrying", payload.JobId); ThrowRetry(payload, "Parse job JSON"); return; }

                if (!root.TryGetProperty("status", out var statusProp) || statusProp.ValueKind != JsonValueKind.String)
                { _logger.LogWarning("[Poller {JobId}] Missing status property; retrying", payload.JobId); ThrowRetry(payload, "Missing status"); }
                var status = statusProp.GetString();

                _logger.LogInformation("[Poller {JobId}] Transcription Status: {Status}", payload.JobId, status);
                switch (status)
                {
                    case "NotStarted":
                    case "Running":
                        await RequeueAndDelayAsync(storage!, payload, status!);
                        return; // successful completion so the current dequeue is done
                    case "Failed":
                        LogFailureDetails(root, payload.JobId, raw);
                        // Fail permanently (let move to poison after maxDequeueCount)
                        throw new InvalidOperationException("Transcription job failed");
                    case "Succeeded":
                        await ProcessSucceededAsync(root, storage!, payload, speechRegion!, speechKey!);
                        _logger.LogInformation("[Poller {JobId}] Completed processing transcript successfully.", payload.JobId);
                        break;
                    default:
                        _logger.LogWarning("[Poller {JobId}] Unknown status {Status}; retrying", payload.JobId, status);
                        ThrowRetry(payload, "Unknown status");
                        break;
                }
            }
            catch (RetryPollingException)
            {
                // Allow to bubble so the runtime does NOT complete the message
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Poller {JobId}] Unhandled exception - will retry", payload?.JobId);
                throw; // ensure queue message not completed
            }
        }

        /// <summary>
        /// Re-enqueues the transcription job message for a future poll when the job is still in progress.
        /// Increments the attempt counter and applies a fixed 60 second visibility delay. If attempts exceed
        /// <see cref="MaxPollingAttempts"/>, the method throws to stop further polling.
        /// </summary>
        /// <param name="storageConnection">Connection string for Azure Storage (queue + blobs).</param>
        /// <param name="payload">The queue message payload being re-enqueued; its Attempts value is incremented.</param>
        /// <param name="status">Current transcription job status (Running or NotStarted).</param>
        /// <exception cref="InvalidOperationException">If maximum polling attempts have been exceeded.</exception>
        /// <remarks>Uses Base64 encoding for the re-enqueued message to avoid issues with special characters.</remarks>
        private async Task RequeueAndDelayAsync(string storageConnection, TranscriptionQueueMessage payload, string status)
        {
            payload.Attempts++;
            if (payload.Attempts > MaxPollingAttempts)
            {
                _logger.LogError("[Poller {JobId}] Exceeded max polling attempts ({Attempts}); aborting.", payload.JobId, payload.Attempts);
                throw new InvalidOperationException("Max polling attempts exceeded");
            }

            // Fixed interval backoff: always 60 seconds
            var delay = TimeSpan.FromMinutes(1);

            try
            {
                var queueClient = new QueueClient(storageConnection, TranscriptionJobsQueue);
                await queueClient.CreateIfNotExistsAsync();
                var json = JsonSerializer.Serialize(payload);
                var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                await queueClient.SendMessageAsync(base64, visibilityTimeout: delay);
                _logger.LogInformation("[Poller {JobId}] Re-enqueued (status={Status}) attempt={Attempt} nextDelaySec={Delay}",
                    payload.JobId, status, payload.Attempts, delay.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Poller {JobId}] Failed re-enqueue; will fallback to throw for retry", payload.JobId);
                ThrowRetry(payload, "Re-enqueue failure");
            }
        }

        /// <summary>
        /// Attempts to deserialize the JSON queue message into a <see cref="TranscriptionQueueMessage"/> object.
        /// Returns null if the payload is invalid JSON or does not conform to the expected shape.
        /// </summary>
        /// <param name="message">Raw JSON (or already-decoded) queue message.</param>
        /// <returns>The strongly typed payload or null on failure.</returns>
        private TranscriptionQueueMessage? TryDeserializeMessage(string message)
        { try { return JsonSerializer.Deserialize<TranscriptionQueueMessage>(message); } catch { return null; } }

        /// <summary>
        /// Handles the success path for a completed transcription job. This includes:
        /// 1. Locating the files list endpoint (from the job links or constructing a fallback URL).
        /// 2. Downloading the list of output files and selecting the transcription file entry.
        /// 3. Downloading the transcription JSON via the file's content URL.
        /// 4. Extracting and ordering recognized phrases.
        /// 5. Building a human-readable transcript (with speaker labels) and writing it to Blob Storage as a .txt file.
        /// Any transient or recoverable issue triggers a retry by throwing a <see cref="RetryPollingException"/>.
        /// </summary>
        /// <param name="jobRoot">JSON root for the job status response.</param>
        /// <param name="storage">Azure Storage connection string for blob uploads.</param>
        /// <param name="payload">Original queue message payload with metadata (JobId, BlobName, etc.).</param>
        /// <param name="speechRegion">Azure Speech region used for constructing fallback URLs.</param>
        /// <param name="speechKey">Azure Speech subscription key (used for authenticated REST calls).</param>
        /// <remarks>The generated transcript begins with a topic line that can later guide AI analysis.</remarks>
        private async Task ProcessSucceededAsync(JsonElement jobRoot, string storage, TranscriptionQueueMessage payload, string speechRegion, string speechKey)
        {
            _logger.LogInformation("[Poller {JobId}] Processing succeeded job.", payload.JobId);

            string? filesListUrl = null;
            if (jobRoot.TryGetProperty("links", out var linksProp) &&
                linksProp.ValueKind == JsonValueKind.Object &&
                linksProp.TryGetProperty("files", out var filesUrlProp) &&
                filesUrlProp.ValueKind == JsonValueKind.String)
            {
                filesListUrl = filesUrlProp.GetString();
                _logger.LogInformation("[Poller {JobId}] Found files list link: {FilesUrl}", payload.JobId, filesListUrl);
            }
            else
            {
                filesListUrl = $"https://{speechRegion}.api.cognitive.microsoft.com/speechtotext/v3.1/transcriptions/{payload.JobId}/files";
                _logger.LogInformation("[Poller {JobId}] Constructed fallback files list URL: {FilesUrl}", payload.JobId, filesListUrl);
            }

            if (string.IsNullOrWhiteSpace(filesListUrl)) { _logger.LogWarning("[Poller {JobId}] Files list URL empty.", payload.JobId); ThrowRetry(payload, "Empty files list URL"); }

            JsonElement filesRoot;
            try
            {
                using var filesReq = new HttpRequestMessage(HttpMethod.Get, filesListUrl);
                filesReq.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);
                using var filesResp = await _http.SendAsync(filesReq);
                var statusCode = (int)filesResp.StatusCode;
                var filesRaw = await filesResp.Content.ReadAsStringAsync();
                _logger.LogInformation("[Poller {JobId}] Files list HTTP {Status} length={Len}", payload.JobId, statusCode, filesRaw.Length);
                if (!filesResp.IsSuccessStatusCode)
                { ThrowRetry(payload, "Files list HTTP"); }
                using var filesDoc = JsonDocument.Parse(filesRaw);
                filesRoot = filesDoc.RootElement.Clone();
            }
            catch (RetryPollingException) { throw; }
            catch (Exception ex)
            { _logger.LogWarning(ex, "[Poller {JobId}] Exception retrieving files list", payload.JobId); ThrowRetry(payload, "Files list exception"); return; }

            string? transcriptionContentUrl = null;
            int fileCount = 0;
            if (filesRoot.TryGetProperty("values", out var valuesProp) && valuesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var fileEntry in valuesProp.EnumerateArray())
                {
                    fileCount++;
                    var kindOk = fileEntry.TryGetProperty("kind", out var kindProp) && kindProp.ValueKind == JsonValueKind.String;
                    var kindValue = kindOk ? kindProp.GetString() : "(null)";
                    if (kindValue == "Transcription")
                    {
                        if (fileEntry.TryGetProperty("links", out var fileLinks) && fileLinks.TryGetProperty("contentUrl", out var cu) && cu.ValueKind == JsonValueKind.String)
                        {
                            transcriptionContentUrl = cu.GetString();
                            _logger.LogInformation("[Poller {JobId}] Selected transcription file; contentUrl length={Len}", payload.JobId, transcriptionContentUrl?.Length);
                            break;
                        }
                    }
                }
            }
            _logger.LogInformation("[Poller {JobId}] Files enumerated count={Count}", payload.JobId, fileCount);

            if (string.IsNullOrWhiteSpace(transcriptionContentUrl))
            { _logger.LogWarning("[Poller {JobId}] No transcription contentUrl found.", payload.JobId); ThrowRetry(payload, "No transcription file"); }

            string transcriptJson;
            try
            {
                using var txReq = new HttpRequestMessage(HttpMethod.Get, transcriptionContentUrl);
                txReq.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);
                using var txResp = await _http.SendAsync(txReq);
                var httpStatus = (int)txResp.StatusCode;
                transcriptJson = await txResp.Content.ReadAsStringAsync();
                _logger.LogInformation("[Poller {JobId}] Download transcription status={Status} jsonLength={Len}", payload.JobId, httpStatus, transcriptJson.Length);
                if (!txResp.IsSuccessStatusCode) { ThrowRetry(payload, "Download transcription HTTP"); }
            }
            catch (RetryPollingException) { throw; }
            catch (Exception ex)
            { _logger.LogWarning(ex, "[Poller {JobId}] Exception downloading transcription", payload.JobId); ThrowRetry(payload, "Download exception"); return; }

            JsonElement resultRoot;
            try { using var doc = JsonDocument.Parse(transcriptJson); resultRoot = doc.RootElement.Clone(); }
            catch (Exception ex)
            { _logger.LogWarning(ex, "[Poller {JobId}] Failed parsing transcription JSON", payload.JobId); ThrowRetry(payload, "Parse transcription JSON"); return; }

            JsonElement phrasesArray;
            if (!(resultRoot.TryGetProperty("recognizedPhrases", out phrasesArray) && phrasesArray.ValueKind == JsonValueKind.Array))
            {
                if (!(resultRoot.TryGetProperty("combinedRecognizedPhrases", out phrasesArray) && phrasesArray.ValueKind == JsonValueKind.Array))
                { _logger.LogWarning("[Poller {JobId}] No phrases arrays present.", payload.JobId); ThrowRetry(payload, "No phrases"); return; }
            }

            var phrases = phrasesArray.EnumerateArray()
                .Select(p => new RecognizedPhrase
                {
                    Speaker = p.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetInt32() : -1,
                    Offset = p.TryGetProperty("offset", out var of) && of.ValueKind == JsonValueKind.Number ? of.GetInt64() : 0,
                    Text = p.TryGetProperty("nBest", out var nbest) && nbest.ValueKind == JsonValueKind.Array && nbest.GetArrayLength() > 0 ? SafeGetDisplay(nbest[0]) : (p.TryGetProperty("display", out var disp) && disp.ValueKind == JsonValueKind.String ? disp.GetString() ?? string.Empty : string.Empty)
                })
                .Where(p => !string.IsNullOrWhiteSpace(p.Text))
                .OrderBy(p => p.Offset)
                .ToList();

            _logger.LogInformation("[Poller {JobId}] Extracted phrases count={Count}", payload.JobId, phrases.Count);
            if (phrases.Count == 0)
            { _logger.LogWarning("[Poller {JobId}] No phrases extracted; retrying", payload.JobId); ThrowRetry(payload, "Zero phrases"); }

            var speakerMap = new System.Collections.Generic.Dictionary<int, string>();
            int seq = 1; var sb = new StringBuilder();
            // Added topic header as first line of transcript
            // NOTE: the topic should be set to the workflow topic so we
            // know what set of questions to use during AI analysis.
            sb.AppendLine("Topic: generic");
            foreach (var ph in phrases)
            {
                if (!speakerMap.TryGetValue(ph.Speaker, out var label)) { label = ph.Speaker >= 0 ? $"Speaker {seq++}" : "Speaker ?"; speakerMap[ph.Speaker] = label; }
                sb.AppendLine($"{label}: {ph.Text}");
            }

            var transcriptText = sb.ToString();
            _logger.LogInformation("[Poller {JobId}] Final transcript length={Len}", payload.JobId, transcriptText.Length);

            var blobServiceClient = new BlobServiceClient(storage);
            var containerClient = blobServiceClient.GetBlobContainerClient(TranscriptOutputContainer);
            await containerClient.CreateIfNotExistsAsync();
            var transcriptFileName = System.IO.Path.ChangeExtension(payload.BlobName, ".txt");
            var blobClient = containerClient.GetBlobClient(transcriptFileName);
            using var ms = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(transcriptText));
            await blobClient.UploadAsync(ms, overwrite: true);
            _logger.LogInformation("[Poller {JobId}] Transcript uploaded container={Container} blob={Blob}", payload.JobId, TranscriptOutputContainer, transcriptFileName);
        }

        /// <summary>
        /// Extracts and logs details about a failed transcription job, including the first error code and message
        /// (when present). Helps with diagnosing why a job ended in the Failed state.
        /// </summary>
        /// <param name="root">JSON root containing potential errors array.</param>
        /// <param name="jobId">The transcription job identifier.</param>
        /// <param name="raw">Raw JSON string snippet for troubleshooting.</param>
        private void LogFailureDetails(JsonElement root, string jobId, string raw)
        {
            string code = string.Empty, msg = string.Empty;
            try
            {
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                {
                    var first = errors[0];
                    if (first.TryGetProperty("code", out var c)) code = c.GetString() ?? string.Empty;
                    if (first.TryGetProperty("message", out var m)) msg = m.GetString() ?? string.Empty;
                }
            }
            catch { }
            _logger.LogError("[Poller {JobId}] FAILED Code={Code} Msg={Msg} RawSnippet={Snippet}", jobId, code, msg, Truncate(raw, 220));
        }

        /// <summary>
        /// Throws a custom exception type indicating the poller should retry. Using a distinct exception type makes it
        /// easy to differentiate between conditional retries and unexpected faults.
        /// </summary>
        /// <param name="payload">The current queue message payload (included for context in the exception message).</param>
        /// <param name="reason">Short description of why a retry is needed.</param>
        private void ThrowRetry(TranscriptionQueueMessage payload, string reason)
            => throw new RetryPollingException($"Retry transcription polling {payload.JobId}: {reason}");

        /// <summary>
        /// Heuristically determines whether a string looks like base64 data. This avoids attempting to decode obviously
        /// non-base64 messages (e.g., raw JSON) and prevents exceptions for common invalid inputs.
        /// </summary>
        /// <param name="input">Candidate string.</param>
        /// <returns>True if the string length is a multiple of 4 and all characters are valid base64 characters; otherwise false.</returns>
        private bool IsLikelyBase64(string input)
        { if (input.Length % 4 != 0) return false; foreach (var c in input) if (!(char.IsLetterOrDigit(c) || c=='+'||c=='/'||c=='=')) return false; return true; }

        /// <summary>
        /// Safely extracts the display text from an nBest hypothesis element. Falls back to lexical form when display
        /// is absent. Returns empty string when neither is found. Prevents KeyNotFound handling clutter.
        /// </summary>
        /// <param name="nBest0">First element of the nBest array for a recognized phrase.</param>
        /// <returns>Display or lexical text; empty string if neither is present.</returns>
        private static string SafeGetDisplay(JsonElement nBest0)
        { if (nBest0.TryGetProperty("display", out var disp) && disp.ValueKind == JsonValueKind.String) return disp.GetString() ?? string.Empty; if (nBest0.TryGetProperty("lexical", out var lex) && lex.ValueKind == JsonValueKind.String) return lex.GetString() ?? string.Empty; return string.Empty; }

        /// <summary>
        /// Truncates an input string to a maximum length for logging, adding ellipsis when the original exceeds the limit.
        /// Prevents log flooding while still offering context for debugging.
        /// </summary>
        /// <param name="v">Original string (may be null).</param>
        /// <param name="m">Maximum length before truncation.</param>
        /// <returns>Original string if shorter than or equal to limit; otherwise a truncated version with ellipsis.</returns>
        private static string Truncate(string? v, int m) => string.IsNullOrEmpty(v) ? string.Empty : (v.Length <= m ? v : v.Substring(0, m) + "...");

        /// <summary>
        /// Internal representation of a phrase extracted from the transcription JSON.
        /// Contains speaker identifier, offset (ordering), and the recognized text.
        /// </summary>
        private class RecognizedPhrase { public int Speaker { get; set; } public long Offset { get; set; } public string Text { get; set; } = string.Empty; }

        /// <summary>
        /// Custom exception used strictly to signal intentional polling retries (transient state or recoverable issues).
        /// Distinguishes expected retry flow from unexpected errors which also cause retries but should be logged differently.
        /// </summary>
        private class RetryPollingException : Exception { public RetryPollingException(string msg) : base(msg) { } }
    }
}
