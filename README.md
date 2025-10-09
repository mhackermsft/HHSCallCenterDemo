# HHSCallCenterDemo
Demo repo for an HHS Call Center solution that transcribes audio into text and then uses an automated AI solution to ask a series of questions about the transcript and record the results.

## Solution Structure

### Project 2: Transcript Processor Function
Located in `Project2TranscriptProcessor/`

An Azure Function that is triggered when a new transcript is available in the `output-transcripts` blob storage container. The function:
- Reads the transcript from blob storage
- Loads questions from `questions.txt` (one question per line)
- For each question, calls an Azure AI Foundry LLM model with the transcript and question
- Writes all results to a file named `{transcript-name}-questionresults.txt` in the same blob container

## Getting Started

### Prerequisites
- .NET 8.0 SDK or later
- Azure subscription with:
  - Azure Storage Account
  - Azure AI Foundry deployment with LLM model

### Configuration

Navigate to `Project2TranscriptProcessor/` and:
1. Copy `local.settings.json.template` to `local.settings.json`
2. Update the following values:
   - `AzureWebJobsStorage`: Your Azure Storage connection string
   - `AZURE_AI_ENDPOINT`: Your Azure AI Foundry endpoint
   - `AZURE_AI_MODEL_NAME`: Your deployed model name (e.g., `gpt-4`, `gpt-35-turbo`)

### Build and Run

```bash
# Build the solution
dotnet build

# Run Project 2 locally
cd Project2TranscriptProcessor
func start
```

## Deployment

Deploy Project 2 to Azure Functions:
```bash
cd Project2TranscriptProcessor
func azure functionapp publish <your-function-app-name>
```

## How It Works

1. Project 1 (to be implemented) transcribes audio and stores transcripts in the `output-transcripts` blob container
2. Project 2 is automatically triggered when a new transcript appears
3. The function reads the transcript and processes each question from `questions.txt`
4. Results are saved to `{transcript-name}-questionresults.txt` in the same container

