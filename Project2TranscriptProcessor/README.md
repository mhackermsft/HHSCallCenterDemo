# Project 2: Transcript Processor Function

This Azure Function is triggered when a new transcript is available in the `output-transcripts` blob storage container (from Project 1).

## Functionality

The function performs the following tasks:
1. Reads the transcript from the blob storage trigger
2. Loads questions from `questions.txt` (one question per line)
3. For each question, calls an Azure AI Foundry LLM model with the transcript and question
4. Writes all results to `questionresults.txt` in the same blob container (one result per line)

## Configuration

### Required Environment Variables

Set these in `local.settings.json` for local development or in Azure Function App Settings for production:

- `AzureWebJobsStorage`: Azure Storage connection string
- `AZURE_AI_ENDPOINT`: Azure AI Foundry endpoint URL (e.g., `https://your-endpoint.openai.azure.com`)
- `AZURE_AI_MODEL_NAME`: Model deployment name (e.g., `gpt-4`, `gpt-35-turbo`)

### Setup

1. Copy `local.settings.json.template` to `local.settings.json`
2. Update the values with your Azure resources
3. Customize `questions.txt` with your desired questions (one per line)

### Authentication

The function uses `DefaultAzureCredential` for authentication with Azure AI services. Ensure the function's identity has appropriate permissions.

## Build and Run

```bash
dotnet restore
dotnet build
func start
```

## Deployment

Deploy to Azure Functions:
```bash
func azure functionapp publish <your-function-app-name>
```
