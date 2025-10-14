# HHSCallCenterDemo

Demo repo for an HHS Call Center solution that transcribes caller audio into text using Azure Speech Batch Transcription and then uses Azure OpenAI to answer a series of predefined questions about the transcript, writing results back to storage.

## Solution Structure

Visual Studio solution with three Azure Functions projects and one web application.

### Project 1: AudioTranscriptionFunction
Submits and monitors Azure Speech batch transcription jobs when audio files are uploaded.

Components / Functions:
- `BatchTranscriptionSubmitFunction` (Blob Trigger): Fires on new blobs in `audio-input` (primary storage). Submits a batch transcription job to Azure Speech using a SAS URL to the audio file and enqueues a polling message.
- `BatchTranscriptionPoller` (Queue Trigger): Processes messages on the `transcription-jobs` queue, polling Speech job status until completion. On success, downloads the recognized phrases JSON, assembles a plain text transcript (prefixed with a topic line), and writes it to the `transcript-output` container.
- `StorageContainerInitializer` (Hosted Service): Ensures required containers (`audio-input`, `transcript-output`) exist at startup.

Key Behaviors:
- Batch (asynchronous) transcription via REST API (`speechtotext/v3.1`).
- Speaker diarization enabled; speakers labeled sequentially as `Speaker 1:`, `Speaker 2:`, etc.
- Transcript begins with a topic header line: `Topic: generic` (or a custom topic you may later inject) to guide downstream question selection.
- Uses a queue (`transcription-jobs`) for resilient polling with retry/backoff (fixed 60s visibility delay) until job succeeds or fails.
- Avoids Azurite for audio input because Azure Speech must fetch the blob via SAS (public Internet reachability to real storage required).

### Project 2: AIQuestionsProcessing
Processes finished transcripts and runs Azure OpenAI question/answering.

- `AIQuestionsProcessingTrigger` (Blob Trigger): Fires on new blobs in `transcript-output` (primary storage � referenced via `TranscriptsStorage`).
- Extracts an optional topic line (`Topic: <name>`) from the first non-empty line to choose a topic-specific questions file; falls back to `generic.txt`.
- Loads question files from local `Questions` folder copied to the build output (supports multiple casing patterns).
- Calls Azure OpenAI Chat Completions (Azure.AI.OpenAI SDK) once per question, providing the full transcript as context.
- Writes aggregated Q/A pairs to `{original_name}_questionresults.txt` in the `final-output` container (using `TranscriptsStorage`).

### Project 3: DecisionTreeFunction
Processes transcripts through a configurable decision tree using Azure OpenAI.

- `DecisionTreeTrigger` (Blob Trigger): Fires on new blobs in `transcript-output` (primary storage).
- Loads and validates decision tree from `rules.json` configuration file.
- Processes transcript through the tree by asking questions and using AI to determine the path.
- Validates tree structure for missing links, cycles, and unreachable nodes.
- Writes complete Q&A history and final outcome to `{original_name}_decisiontree.txt` in the `final-output` container.

### Project 4: RulesEditor (Web Application)
A modern Blazor Server web application for creating and editing `rules.json` files used by DecisionTreeFunction.

**Features:**
- Real-time JSON validation using DecisionTreeEngine
- Visual preview of decision tree structure with expandable node details
- Intuitive interface with file upload/download
- Example templates and built-in documentation
- Validates all node references, detects cycles, ensures all nodes are reachable

**Running the RulesEditor:**
```bash
cd RulesEditor
dotnet run
```
Then navigate to http://localhost:5050

See [RulesEditor/README.md](RulesEditor/README.md) for detailed documentation.

### Shared Library: DecisionTreeShared
Class library containing decision tree models and validation engine shared between DecisionTreeFunction and RulesEditor.

## Processing Pipeline (End-to-End)
1. Upload audio file to `audio-input` container (primary storage).
2. `BatchTranscriptionSubmitFunction` submits a batch job and enqueues polling metadata.
3. `BatchTranscriptionPoller` polls job status; once succeeded it downloads transcription JSON, builds text transcript with speaker labels + topic line, and saves it to `transcript-output`.
4. Blob trigger in `AIQuestionsProcessing` fires; transcript is read once and cached in memory.
5. Topic extracted; appropriate question list loaded from local `Questions` folder.
6. Each question sent to Azure OpenAI (deployment-based) with transcript context; answers are collected.
7. Results file written to `final-output`.

## Storage Accounts Layout (Important)
Two Azure Storage accounts are required:

1) Primary Storage Account (data pipeline)
   - Containers:
     - `audio-input`: Input audio files
     - `transcript-output`: Generated transcripts (.txt)
     - `final-output`: AI question results
     - `transcription-jobs` queue (created automatically for polling)

2) Secondary Storage Account (host state for AIQuestionsProcessing only)
   - Used as `AzureWebJobsStorage` for the AI function app host runtime
   - Does NOT need data containers

## Prerequisites
- .NET 8.0 SDK or later
- Azure subscription
- Two Azure Storage Accounts
- Azure AI Speech resource (Batch transcription enabled)
- Azure OpenAI resource (with deployed model; deployment name required)

## Configuration

