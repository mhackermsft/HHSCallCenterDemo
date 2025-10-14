using Azure;
using Azure.AI.OpenAI;
using Azure.Storage.Blobs;
using DecisionTreeFunction.Engine;
using DecisionTreeFunction.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text;

namespace DecisionTreeFunction
{
    /// <summary>
    /// Azure Function triggered by new transcript blobs to process them through a decision tree
    /// </summary>
    public class DecisionTreeTrigger
    {
        private readonly ILogger<DecisionTreeTrigger> _logger;
        private static readonly DecisionTreeEngine _engine = new();
        private static bool _engineLoaded = false;
        private static readonly object _loadLock = new();

        public DecisionTreeTrigger(ILogger<DecisionTreeTrigger> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Main Azure Function entry point. Processes a transcript when a blob is uploaded to transcript-output.
        /// </summary>
        [Function(nameof(DecisionTreeTrigger))]
        public async Task Run(
            [BlobTrigger("transcript-output/{name}", Connection = "TranscriptsStorage")] Stream transcriptStream,
            string name)
        {
            try
            {
                _logger.LogInformation("Processing transcript with decision tree: {Name}", name);

                // Load and validate configuration
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

                // Read transcript
                string transcript;
                using (var reader = new StreamReader(transcriptStream))
                {
                    transcript = await reader.ReadToEndAsync();
                }
                _logger.LogInformation("Transcript read successfully. Length: {Length} characters", transcript.Length);

                // Load decision tree (singleton pattern to avoid loading on every execution)
                LoadDecisionTree();

                // Initialize Azure OpenAI client
                var openAiClient = new OpenAIClient(new Uri(aoaiEndpoint), new AzureKeyCredential(aoaiApiKey));

                // Process through decision tree
                var results = await ProcessDecisionTree(openAiClient, aoaiDeployment, transcript);

                // Save results to blob storage
                await SaveResults(transcriptsConnectionString, name, results);

                _logger.LogInformation("Successfully processed transcript through decision tree: {Name}", name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing transcript {Name}: {Message}", name, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Loads the decision tree from rules.json (thread-safe singleton)
        /// </summary>
        private void LoadDecisionTree()
        {
            if (_engineLoaded)
            {
                return;
            }

            lock (_loadLock)
            {
                if (_engineLoaded)
                {
                    return;
                }

                var baseDir = AppContext.BaseDirectory;
                var rulesPath = Path.Combine(baseDir, "rules.json");

                _logger.LogInformation("Loading decision tree from: {Path}", rulesPath);
                _engine.LoadFromFile(rulesPath);
                _engineLoaded = true;
                _logger.LogInformation("Decision tree loaded and validated successfully");
            }
        }

        /// <summary>
        /// Processes the transcript through the decision tree, calling Azure OpenAI for each question
        /// </summary>
        private async Task<List<string>> ProcessDecisionTree(OpenAIClient client, string deployment, string transcript)
        {
            var results = new List<string>();
            var currentNode = _engine.GetStartNode();
            int questionNumber = 1;

            while (currentNode != null)
            {
                _logger.LogInformation("Processing node: {NodeId}, Type: {Type}", currentNode.Id, currentNode.Type);

                // If we've reached an end node, record it and stop
                if (currentNode.Type == "End")
                {
                    var endResult = $"End Node: {currentNode.Id}\nOutcome: {currentNode.Prompt}";
                    results.Add(endResult);
                    _logger.LogInformation("Reached end node: {NodeId}", currentNode.Id);
                    break;
                }

                // Construct the prompt for Azure OpenAI
                var prompt = BuildPrompt(currentNode, transcript, questionNumber);
                _logger.LogInformation("Asking question {Num}: {Question}", questionNumber, currentNode.Prompt);

                // Call Azure OpenAI
                var aiResponse = await CallAzureOpenAI(client, deployment, prompt);
                _logger.LogInformation("AI Response for question {Num}: {Response}", questionNumber, aiResponse);

                // Record the Q&A
                var qaResult = $"Question {questionNumber} (Node: {currentNode.Id}): {currentNode.Prompt}\nAI Response: {aiResponse}";
                results.Add(qaResult);

                // Determine next node based on AI response
                var nextNodeId = _engine.GetNextNodeId(currentNode, aiResponse);
                
                if (nextNodeId == null)
                {
                    _logger.LogWarning("No next node determined for response: {Response}", aiResponse);
                    break;
                }

                currentNode = _engine.GetNode(nextNodeId);
                if (currentNode == null)
                {
                    _logger.LogError("Next node not found: {NodeId}", nextNodeId);
                    break;
                }

                questionNumber++;
            }

            return results;
        }

        /// <summary>
        /// Builds the prompt to send to Azure OpenAI based on the current node
        /// </summary>
        private string BuildPrompt(DecisionNode node, string transcript, int questionNumber)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Based on the following call center transcript, please answer the question concisely.");
            sb.AppendLine();
            sb.AppendLine("Transcript:");
            sb.AppendLine(transcript);
            sb.AppendLine();
            sb.AppendLine($"Question {questionNumber}: {node.Prompt}");
            
            // Add guidance based on node type
            if (node.Type == "SingleChoice" && node.Choices != null)
            {
                sb.AppendLine();
                sb.AppendLine("Please respond with one of the following options that best answers the question based on the transcript:");
                foreach (var choice in node.Choices)
                {
                    sb.AppendLine($"- {choice.Label}");
                }
                sb.AppendLine();
                sb.AppendLine("Respond with just the option name.");
            }
            else if (node.Type == "Number")
            {
                sb.AppendLine();
                sb.AppendLine("Please respond with a numeric value only.");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Calls Azure OpenAI with the given prompt
        /// </summary>
        private async Task<string> CallAzureOpenAI(OpenAIClient client, string deployment, string prompt)
        {
            var chatOptions = new ChatCompletionsOptions
            {
                DeploymentName = deployment,
                Temperature = 0.1f,
                MaxTokens = 500
            };

            chatOptions.Messages.Add(new ChatRequestSystemMessage("You are an AI assistant analyzing call center transcripts. Answer questions based solely on the transcript provided. Be concise and direct in your responses."));
            chatOptions.Messages.Add(new ChatRequestUserMessage(prompt));

            var response = await client.GetChatCompletionsAsync(chatOptions);
            
            if (response.Value.Choices.Count == 0)
            {
                throw new InvalidOperationException("Azure OpenAI returned no response");
            }

            return response.Value.Choices[0].Message.Content.Trim();
        }

        /// <summary>
        /// Saves the decision tree results to the final-output blob container
        /// </summary>
        private async Task SaveResults(string connectionString, string originalFileName, List<string> results)
        {
            var blobServiceClient = new BlobServiceClient(connectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient("final-output");
            await containerClient.CreateIfNotExistsAsync();

            var resultFileName = Path.ChangeExtension(originalFileName, null) + "_decisiontree.txt";
            var blobClient = containerClient.GetBlobClient(resultFileName);

            var resultText = results.Count == 0
                ? "No results (decision tree processing failed or incomplete)."
                : string.Join("\n\n---\n\n", results);

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(resultText));
            await blobClient.UploadAsync(stream, overwrite: true);

            _logger.LogInformation("Decision tree results written to container 'final-output' as blob: {File}", resultFileName);
        }
    }
}
