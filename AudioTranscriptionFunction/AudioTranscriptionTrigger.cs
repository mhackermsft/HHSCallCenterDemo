using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Storage.Sas;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AudioTranscriptionFunction
{
    public class BatchTranscriptionSubmitFunction
    {
        private readonly ILogger<BatchTranscriptionSubmitFunction> _logger;
        private static readonly HttpClient _http = new HttpClient();
        private const string TranscriptionJobsQueue = "transcription-jobs"; // queue name

        /// <summary>
        /// Constructs the submit function instance. The logger is provided by the Azure Functions DI system
        /// and is used throughout the lifecycle of a single blob-triggered invocation.
        /// </summary>
        /// <param name="logger">Structured logger for diagnostics.</param>
        public BatchTranscriptionSubmitFunction(ILogger<BatchTranscriptionSubmitFunction> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Azure Function entry point triggered whenever a new audio blob is uploaded to the 'audio-input' container.
        /// Responsibilities:
        /// 1. Validate required configuration (Speech key/region, storage account) and ensure not using Azurite.
        /// 2. Generate a short-lived (30 min) account SAS URL for the uploaded audio blob so Speech service can fetch it.
        /// 3. Optionally preflight the SAS URL with an HTTP HEAD for early failure detection.
        /// 4. Submit a batch transcription job to Azure Speech (REST API) with diarization & timestamps enabled.
        /// 5. Enqueue a polling message to the 'transcription-jobs' queue with initial metadata for later status polling.
        /// Any fatal error is logged and rethrown to allow retry semantics (unless configuration is incomplete, in which case it exits early).
        /// </summary>
        /// <param name="_">Unused blob bytes (the trigger binding supplies them but we only need the blob name).</param>
        /// <param name="name">Name of the uploaded blob (used for SAS generation and job display name).</param>
        [Function(nameof(BatchTranscriptionSubmitFunction))]
        public async Task Run(
            [BlobTrigger("audio-input/{name}", Connection = "AzureWebJobsStorage")] byte[] _,
            string name)
        {
            var invocationId = Guid.NewGuid().ToString("N");
            _logger.LogInformation("[{Inv}] Batch submit triggered for audio blob: {Name}", invocationId, name);

            var speechKey = Environment.GetEnvironmentVariable("SpeechServiceKey");
            var speechRegion = Environment.GetEnvironmentVariable("SpeechServiceRegion");
            var storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            var locale = Environment.GetEnvironmentVariable("SpeechTranscriptionLocale") ?? "en-US";

            LogConfigPresence(invocationId, speechKey, speechRegion, storageConnectionString, locale);

            if (string.IsNullOrWhiteSpace(speechKey) || string.IsNullOrWhiteSpace(speechRegion))
            {
                _logger.LogError("[{Inv}] Missing Speech service configuration.", invocationId);
                return;
            }
            if (string.IsNullOrWhiteSpace(storageConnectionString))
            {
                _logger.LogError("[{Inv}] Missing storage connection string.", invocationId);
                return;
            }
            if (storageConnectionString.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("[{Inv}] UseDevelopmentStorage=true detected. Speech cannot pull from Azurite.", invocationId);
                return;
            }

            var swTotal = Stopwatch.StartNew();
            try
            {
                var blobServiceClient = new BlobServiceClient(storageConnectionString);
                var containerClient = blobServiceClient.GetBlobContainerClient("audio-input");
                var blobClient = containerClient.GetBlobClient(name);
                var exists = await blobClient.ExistsAsync();
                if (!exists.Value)
                {
                    _logger.LogError("[{Inv}] Blob not found: {Name}", invocationId, name);
                    return;
                }

                string sasUrl;
                try
                {
                    sasUrl = GetAccountSasUrl(storageConnectionString, blobClient.BlobContainerName, blobClient.Name, TimeSpan.FromMinutes(30));
                    _logger.LogInformation("[{Inv}] Account SAS generated length={Len} preview={Preview}", invocationId, sasUrl.Length, TruncateSensitive(sasUrl, 60));
                }
                catch (Exception sasEx)
                {
                    _logger.LogError(sasEx, "[{Inv}] Failed generating SAS.", invocationId);
                    return;
                }

                if (!await PreflightHeadAsync(sasUrl, invocationId))
                {
                    _logger.LogError("[{Inv}] SAS preflight failed.", invocationId);
                    return;
                }

                string jobId = await SubmitBatchJobAsync(speechKey!, speechRegion!, sasUrl, locale, name, invocationId);
                _logger.LogInformation("[{Inv}] Submitted transcription job {JobId}", invocationId, jobId);

                try
                {
                    var queueClient = new QueueClient(storageConnectionString, TranscriptionJobsQueue);
                    await queueClient.CreateIfNotExistsAsync();
                    var messageObj = new TranscriptionQueueMessage
                    {
                        JobId = jobId,
                        BlobName = name,
                        Locale = locale,
                        Attempts = 0,
                        SubmittedUtc = DateTime.UtcNow
                    };
                    var msgJson = JsonSerializer.Serialize(messageObj);
                    var msgBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(msgJson));
                    await queueClient.SendMessageAsync(msgBase64);
                    _logger.LogInformation("[{Inv}] Queued job {JobId}", invocationId, jobId);
                }
                catch (Exception qex)
                {
                    _logger.LogError(qex, "[{Inv}] Queue send failed jobId={JobId}", invocationId, jobId);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Inv}] Submission failure for {Name}", invocationId, name);
                throw;
            }
            finally
            {
                swTotal.Stop();
                _logger.LogInformation("[{Inv}] Submission complete totalElapsed={Ms}ms", invocationId, swTotal.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// Generates a shared access signature (SAS) URL for a specific blob using the account key from the connection string.
        /// Grants read-only access for the specified TTL so the Speech service can download the audio content.
        /// </summary>
        /// <param name="connectionString">Full storage account connection string containing AccountName and AccountKey.</param>
        /// <param name="container">Container name hosting the blob.</param>
        /// <param name="blobName">Target blob name.</param>
        /// <param name="ttl">Time-to-live for generated SAS.</param>
        /// <returns>Absolute SAS URL to the blob with query parameters.</returns>
        /// <exception cref="InvalidOperationException">Thrown when connection string cannot be parsed.</exception>
        private string GetAccountSasUrl(string connectionString, string container, string blobName, TimeSpan ttl)
        {
            // Create a service client from the connection string
            var serviceClient = new BlobServiceClient(connectionString);
            var blobClient = serviceClient.GetBlobContainerClient(container).GetBlobClient(blobName);

            // Parse account key from connection string via StorageSharedKeyCredential
            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = container,
                BlobName = blobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.Add(ttl)
            };
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            // Need StorageSharedKeyCredential; easiest is to reconstruct from connection string parts
            var (accountName, key) = ParseConnectionString(connectionString);
            var cred = new Azure.Storage.StorageSharedKeyCredential(accountName, key);
            var sas = sasBuilder.ToSasQueryParameters(cred).ToString();
            return blobClient.Uri + "?" + sas;
        }

        /// <summary>
        /// Extracts the account name and key from a standard storage connection string.
        /// </summary>
        /// <param name="connectionString">Storage connection string with AccountName and AccountKey segments.</param>
        /// <returns>Tuple containing account name and key.</returns>
        /// <exception cref="InvalidOperationException">If either segment is missing.</exception>
        private (string accountName, string key) ParseConnectionString(string connectionString)
        {
            string? accountName = null;
            string? key = null;
            var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts)
            {
                if (p.StartsWith("AccountName=", StringComparison.OrdinalIgnoreCase)) accountName = p.Substring("AccountName=".Length);
                else if (p.StartsWith("AccountKey=", StringComparison.OrdinalIgnoreCase)) key = p.Substring("AccountKey=".Length);
            }
            if (string.IsNullOrEmpty(accountName) || string.IsNullOrEmpty(key)) throw new InvalidOperationException("Connection string missing AccountName or AccountKey");
            return (accountName, key);
        }

        /// <summary>
        /// Issues an HTTP HEAD request against the SAS URL to ensure the blob is reachable before submitting the
        /// transcription job. Helps surface permission or network issues early.
        /// </summary>
        /// <param name="sasUrl">Generated blob SAS URL.</param>
        /// <param name="invocationId">Correlation ID for logging.</param>
        /// <returns>True if the HEAD request succeeded (2xx), otherwise false.</returns>
        private async Task<bool> PreflightHeadAsync(string sasUrl, string invocationId)
        {
            try
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, sasUrl);
                var sw = Stopwatch.StartNew();
                using var headResp = await _http.SendAsync(headReq);
                sw.Stop();
                _logger.LogInformation("[{Inv}] Preflight HEAD status={Status} elapsed={Ms}ms", invocationId, (int)headResp.StatusCode, sw.ElapsedMilliseconds);
                return headResp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Inv}] Preflight HEAD failed", invocationId);
                return false;
            }
        }

        /// <summary>
        /// Submits the batch transcription job to Azure Speech using REST. Configures diarization, word timestamps,
        /// punctuation, and profanity filter. Returns the job ID parsed from the response body or Location header.
        /// Throws if submission fails or a job ID cannot be determined.
        /// </summary>
        /// <param name="speechKey">Speech subscription key.</param>
        /// <param name="region">Speech region (e.g., eastus).</param>
        /// <param name="contentUrl">SAS URL pointing to the audio blob.</param>
        /// <param name="locale">Language/locale for transcription (e.g., en-US).</param>
        /// <param name="originalFile">Original blob name (used for displayName).</param>
        /// <param name="invocationId">Correlation ID for log tracing.</param>
        /// <returns>The transcription job identifier (GUID string).</returns>
        private async Task<string> SubmitBatchJobAsync(string speechKey, string region, string contentUrl, string locale, string originalFile, string invocationId)
        {
            var endpoint = $"https://{region}.api.cognitive.microsoft.com/speechtotext/v3.1/transcriptions";
            var body = new
            {
                displayName = $"Call_{originalFile}",
                description = "HHS Call Center Batch Transcription",
                locale = locale,
                contentUrls = new[] { contentUrl },
                properties = new
                {
                    diarizationEnabled = true,
                    wordLevelTimestampsEnabled = true,
                    punctuationMode = "DictatedAndAutomatic",
                    profanityFilterMode = "Masked"
                }
            };

            var json = JsonSerializer.Serialize(body);
            _logger.LogInformation("[{Inv}] Submitting batch job endpoint={Endpoint} payloadSize={Size}", invocationId, endpoint, json.Length);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);

            using var response = await _http.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("[{Inv}] Speech submit response status={Status} bodyLen={Len}", invocationId, (int)response.StatusCode, raw.Length);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[{Inv}] Batch submit failed HTTP {Status} snippet={Snippet}", invocationId, (int)response.StatusCode, Truncate(raw, 200));
                response.EnsureSuccessStatusCode();
            }

            string? jobId = null;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("id", out var idProp)) jobId = idProp.GetString();
                }
                catch { }
            }
            if (string.IsNullOrEmpty(jobId))
            {
                var location = response.Headers.Location?.ToString();
                if (!string.IsNullOrEmpty(location))
                {
                    var lastSegment = location.TrimEnd('/').Split('/').Last();
                    if (Guid.TryParse(lastSegment, out _)) jobId = lastSegment;
                }
            }
            if (string.IsNullOrEmpty(jobId)) throw new InvalidOperationException("No transcription job id returned.");
            return jobId;
        }

        /// <summary>
        /// Logs presence/absence of critical configuration elements (Speech key/region, storage) and the selected locale.
        /// Helps quickly diagnose missing app settings in logs without outputting sensitive values.
        /// </summary>
        /// <param name="inv">Invocation correlation ID.</param>
        /// <param name="speechKey">Speech key (presence only logged).</param>
        /// <param name="speechRegion">Speech region string.</param>
        /// <param name="storage">Storage connection string (presence only logged).</param>
        /// <param name="locale">Locale used for transcription.</param>
        private void LogConfigPresence(string inv, string? speechKey, string? speechRegion, string? storage, string locale)
        {
            _logger.LogInformation("[{Inv}] Config SpeechKeyPresent={Key} Region={Region} StoragePresent={StoragePresent} Locale={Locale}",
                inv,
                string.IsNullOrEmpty(speechKey) ? "false" : "true",
                speechRegion ?? "(null)",
                string.IsNullOrEmpty(storage) ? "false" : "true",
                locale);
        }

        /// <summary>
        /// Truncates a string for safe logging, appending ellipsis when exceeding the specified length.
        /// </summary>
        private static string Truncate(string value, int max)
            => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value.Substring(0, max) + "...");

        /// <summary>
        /// Produces a partially masked representation of a potentially sensitive query string (e.g., SAS URL)
        /// preserving base path and concealing most of the signature.
        /// </summary>
        private static string TruncateSensitive(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Length <= max) return value;
            var idx = value.IndexOf('?');
            if (idx > 0)
            {
                var basePart = value.Substring(0, Math.Min(idx + 1, max / 2));
                return basePart + "***";
            }
            return value.Substring(0, max) + "***";
        }
    }

    internal class TranscriptionQueueMessage
    {
        public string JobId { get; set; } = string.Empty;
        public string BlobName { get; set; } = string.Empty;
        public string Locale { get; set; } = "en-US";
        public int Attempts { get; set; }
        public DateTime SubmittedUtc { get; set; }
    }
}
