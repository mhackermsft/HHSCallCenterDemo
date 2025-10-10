using Azure;
using Azure.AI.OpenAI;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIQuestionsProcessing
{
    /// <summary>
    /// Azure Function that is triggered whenever a new transcript text file is written to the
    /// <c>transcript-output</c> blob container. It loads a list of pre-defined questions (topic-specific or
    /// generic), sends each question along with the transcript to Azure OpenAI, and saves the answers
    /// to a results blob.
    /// </summary>
    /// <remarks>
    /// High level flow:
    /// 1. Trigger fires for a new transcript blob.
    /// 2. Reads transcript text and attempts to extract a topic from the first non-empty line (e.g. "Topic: benefits").
    /// 3. Loads a questions file matching the topic (or a generic one if topic-specific file not found).
    /// 4. For each question, calls Azure OpenAI (chat completion) providing the transcript as context.
    /// 5. Aggregates question / answer pairs and writes them to a <c>final-output</c> container.
    /// </remarks>
    public class AIQuestionsProcessingTrigger
    {
        private readonly ILogger<AIQuestionsProcessingTrigger> _logger;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        /// <summary>
        /// Creates a new instance of <see cref="AIQuestionsProcessingTrigger"/>
        /// </summary>
        /// <param name="logger">Logger used to record diagnostic information and errors.</param>
        public AIQuestionsProcessingTrigger(ILogger<AIQuestionsProcessingTrigger> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Main Azure Function entry point. Processes a transcript when a blob is uploaded.
        /// </summary>
        /// <param name="transcriptStream">The raw stream containing the transcript text file contents.</param>
        /// <param name="name">The original blob name (file name of the transcript).</param>
        /// <remarks>
        /// This method orchestrates reading the transcript, loading questions, calling the AI model, and
        /// writing results. If configuration is missing (e.g. Azure OpenAI settings), it throws an exception.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown if required environment variables are missing.</exception>
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

                var aoaiEndpoint = Environment.GetEnvironmentVariable("AzureOpenAI__Endpoint");
                var aoaiApiKey = Environment.GetEnvironmentVariable("AzureOpenAI__ApiKey");
                var aoaiDeployment = Environment.GetEnvironmentVariable("AzureOpenAI__DeploymentName");
                if (string.IsNullOrWhiteSpace(aoaiEndpoint) || string.IsNullOrWhiteSpace(aoaiApiKey) || string.IsNullOrWhiteSpace(aoaiDeployment))
                {
                    _logger.LogError("Azure OpenAI configuration missing. Set AzureOpenAI__Endpoint, AzureOpenAI__ApiKey, and AzureOpenAI__DeploymentName.");
                    throw new InvalidOperationException("Azure OpenAI configuration missing");
                }

                if (!aoaiEndpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    aoaiEndpoint = "https://" + aoaiEndpoint.TrimStart('/');
                }
                aoaiEndpoint = aoaiEndpoint.TrimEnd('/');

                string transcript;
                using (var reader = new StreamReader(transcriptStream))
                {
                    transcript = await reader.ReadToEndAsync();
                }
                _logger.LogInformation("Transcript read successfully. Length: {Length} characters", transcript.Length);

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

                var results = new List<string>();
                if (questions.Count > 0)
                {
                    var client = new OpenAIClient(new Uri(aoaiEndpoint), new AzureKeyCredential(aoaiApiKey));

                    foreach (var (question, index) in questions.Select((q, i) => (q, i)))
                    {
                        _logger.LogInformation("Processing question {Index}/{Total}: {Question}", index + 1, questions.Count, question);

                        var chatOptions = new ChatCompletionsOptions
                        {
                            Temperature = 0.2f,
                            MaxTokens = 1000,
                            DeploymentName = aoaiDeployment
                        };
                        chatOptions.Messages.Add(new ChatRequestSystemMessage("You are an AI assistant analyzing call center transcripts. Answer the question based solely on the transcript provided."));
                        chatOptions.Messages.Add(new ChatRequestUserMessage($"{cleanedTranscript}\n\n{question}"));

                        Response<ChatCompletions> response;
                        try
                        {
                            response = await client.GetChatCompletionsAsync(chatOptions);
                        }
                        catch (RequestFailedException rfe)
                        {
                            _logger.LogError(rfe, "Azure OpenAI call failed for question {Index}/{Total}", index + 1, questions.Count);
                            throw;
                        }

                        var answer = response.Value.Choices.FirstOrDefault()?.Message?.Content ?? string.Empty;
                        results.Add($"Q: {question}\nA: {answer}");
                        _logger.LogInformation("Completed question {Index}/{Total}", index + 1, questions.Count);
                    }
                }

                await WriteResultsToBlob(transcriptsConnectionString, name, results);
                _logger.LogInformation("Successfully processed all questions for transcript: {Name}", name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing transcript {Name}: {Message}", name, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Looks at the very first non-empty line of the transcript to see if it starts with
        /// <c>Topic:</c>. If so, extracts and normalizes that topic name for use in file lookup.
        /// </summary>
        /// <param name="transcript">Full transcript text.</param>
        /// <returns>The sanitized topic string (lowercase, no spaces / invalid file chars) or <c>null</c> if not found.</returns>
        private string? ExtractTopic(string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript)) return null;

            var reader = new StringReader(transcript);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                if (line.StartsWith("Topic:", StringComparison.OrdinalIgnoreCase))
                {
                    var topicValue = line.Substring(line.IndexOf(':') + 1).Trim();
                    var sanitized = new string(topicValue.ToLowerInvariant().Where(ch => !Path.GetInvalidFileNameChars().Contains(ch) && !char.IsWhiteSpace(ch)).ToArray());
                    return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
                }
                break;
            }
            return null;
        }

        /// <summary>
        /// Removes the initial <c>Topic:</c> line (if present) so the AI model does not treat it as part of the
        /// conversation content.
        /// </summary>
        /// <param name="transcript">Full transcript text (possibly starting with a topic line).</param>
        /// <returns>The transcript without the topic declaration line.</returns>
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

        /// <summary>
        /// Attempts to load a list of questions from the <c>Questions</c> folder in the build output.
        /// It tries several filename variations for the specific topic before falling back to generic files.
        /// </summary>
        /// <param name="topic">Topic name extracted from the transcript (or "generic").</param>
        /// <returns>A list of individual question strings. Empty if no file found.</returns>
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
            return list;
        }

        /// <summary>
        /// Splits raw text (from a questions file) into individual non-empty lines.
        /// </summary>
        /// <param name="text">Full contents of a questions text file.</param>
        /// <returns>List of trimmed, non-empty question lines.</returns>
        private List<string> ParseQuestions(string text)
            => text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(q => q.Trim())
                   .Where(q => !string.IsNullOrWhiteSpace(q))
                   .ToList();

        /// <summary>
        /// Writes the aggregated question / answer results to a blob in the <c>final-output</c> container.
        /// Creates the container if it does not already exist.
        /// </summary>
        /// <param name="resultsConnection">Storage connection string to use for writing results.</param>
        /// <param name="originalFileName">Original transcript blob name (used to derive results filename).</param>
        /// <param name="results">List of formatted question / answer pairs.</param>
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
