using System.Text;
using Azure;
using Azure.AI.Inference;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Project2TranscriptProcessor;

public class TranscriptProcessorFunction
{
    private readonly ILogger<TranscriptProcessorFunction> _logger;

    public TranscriptProcessorFunction(ILogger<TranscriptProcessorFunction> logger)
    {
        _logger = logger;
    }

    [Function(nameof(TranscriptProcessorFunction))]
    public async Task Run(
        [BlobTrigger("output-transcripts/{name}", Connection = "AzureWebJobsStorage")] 
        Stream transcriptStream,
        string name,
        Uri uri,
        FunctionContext context)
    {
        _logger.LogInformation("Processing transcript blob: {name}", name);
        _logger.LogInformation("Blob Uri: {uri}", uri);

        try
        {
            // Read the transcript into memory
            string transcript;
            using (var reader = new StreamReader(transcriptStream, Encoding.UTF8))
            {
                transcript = await reader.ReadToEndAsync();
            }

            _logger.LogInformation("Transcript loaded. Length: {length} characters", transcript.Length);

            // Read questions from questions.txt
            var questionsFilePath = Path.Combine(AppContext.BaseDirectory, "questions.txt");
            if (!File.Exists(questionsFilePath))
            {
                _logger.LogError("questions.txt file not found at: {path}", questionsFilePath);
                return;
            }

            var questions = await File.ReadAllLinesAsync(questionsFilePath);
            _logger.LogInformation("Loaded {count} questions from questions.txt", questions.Length);

            // Get Azure AI configuration
            var aiEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_ENDPOINT");
            var aiModelName = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_NAME");
            
            if (string.IsNullOrEmpty(aiEndpoint) || string.IsNullOrEmpty(aiModelName))
            {
                _logger.LogError("AZURE_AI_ENDPOINT or AZURE_AI_MODEL_NAME environment variable not set");
                return;
            }

            // Initialize Azure AI client
            var credential = new DefaultAzureCredential();
            var client = new ChatCompletionsClient(new Uri(aiEndpoint), credential);

            var results = new List<string>();

            // Process each question
            for (int i = 0; i < questions.Length; i++)
            {
                var question = questions[i].Trim();
                if (string.IsNullOrEmpty(question))
                {
                    continue;
                }

                _logger.LogInformation("Processing question {index}: {question}", i + 1, question);

                try
                {
                    // Create the prompt with transcript and question
                    var prompt = $"{transcript}\n\n{question}";

                    var requestOptions = new ChatCompletionsOptions
                    {
                        Messages =
                        {
                            new ChatRequestSystemMessage("You are a helpful assistant that analyzes call center transcripts and answers questions about them."),
                            new ChatRequestUserMessage(prompt)
                        },
                        Model = aiModelName,
                        MaxTokens = 1000,
                        Temperature = 0.7f
                    };

                    var response = await client.CompleteAsync(requestOptions);
                    var answer = response.Value.Content;

                    results.Add(answer);
                    _logger.LogInformation("Question {index} processed successfully", i + 1);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing question {index}: {question}", i + 1, question);
                    results.Add($"Error: {ex.Message}");
                }
            }

            // Write results to questionresults.txt in the same blob container
            var storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            if (string.IsNullOrEmpty(storageConnectionString))
            {
                _logger.LogError("AzureWebJobsStorage connection string not found");
                return;
            }

            var blobServiceClient = new BlobServiceClient(storageConnectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient("output-transcripts");
            
            // Create a results file name based on the transcript name
            var resultsFileName = Path.GetFileNameWithoutExtension(name) + "-questionresults.txt";
            var resultsBlobClient = containerClient.GetBlobClient(resultsFileName);

            // Write results with each result on a new line
            var resultsContent = string.Join(Environment.NewLine, results);
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(resultsContent)))
            {
                await resultsBlobClient.UploadAsync(stream, overwrite: true);
            }

            _logger.LogInformation("Results written to {fileName}", resultsFileName);
            _logger.LogInformation("Processing complete for transcript: {name}", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing transcript: {name}", name);
            throw;
        }
    }
}