### Environment Variables / App Settings
AudioTranscriptionFunction:
- `AzureWebJobsStorage` (primary storage connection string) � must be real Azure Storage (NOT Azurite for audio) so Speech can access the blob via SAS
- `SpeechServiceKey`
- `SpeechServiceRegion` (e.g. eastus)
- `SpeechTranscriptionLocale` (optional, default `en-US`)

AIQuestionsProcessing:
- `AzureWebJobsStorage` (secondary storage connection string � host only)
- `TranscriptsStorage` (primary storage connection string � read transcripts / write results)
- `AzureOpenAI__Endpoint` (e.g. https://YOUR_RESOURCE.openai.azure.com/)
- `AzureOpenAI__ApiKey`
- `AzureOpenAI__DeploymentName` (deployment, not raw model name)

### Local Development
1. Copy template settings:
   ```bash
   cp AudioTranscriptionFunction/local.settings.json.template AudioTranscriptionFunction/local.settings.json
   cp AIQuestionsProcessing/local.settings.json.template AIQuestionsProcessing/local.settings.json
   ```
2. Populate each with the values described above.
3. IMPORTANT: For end-to-end batch transcription you must use real Azure Storage (Azurite cannot serve blobs to the Speech service via SAS). You may still test later stages separately with mock or pre-created transcripts.
4. Add at least one questions file (e.g., `AIQuestionsProcessing/Questions/generic.txt`) with one question per line.
5. Run each project:
   ```bash
   cd AudioTranscriptionFunction
   func start
   # separate terminal
   cd AIQuestionsProcessing
   func start
   ```
6. Upload a suitable audio file to the `audio-input` container (primary storage).
7. Observe queue messages in `transcription-jobs`, eventual transcript in `transcript-output`, and final results file in `final-output`.

### Sample local.settings.json (AudioTranscriptionFunction)
```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "<PRIMARY_STORAGE_CONNECTION_STRING>",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "SpeechServiceKey": "<SPEECH_KEY>",
    "SpeechServiceRegion": "<SPEECH_REGION>",
    "SpeechTranscriptionLocale": "en-US"
  }
}
```

### Sample local.settings.json (AIQuestionsProcessing)
```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "<SECONDARY_STORAGE_CONNECTION_STRING>",
    "TranscriptsStorage": "<PRIMARY_STORAGE_CONNECTION_STRING>",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureOpenAI__Endpoint": "https://<YOUR_AOAI_RESOURCE>.openai.azure.com/",
    "AzureOpenAI__ApiKey": "<YOUR_AOAI_KEY>",
    "AzureOpenAI__DeploymentName": "<YOUR_DEPLOYMENT_NAME>"
  }
}
```

### Azure Deployment (App Settings)
AudioTranscriptionFunction:
- `AzureWebJobsStorage`
- `SpeechServiceKey`
- `SpeechServiceRegion`
- (Optional) `SpeechTranscriptionLocale`

AIQuestionsProcessing:
- `AzureWebJobsStorage` (secondary)
- `TranscriptsStorage` (primary)
- `AzureOpenAI__Endpoint`
- `AzureOpenAI__ApiKey`
- `AzureOpenAI__DeploymentName`

## Storage Containers / Queue Summary (Primary Account)
- `audio-input`: Input audio (trigger)
- `transcript-output`: Text transcripts produced by poller
- `final-output`: AI question results
- Queue `transcription-jobs`: Batch job polling messages

## Building
```bash
dotnet restore
dotnet build
```

## Audio File Guidance
- Prefer WAV (PCM) 16 kHz, mono or stereo.  MP3 supported.
- Clear speech, minimal background noise

## Transcript Format
Example snippet:
```
Topic: generic
Speaker 1: Hello thank you for calling...
Speaker 2: I'm calling about my benefits...
```
(Topic can be customized; change the first line to influence which questions file is used.)

## Adding/Customizing Questions
Place `*.txt` files in `AIQuestionsProcessing/Questions/`. During build they are copied to output. The function searches for `<topic>.txt` (case variants) then falls back to `generic.txt`.

## Dependencies (Key)
AudioTranscriptionFunction:
- Microsoft.Azure.Functions.Worker 2.1.0
- Microsoft.Azure.Functions.Worker.Extensions.Storage.* 6.8.0
- Microsoft.CognitiveServices.Speech 1.46.0
- Azure.Storage.Queues 12.x

AIQuestionsProcessing:
- Microsoft.Azure.Functions.Worker 2.1.0
- Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs 6.8.0
- Azure.AI.OpenAI 1.0.0-beta.17

## Limitations / Notes
- Demo only; not production hardened (error handling, security, PII/HIPAA compliance, cost controls, retries beyond basic, etc.).
- Batch transcription requires real Azure resources; local emulator unsupported for speech ingest.
- No automatic cleanup of old queue messages or transcripts.
- Azure OpenAI costs accrue per token; questions should be concise.

## License
You may copy, use, or modify the code for any purpose, including commercial applications, without any restrictions or attribution required.
Provided "as is" without warranty of any kind.

## Disclaimer
Not for production use. You are responsible for security, compliance, and data protection in your own deployment.
