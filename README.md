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

### Project 2: PlaceholderFunction
A placeholder Azure Functions project for future functionality.

## Prerequisites

- .NET 8.0 SDK or later
- Azure subscription
- Azure Storage Account
- Azure AI Speech service

## Configuration

### Local Development

1. Copy the `local.settings.json.template` file to `local.settings.json` in the `AudioTranscriptionFunction` project:
   ```bash
   cp AudioTranscriptionFunction/local.settings.json.template AudioTranscriptionFunction/local.settings.json
   ```

2. Update the `local.settings.json` file with your Azure credentials:

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

**Note:** The `local.settings.json` file is excluded from source control to protect sensitive credentials.

### Azure Deployment

When deploying to Azure, configure the following Application Settings:
- `AzureWebJobsStorage`: Your Azure Storage connection string
- `SpeechServiceKey`: Your Azure AI Speech service key
- `SpeechServiceRegion`: Your Azure AI Speech service region (e.g., "eastus")

## Storage Containers

The solution uses two blob storage containers:
- `audio-input`: Drop audio files here to trigger transcription (created automatically by function trigger)
- `transcripts`: Transcription results are written here (created automatically if it doesn't exist)

## Building the Solution

```bash
dotnet restore
dotnet build
```

## Running Locally

1. Ensure you have configured the `local.settings.json` file with your Azure credentials
2. Start the Azure Storage Emulator (Azurite) or configure a real Azure Storage account
3. Run the function:
   ```bash
   cd AudioTranscriptionFunction
   func start
   ```
4. Upload a WAV audio file to the `audio-input` container in your storage account
5. The function will automatically process the file and write the transcript to the `transcripts` container

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

### PlaceholderFunction
- Microsoft.Azure.Functions.Worker (2.1.0)
- Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore (2.0.2)

## License

[Add your license information here]
