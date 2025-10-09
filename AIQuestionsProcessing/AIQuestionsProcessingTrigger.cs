using Azure;
using Azure.AI.Inference;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text;

namespace AIQuestionsProcessing
{
    public class AIQuestionsProcessingTrigger
    {
        private readonly ILogger<AIQuestionsProcessingTrigger> _logger;

        public AIQuestionsProcessingTrigger(ILogger<AIQuestionsProcessingTrigger> logger)
        {
            _logger = logger;
        }

        [Function(nameof(AIQuestionsProcessingTrigger))]
        public async Task Run(
            [BlobTrigger("transcripts/{name}", Connection = "AzureWebJobsStorage")] Stream transcriptStream,
            string name)
        {
            try
            {
                _logger.LogInformation($"Processing transcript: {name}");

                // Get configuration
                var storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
                var aiEndpoint = Environment.GetEnvironmentVariable("AIFoundryEndpoint");
                var aiApiKey = Environment.GetEnvironmentVariable("AIFoundryApiKey");
                var aiModelName = Environment.GetEnvironmentVariable("AIFoundryModelName") ?? "gpt-4o";

                if (string.IsNullOrEmpty(aiEndpoint) || string.IsNullOrEmpty(aiApiKey))
                {
                    _logger.LogError("AIFoundryEndpoint or AIFoundryApiKey not configured");
                    throw new InvalidOperationException("Azure AI Foundry configuration missing");
                }

                // Read the transcript
                string transcript;
                using (var reader = new StreamReader(transcriptStream))
                {
                    transcript = await reader.ReadToEndAsync();
                }

                _logger.LogInformation($"Transcript read successfully. Length: {transcript.Length} characters");

                // Read questions from questions.txt
                var questions = await ReadQuestionsFromBlob(storageConnectionString!);
                _logger.LogInformation($"Found {questions.Count} questions to process");

                // Process each question with the LLM
                var results = new List<string>();
                var client = new ChatCompletionsClient(new Uri(aiEndpoint), new AzureKeyCredential(aiApiKey));

                for (int i = 0; i < questions.Count; i++)
                {
                    var question = questions[i];
                    _logger.LogInformation($"Processing question {i + 1}/{questions.Count}: {question}");

                    var prompt = $"{transcript}\n\n{question}";
                    
                    var requestOptions = new ChatCompletionsOptions
                    {
                        Messages =
                        {
                            new ChatRequestSystemMessage("You are an AI assistant analyzing call center transcripts. Answer the question based solely on the transcript provided."),
                            new ChatRequestUserMessage(prompt)
                        },
                        Model = aiModelName,
                        Temperature = 0.7f,
                        MaxTokens = 1000
                    };

                    var response = await client.CompleteAsync(requestOptions);
                    var answer = response.Value.Content;
                    
                    results.Add($"Q: {question}\nA: {answer}");
                    _logger.LogInformation($"Completed question {i + 1}/{questions.Count}");
                }

                // Write results to blob storage
                await WriteResultsToBlob(storageConnectionString!, name, results);

                _logger.LogInformation($"Successfully processed all questions for transcript: {name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing transcript {name}: {ex.Message}");
                throw;
            }
        }

        private async Task<List<string>> ReadQuestionsFromBlob(string connectionString)
        {
            var blobServiceClient = new BlobServiceClient(connectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient("transcripts");
            var blobClient = containerClient.GetBlobClient("questions.txt");

            if (!await blobClient.ExistsAsync())
            {
                _logger.LogWarning("questions.txt not found in transcripts container");
                return new List<string>();
            }

            var content = await blobClient.DownloadContentAsync();
            var questionsText = content.Value.Content.ToString();
            
            return questionsText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(q => q.Trim())
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .ToList();
        }

        private async Task WriteResultsToBlob(string connectionString, string originalFileName, List<string> results)
        {
            var blobServiceClient = new BlobServiceClient(connectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient("transcripts");
            
            await containerClient.CreateIfNotExistsAsync();

            var resultFileName = Path.ChangeExtension(originalFileName, null) + "_questionresults.txt";
            var blobClient = containerClient.GetBlobClient(resultFileName);

            var resultText = string.Join("\n\n---\n\n", results);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(resultText));
            await blobClient.UploadAsync(stream, overwrite: true);

            _logger.LogInformation($"Results written to blob: {resultFileName}");
        }
    }
}
