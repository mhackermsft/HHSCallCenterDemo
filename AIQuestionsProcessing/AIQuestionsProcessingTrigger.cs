using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Globalization;

namespace AIQuestionsProcessing
{
    public class AIQuestionsProcessingTrigger
    {
        private readonly ILogger<AIQuestionsProcessingTrigger> _logger;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public AIQuestionsProcessingTrigger(ILogger<AIQuestionsProcessingTrigger> logger)
        {
            _logger = logger;
        }

        [Function(nameof(AIQuestionsProcessingTrigger))]
        public async Task Run(
            [BlobTrigger("transcript-output/{name}", Connection = "TranscriptsStorage")] Stream transcriptStream,
            string name)
        {
            try
            {
                _logger.LogInformation("Processing transcript: {Name}", name);

                var transcriptsConnectionString = Environment.GetEnvironmentVariable("TranscriptsStorage");
                if (string.IsNullOrWhiteSpace(transcriptsConnectionString))
                {
                    throw new InvalidOperationException("TranscriptsStorage connection string not configured");
                }
                var resultsConnectionString = Environment.GetEnvironmentVariable("ResultsStorage") ?? transcriptsConnectionString;

                var aoaiEndpoint = Environment.GetEnvironmentVariable("AzureOpenAI__Endpoint");
                var aoaiApiKey = Environment.GetEnvironmentVariable("AzureOpenAI__ApiKey");
                var aoaiDeployment = Environment.GetEnvironmentVariable("AzureOpenAI__DeploymentName");
                if (string.IsNullOrWhiteSpace(aoaiEndpoint) || string.IsNullOrWhiteSpace(aoaiApiKey) || string.IsNullOrWhiteSpace(aoaiDeployment))
                {
                    _logger.LogError("Azure OpenAI configuration missing. Set AzureOpenAI__Endpoint, AzureOpenAI__ApiKey, and AzureOpenAI__DeploymentName.");
                    throw new InvalidOperationException("Azure OpenAI configuration missing");
                }

                // Normalize endpoint
                if (!aoaiEndpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    aoaiEndpoint = "https://" + aoaiEndpoint.TrimStart('/');
                }
                aoaiEndpoint = aoaiEndpoint.TrimEnd('/');

                // Read transcript once
                string transcript;
                using (var reader = new StreamReader(transcriptStream))
                {
                    transcript = await reader.ReadToEndAsync();
                }
                _logger.LogInformation("Transcript read successfully. Length: {Length} characters", transcript.Length);

                // Extract topic and load questions
                var topic = ExtractTopic(transcript) ?? "generic";
                _logger.LogInformation("Extracted topic: {Topic}", topic);

                var cleanedTranscript = RemoveTopicLine(transcript);

                var questions = LoadQuestionsFromLocal(topic);
                if (questions.Count == 0)
                {
                    _logger.LogWarning("No questions found locally for topic '{Topic}' (topic-specific or generic). Aborting question processing.", topic);
                }
                else
                {
                    _logger.LogInformation("Loaded {Count} questions for topic '{Topic}' from local Questions folder.", questions.Count, topic);
                }

                // Process each question using Azure OpenAI deployment
                var results = new List<string>();
                if (questions.Count > 0)
                {
                    using var http = new HttpClient
                    {
                        BaseAddress = new Uri(aoaiEndpoint)
                    };
                    http.DefaultRequestHeaders.Add("api-key", aoaiApiKey);

                    // Build deployment-scoped path per Azure OpenAI REST
                    // POST /openai/deployments/{deployment}/chat/completions?api-version=2024-06-01
                    var requestUri = $"/openai/deployments/{Uri.EscapeDataString(aoaiDeployment)}/chat/completions?api-version=2024-06-01";

                    for (int i = 0; i < questions.Count; i++)
                    {
                        var question = questions[i];
                        _logger.LogInformation("Processing question {Index}/{Total}: {Question}", i + 1, questions.Count, question);

                        var payload = new
                        {
                            messages = new object[]
                            {
                                new { role = "system", content = "You are an AI assistant analyzing call center transcripts. Answer the question based solely on the transcript provided." },
                                new { role = "user", content = $"{cleanedTranscript}\n\n{question}" }
                            },
                            temperature = 0.7,
                            max_tokens = 1000
                        };

                        var resp = await http.PostAsJsonAsync(requestUri, payload, JsonOptions);
                        if (!resp.IsSuccessStatusCode)
                        {
                            var err = await resp.Content.ReadAsStringAsync();
                            _logger.LogError("Azure OpenAI call failed. Status {Status}: {Body}", (int)resp.StatusCode, err);
                            throw new RequestFailedException((int)resp.StatusCode, $"Azure OpenAI returned {(int)resp.StatusCode}");
                        }

                        using var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
                        var answer = json?.RootElement
                            .GetProperty("choices")[0]
                            .GetProperty("message")
                            .GetProperty("content")
                            .GetString() ?? string.Empty;

                        results.Add($"Q: {question}\nA: {answer}");
                        _logger.LogInformation("Completed question {Index}/{Total}", i + 1, questions.Count);
                    }
                }

                await WriteResultsToBlob(resultsConnectionString, name, results);
                _logger.LogInformation("Successfully processed all questions for transcript: {Name}", name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing transcript {Name}: {Message}", name, ex.Message);
                throw;
            }
        }

        private string? ExtractTopic(string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript)) return null;

