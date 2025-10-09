using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AudioTranscriptionFunction
{
    public class BatchTranscriptionSubmitFunction
    {
        private readonly ILogger<BatchTranscriptionSubmitFunction> _logger;
        private static readonly HttpClient _http = new HttpClient();
        private const string TranscriptionJobsQueue = "transcription-jobs"; // queue name

        public BatchTranscriptionSubmitFunction(ILogger<BatchTranscriptionSubmitFunction> logger)
        {
            _logger = logger;
        }

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

        private void LogConfigPresence(string inv, string? speechKey, string? speechRegion, string? storage, string locale)
        {
            _logger.LogInformation("[{Inv}] Config SpeechKeyPresent={Key} Region={Region} StoragePresent={StoragePresent} Locale={Locale}",
                inv,
                string.IsNullOrEmpty(speechKey) ? "false" : "true",
                speechRegion ?? "(null)",
                string.IsNullOrEmpty(storage) ? "false" : "true",
                locale);
        }

        private static string Truncate(string value, int max)
            => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value.Substring(0, max) + "...");

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
