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

        public BatchTranscriptionPoller(ILogger<BatchTranscriptionPoller> logger)
        {
            _logger = logger;
        }

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

        private TranscriptionQueueMessage? TryDeserializeMessage(string message)
        { try { return JsonSerializer.Deserialize<TranscriptionQueueMessage>(message); } catch { return null; } }

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

        private void ThrowRetry(TranscriptionQueueMessage payload, string reason)
            => throw new RetryPollingException($"Retry transcription polling {payload.JobId}: {reason}");

        private bool IsLikelyBase64(string input)
        { if (input.Length % 4 != 0) return false; foreach (var c in input) if (!(char.IsLetterOrDigit(c) || c=='+'||c=='/'||c=='=')) return false; return true; }

        private static string SafeGetDisplay(JsonElement nBest0)
        { if (nBest0.TryGetProperty("display", out var disp) && disp.ValueKind == JsonValueKind.String) return disp.GetString() ?? string.Empty; if (nBest0.TryGetProperty("lexical", out var lex) && lex.ValueKind == JsonValueKind.String) return lex.GetString() ?? string.Empty; return string.Empty; }

        private static string Truncate(string? v, int m) => string.IsNullOrEmpty(v) ? string.Empty : (v.Length <= m ? v : v.Substring(0, m) + "...");

        private class RecognizedPhrase { public int Speaker { get; set; } public long Offset { get; set; } public string Text { get; set; } = string.Empty; }
        private class RetryPollingException : Exception { public RetryPollingException(string msg) : base(msg) { } }
    }
}
