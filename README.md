# HHSCallCenterDemo

Demo repo for an HHS Call Center solution that transcribes audio into text and then uses an automated AI solution to ask a series of questions about the transcript and record the results.

## Solution Structure

This is a Visual Studio solution with multiple Azure Functions projects:

### Project 1: AudioTranscriptionFunction
An Azure Function that is triggered when a new audio file is dropped into a specific blob storage container. The function:
- Uses Azure Blob Storage trigger to detect new audio files in the `audio-input` container
- Transcribes the audio file using Azure AI Speech service
- Implements speaker diarization to identify different speakers (Speaker1, Speaker2, etc.)
- Writes the transcription to a `transcripts` container in blob storage

### Project 2: AIQuestionsProcessing
An Azure Function that is triggered when a new transcript is available in the `transcripts` container. The function:
- Uses Azure Blob Storage trigger to detect new transcript files in the `transcripts` container
- Reads the transcript file into memory
- Reads questions from a `questions.txt` file (stored in the `transcripts` container)
- Calls Azure AI Foundry LLM model for each question, passing in the full transcript with the question
- Writes the results to a file named `{original_name}_questionresults.txt` in the `transcripts` container

## Prerequisites

- .NET 8.0 SDK or later
- Azure subscription
- Azure Storage Account
- Azure AI Speech service
- Azure AI Foundry with a deployed LLM model

## Configuration

### Local Development

1. Copy the `local.settings.json.template` file to `local.settings.json` in each project:
   ```bash
   cp AudioTranscriptionFunction/local.settings.json.template AudioTranscriptionFunction/local.settings.json
   cp AIQuestionsProcessing/local.settings.json.template AIQuestionsProcessing/local.settings.json
   ```

2. Update the `local.settings.json` files with your Azure credentials:

**AudioTranscriptionFunction/local.settings.json:**
```json
{
    "IsEncrypted": false,
    "Values": {
        "AzureWebJobsStorage": "your-storage-connection-string",
        "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
        "SpeechServiceKey": "your-speech-service-key",
        "SpeechServiceRegion": "your-speech-service-region"
    }
}
```

**AIQuestionsProcessing/local.settings.json:**
```json
{
    "IsEncrypted": false,
    "Values": {
        "AzureWebJobsStorage": "your-storage-connection-string",
        "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
        "AIFoundryEndpoint": "your-ai-foundry-endpoint-here",
        "AIFoundryApiKey": "your-ai-foundry-api-key-here",
        "AIFoundryModelName": "gpt-4o"
    }
}
```

**Note:** The `local.settings.json` files are excluded from source control to protect sensitive credentials.

### Azure Deployment

When deploying to Azure, configure the following Application Settings:

**For AudioTranscriptionFunction:**
- `AzureWebJobsStorage`: Your Azure Storage connection string
- `SpeechServiceKey`: Your Azure AI Speech service key
- `SpeechServiceRegion`: Your Azure AI Speech service region (e.g., "eastus")

**For AIQuestionsProcessing:**
- `AzureWebJobsStorage`: Your Azure Storage connection string
- `AIFoundryEndpoint`: Your Azure AI Foundry endpoint URL
- `AIFoundryApiKey`: Your Azure AI Foundry API key
- `AIFoundryModelName`: The model name to use (e.g., "gpt-4o")

## Storage Containers

The solution uses the following blob storage containers:
- `audio-input`: Drop audio files here to trigger transcription (created automatically by function trigger)
- `transcripts`: Transcription results are written here and question results are also stored here (created automatically if it doesn't exist)
  - Place a `questions.txt` file in this container with one question per line for the AI to answer about each transcript

## Building the Solution

```bash
dotnet restore
dotnet build
```

## Running Locally

1. Ensure you have configured the `local.settings.json` files for both projects with your Azure credentials
2. Start the Azure Storage Emulator (Azurite) or configure a real Azure Storage account
3. Upload a `questions.txt` file to the `transcripts` container with one question per line (e.g., "What was the main topic of the conversation?")
4. Run the AudioTranscriptionFunction:
   ```bash
   cd AudioTranscriptionFunction
   func start
   ```
5. In a separate terminal, run the AIQuestionsProcessing function:
   ```bash
   cd AIQuestionsProcessing
   func start
   ```
6. Upload a WAV audio file to the `audio-input` container in your storage account
7. The AudioTranscriptionFunction will automatically process the file and write the transcript to the `transcripts` container
8. The AIQuestionsProcessing function will automatically process the transcript, answer the questions, and write results to `{filename}_questionresults.txt` in the `transcripts` container

## Audio File Format

The Speech service works best with:
- WAV format audio files
- 16 kHz sample rate
- Mono or stereo audio
- Clear audio with minimal background noise

## Speaker Diarization

The transcription includes basic speaker identification. The output format is:
```
Speaker1: [transcribed text]
Speaker1: [more text]
Speaker2: [different speaker text]
```

Note: For production use with advanced speaker diarization, you may need to use Azure Speech service's conversation transcription features which require additional configuration.

## Project Dependencies

### AudioTranscriptionFunction
- Microsoft.Azure.Functions.Worker (2.1.0)
- Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs (6.8.0)
- Microsoft.CognitiveServices.Speech (1.46.0)
- Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore (2.0.2)

### AIQuestionsProcessing
- Azure.AI.Inference (1.0.0-beta.2)
- Microsoft.Azure.Functions.Worker (2.1.0)
- Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs (6.8.0)
- Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore (2.0.2)

## License

[Add your license information here]
