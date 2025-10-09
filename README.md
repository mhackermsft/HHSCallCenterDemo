# HHSCallCenterDemo

Demo repo for an HHS Call Center solution that transcribes audio into text and then uses an automated AI solution to ask a series of questions about the transcript and record the results.

## Solution Structure

This is a Visual Studio solution with multiple Azure Functions projects:

### Project 1: AudioTranscriptionFunction
An Azure Function that is triggered when a new audio file is dropped into a specific blob storage container. The function:
- Uses Azure Blob Storage trigger to detect new audio files in the `audio-input` container
- Transcribes the audio file using Azure AI Speech service
- Implements speaker diarization to identify different speakers (Speaker1, Speaker2, etc.)
- Writes the transcription to a `transcript-output` container in blob storage

### Project 2: AIQuestionsProcessing
An Azure Function that is triggered when a new transcript is available in the `transcript-output` container. The function:
- Uses Azure Blob Storage trigger to detect new transcript files in the `transcript-output` container
- Reads the transcript file into memory (once) and reuses it for all questions
- Loads questions from the local `Questions` folder included with the function (supports topic-specific files like `generic.txt`)
- Calls Azure OpenAI (deployment-based) for each question, passing in the full transcript with the question
- Writes the results to a file named `{original_name}_questionresults.txt` in the `final-output` container

## Storage Accounts Layout (Important)

Two Azure Storage accounts are required:

1) Primary Storage Account (pipeline data)
   - Used by the `AudioTranscriptionFunction`
   - Also used by `AIQuestionsProcessing` for reading transcripts and writing results
   - Must contain these containers:
     - `audio-input` (drop audio files here to trigger transcription)
     - `transcript-output` (transcripts written by the transcription function)
     - `final-output` (question results written by the AI processing function)

2) Secondary Storage Account (host-only for AIQuestionsProcessing)
   - Used only by the `AIQuestionsProcessing` function app as its `AzureWebJobsStorage` (host/runtime state)
   - Does not need the data containers above

## Prerequisites

- .NET 8.0 SDK or later
- Azure subscription
- Two Azure Storage Accounts (primary for data, secondary for the AI function host)
- Azure AI Speech service (for audio transcription)
- Azure OpenAI resource with a deployed model (deployment name required)

## Configuration

### Local Development

1. Copy the `local.settings.json.template` file to `local.settings.json` in each project:
   ```bash
   cp AudioTranscriptionFunction/local.settings.json.template AudioTranscriptionFunction/local.settings.json
   cp AIQuestionsProcessing/local.settings.json.template AIQuestionsProcessing/local.settings.json
   ```

2. Update the `local.settings.json` files with your Azure credentials.

Example settings (replace placeholders with your values):

**AudioTranscriptionFunction/local.settings.json:**
```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "<PRIMARY_STORAGE_CONNECTION_STRING>",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "SpeechServiceKey": "<SPEECH_KEY>",
    "SpeechServiceRegion": "<SPEECH_REGION>"
  }
}
```

**AIQuestionsProcessing/local.settings.json:**
```json
{
  "IsEncrypted": false,
  "Values": {
    // Host/runtime storage for this function app (secondary storage account)
    "AzureWebJobsStorage": "<SECONDARY_STORAGE_CONNECTION_STRING>",

    // Data storage for transcripts and results (primary storage account)
    "TranscriptsStorage": "<PRIMARY_STORAGE_CONNECTION_STRING>",
    // Optional: if omitted, results are written to the same account as TranscriptsStorage
    "ResultsStorage": "<PRIMARY_STORAGE_CONNECTION_STRING>",

    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",

    // Azure OpenAI configuration
    "AzureOpenAI__Endpoint": "https://<YOUR_AOAI_RESOURCE>.openai.azure.com/",
    "AzureOpenAI__ApiKey": "<YOUR_AOAI_KEY>",
    "AzureOpenAI__DeploymentName": "<YOUR_DEPLOYMENT_NAME>"
  }
}
```

Notes:
- `TranscriptsStorage` must point to the primary storage account that contains the `transcript-output` and `final-output` containers.
- `ResultsStorage` is optional; when omitted, results are written to `final-output` in the same account as `TranscriptsStorage`.
- The AIQuestionsProcessing function reads questions from its local `Questions` folder in the build output.

### Azure Deployment

When deploying to Azure, configure the following Application Settings.

**For AudioTranscriptionFunction:**
- `AzureWebJobsStorage`: Primary storage connection string
- `SpeechServiceKey`: Your Azure AI Speech service key
- `SpeechServiceRegion`: Your Azure AI Speech service region (e.g., "eastus")

**For AIQuestionsProcessing:**
- `AzureWebJobsStorage`: Secondary storage connection string (host-only)
- `TranscriptsStorage`: Primary storage connection string (data)
- `ResultsStorage`: Primary storage connection string (optional)
- `AzureOpenAI__Endpoint`: Your Azure OpenAI resource endpoint URL
- `AzureOpenAI__ApiKey`: Your Azure OpenAI resource key
- `AzureOpenAI__DeploymentName`: Your Azure OpenAI deployment name (not a model ID)

## Storage Containers

The solution uses the following blob storage containers in the primary storage account:
- `audio-input`: Drop audio files here to trigger transcription (created automatically by function trigger)
- `transcript-output`: Transcription results are written here
- `final-output`: AI question results are written here

## Building the Solution

```bash
dotnet restore
dotnet build
```

## Running Locally

1. Ensure you have configured the `local.settings.json` files for both projects with your Azure credentials
2. Start the Azure Storage Emulator (Azurite) or configure real Azure Storage accounts
3. Place a `generic.txt` (and optionally topic-specific files) inside the `AIQuestionsProcessing/Questions` folder, one question per line
4. Run the `AudioTranscriptionFunction`:
   ```bash
   cd AudioTranscriptionFunction
   func start
   ```
5. In a separate terminal, run the `AIQuestionsProcessing` function:
   ```bash
   cd AIQuestionsProcessing
   func start
   ```
6. Upload a WAV audio file to the `audio-input` container in the primary storage account
7. The transcription function writes the transcript to `transcript-output`
8. The AIQuestionsProcessing function reads the transcript, answers the questions, and writes results to `{filename}_questionresults.txt` in `final-output`

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
- Microsoft.Azure.Functions.Worker (2.1.0)
- Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs (6.8.0)
- Uses HttpClient to call Azure OpenAI REST API

## License

[Add your license information here]