            var reader = new StringReader(transcript);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue; // skip blank lines
                if (line.StartsWith("Topic:", StringComparison.OrdinalIgnoreCase))
                {
                    var topicValue = line.Substring(line.IndexOf(':') + 1).Trim();
                    var sanitized = new string(topicValue.ToLowerInvariant().Where(ch => !Path.GetInvalidFileNameChars().Contains(ch) && !char.IsWhiteSpace(ch)).ToArray());
                    return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
                }
                break; // first non-empty line not a topic line
            }
            return null;
        }

        private string RemoveTopicLine(string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript)) return transcript;
            var reader = new StringReader(transcript);
            var sb = new StringBuilder();
            string? line;
            bool processedFirstContentLine = false;
            while ((line = reader.ReadLine()) != null)
            {
                if (!processedFirstContentLine)
                {
                    if (string.IsNullOrWhiteSpace(line)) { sb.AppendLine(line); continue; }
                    if (line.StartsWith("Topic:", StringComparison.OrdinalIgnoreCase)) { processedFirstContentLine = true; continue; }
                    processedFirstContentLine = true; sb.AppendLine(line);
                }
                else
                {
                    sb.AppendLine(line);
                }
            }
            return sb.ToString().Trim();
        }

        private List<string> LoadQuestionsFromLocal(string topic)
        {
            var list = new List<string>();
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var questionsDir = Path.Combine(baseDir, "Questions");
                if (!Directory.Exists(questionsDir))
                {
                    _logger.LogWarning("Questions directory '{Dir}' not found in output.", questionsDir);
                    return list;
                }

                var textInfo = CultureInfo.InvariantCulture.TextInfo;
                var topicTitle = textInfo.ToTitleCase(topic);
                var candidates = new List<string>
                {
                    Path.Combine(questionsDir, topic + ".txt"),
                    Path.Combine(questionsDir, topicTitle + ".txt"),
                    Path.Combine(questionsDir, topic.ToUpperInvariant() + ".txt"),
                    Path.Combine(questionsDir, "generic.txt"),
                    Path.Combine(questionsDir, "Generic.txt"),
                    Path.Combine(questionsDir, "GENERIC.TXT")
                };

                foreach (var file in candidates)
                {
                    if (File.Exists(file))
                    {
                        var text = File.ReadAllText(file);
                        list = ParseQuestions(text);
                        if (list.Count > 0)
                        {
                            _logger.LogInformation("Loaded questions from '{File}'.", Path.GetFileName(file));
                            return list;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error loading questions from local folder.");
            }
            return list; // empty
        }

        private List<string> ParseQuestions(string text)
            => text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(q => q.Trim())
                   .Where(q => !string.IsNullOrWhiteSpace(q))
                   .ToList();

        private async Task WriteResultsToBlob(string resultsConnection, string originalFileName, List<string> results)
        {
            var blobServiceClient = new BlobServiceClient(resultsConnection);
            var containerClient = blobServiceClient.GetBlobContainerClient("final-output");
            await containerClient.CreateIfNotExistsAsync();

            var resultFileName = Path.ChangeExtension(originalFileName, null) + "_questionresults.txt";
            var blobClient = containerClient.GetBlobClient(resultFileName);

            var resultText = results.Count == 0
                ? "No questions were processed (no questions source found)."
                : string.Join("\n\n---\n\n", results);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(resultText));
            await blobClient.UploadAsync(stream, overwrite: true);

            _logger.LogInformation("Results written to container 'final-output' as blob: {File}", resultFileName);
        }
    }
}
