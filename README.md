# HHSCallCenterDemo

Demo repo for an HHS Call Center solution that:
- Transcribes caller audio into text using Azure Speech Batch Transcription
- Automatically analyzes transcripts with Azure OpenAI (direct Q&A list and decision tree traversal)
- Stores AI outputs back to Azure Storage
- Provides a visual, no-code rules editor for maintaining a decision tree (`rules.json`)

## Solution Structure

Visual Studio solution targeting .NET 8 containing:
- 3 Azure Functions projects (pipeline + AI processing)
- 1 Blazor Server web application (visual rules editor)
- 1 shared class library (models + validation engine)

Projects:

### Project 1: AudioTranscriptionFunction (Azure Functions)
Submits and monitors Azure Speech batch transcription jobs when audio files are uploaded.

Components / Functions:
- `BatchTranscriptionSubmitFunction` (Blob Trigger): Fires on new blobs in `audio-input` (primary storage). Submits a batch transcription job to Azure Speech using a SAS URL to the audio file and enqueues a polling message.
- `BatchTranscriptionPoller` (Queue Trigger): Polls Speech job status via `transcription-jobs` queue until completion. On success, downloads recognized phrases JSON, assembles a plain text transcript (prefixed with a topic line), and writes it to the `transcript-output` container.
- `StorageContainerInitializer` (Hosted Service): Ensures required containers (`audio-input`, `transcript-output`) exist at startup.

Key Behaviors:
- Batch (asynchronous) transcription via REST API (`speechtotext/v3.1`)
- Speaker diarization (labels: `Speaker 1`, `Speaker 2`, ...)
- Transcript begins with `Topic: generic` (or later a custom topic) to guide downstream selection
- Queue-based resilient polling (fixed 60s visibility delay)
- Avoids Azurite for audio (Speech must fetch blob via SAS)

### Project 2: AIQuestionsProcessing (Azure Functions)
Processes finished transcripts and runs Azure OpenAI question/answering.

- `AIQuestionsProcessingTrigger` (Blob Trigger): Fires on new blobs in `transcript-output`.
- Extracts optional `Topic: <name>` line (first non-empty line) to choose a topic-specific questions file; falls back to `generic.txt`.
- Loads questions from local `Questions` folder (copied to build output, case-insensitive variants).
- Calls Azure OpenAI Chat Completions once per question with the full transcript as context.
- Writes aggregated Q/A pairs to `{original_name}_questionresults.txt` in `final-output`.

### Project 3: DecisionTreeFunction (Azure Functions)
Processes transcripts through a configurable decision tree using Azure OpenAI.

- `DecisionTreeTrigger` (Blob Trigger): Fires on new blobs in `transcript-output`.
- Loads and validates decision tree from `rules.json` (copied to function output directory).
- Validates structure (missing links, cycles, unreachable nodes) via `DecisionTreeEngine`.
- Asks each node's question, uses AI response to choose next node until an End node.
- Writes Q&A traversal and final outcome to `{original_name}_decisiontree.txt` in `final-output`.

### Project 4: RulesEditor (Blazor Server Web App)
Visual, no‑code editor for creating and maintaining `rules.json` consumed by DecisionTreeFunction.

Key Features:
- Drag-free auto layout visualization of nodes & branches
- Add / edit / delete nodes (SingleChoice, Number, End)
- Automatic layout recalculation and scroll-to-start
- Validation on save leveraging shared engine
- File load / save to default `rules.json`
- Example tree bootstrap if no file exists
- Dismissible status notifications that auto-hide after 5 seconds (manual close button available)

Run locally:
```bash
cd RulesEditor
dotnet run
# navigate to http://localhost:5050/editor
```
See `RulesEditor/README.md` for deeper usage notes.

### Shared Library: DecisionTreeShared
Models (`DecisionTree`, `DecisionNode`, `Choice`, `Rule`) and `DecisionTreeEngine` for validation and traversal logic (shared by RulesEditor + DecisionTreeFunction).

## Processing Pipeline (End-to-End)
1. Upload audio file to `audio-input` container (primary storage).
2. `BatchTranscriptionSubmitFunction` submits a batch transcription job and enqueues polling metadata.
3. `BatchTranscriptionPoller` polls status; on success builds transcript (`Topic:` header + speaker-labelled lines) -> saves to `transcript-output`.
4. Blob triggers:
   - `AIQuestionsProcessingTrigger` produces `{name}_questionresults.txt`.
   - `DecisionTreeTrigger` produces `{name}_decisiontree.txt` (decision path + final outcome).
5. Both outputs land in `final-output`.

## Storage Accounts Layout (Important)
Two Azure Storage accounts are required:

1) Primary Storage Account (data pipeline)
   - Containers:
     - `audio-input`: Input audio files
     - `transcript-output`: Generated transcripts (.txt)
     - `final-output`: AI results (`*_questionresults.txt`, `*_decisiontree.txt`)
     - Queue `transcription-jobs`: Batch job polling messages

2) Secondary Storage Account (optional host state for AI functions)
   - Used as `AzureWebJobsStorage` for AI question + decision tree functions IF you want to separate runtime state
   - Does NOT need data containers

## Prerequisites
- .NET 8.0 SDK or later
- Azure subscription
- Two Azure Storage Accounts (see layout)
- Azure AI Speech resource (Batch transcription enabled)
- Azure OpenAI resource (with deployed model; deployment name required)

## Configuration

### Environment Variables / App Settings
AudioTranscriptionFunction:
- `AzureWebJobsStorage` (primary storage connection string) – must be real Azure Storage (NOT Azurite for audio)
- `SpeechServiceKey`
- `SpeechServiceRegion` (e.g. eastus)
- `SpeechTranscriptionLocale` (optional, default `en-US`)

AIQuestionsProcessing:
- `AzureWebJobsStorage` (secondary or primary if not separating)
- `TranscriptsStorage` (primary storage connection string – read transcripts / write results)
- `AzureOpenAI__Endpoint` (e.g. https://YOUR_RESOURCE.openai.azure.com/)
- `AzureOpenAI__ApiKey`
- `AzureOpenAI__DeploymentName`

DecisionTreeFunction:
- `AzureWebJobsStorage` (secondary or primary)
- `TranscriptsStorage` (primary storage connection string)
- `AzureOpenAI__Endpoint`
- `AzureOpenAI__ApiKey`
- `AzureOpenAI__DeploymentName`

RulesEditor (Blazor Server):
- Runs without special env vars; writes `rules.json` at the project content root (ensure filesystem permissions in hosting environment).

### Local Development
1. Copy template settings:
   ```bash
   cp AudioTranscriptionFunction/local.settings.json.template AudioTranscriptionFunction/local.settings.json
   cp AIQuestionsProcessing/local.settings.json.template AIQuestionsProcessing/local.settings.json
   cp DecisionTreeFunction/local.settings.json.template DecisionTreeFunction/local.settings.json
   ```
2. Populate each with values described above.
3. IMPORTANT: For end-to-end batch transcription you must use real Azure Storage (Azurite cannot serve blobs to Speech via SAS).
4. Add at least one questions file (e.g., `AIQuestionsProcessing/Questions/generic.txt`) with one question per line.
5. Run each project:
   ```bash
   # Functions (choose separate terminals or tasks)
   cd AudioTranscriptionFunction && func start
   cd AIQuestionsProcessing && func start
   cd DecisionTreeFunction && func start

   # Rules editor
   cd RulesEditor && dotnet run
   ```
6. Upload audio to `audio-input`.
7. Observe queue messages in `transcription-jobs`, transcript in `transcript-output`, and outputs in `final-output`.

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
    "AzureWebJobsStorage": "<SECONDARY_OR_PRIMARY_STORAGE_CONNECTION_STRING>",
    "TranscriptsStorage": "<PRIMARY_STORAGE_CONNECTION_STRING>",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureOpenAI__Endpoint": "https://<YOUR_AOAI_RESOURCE>.openai.azure.com/",
    "AzureOpenAI__ApiKey": "<YOUR_AOAI_KEY>",
    "AzureOpenAI__DeploymentName": "<YOUR_DEPLOYMENT_NAME>"
  }
}
```

### Sample local.settings.json (DecisionTreeFunction)
```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "<SECONDARY_OR_PRIMARY_STORAGE_CONNECTION_STRING>",
    "TranscriptsStorage": "<PRIMARY_STORAGE_CONNECTION_STRING>",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureOpenAI__Endpoint": "https://<YOUR_AOAI_RESOURCE>.openai.azure.com/",
    "AzureOpenAI__ApiKey": "<YOUR_AOAI_KEY>",
    "AzureOpenAI__DeploymentName": "<YOUR_DEPLOYMENT_NAME>"
  }
}
```

### Azure Deployment (App Settings Summary)
AudioTranscriptionFunction:
- `AzureWebJobsStorage`
- `SpeechServiceKey`
- `SpeechServiceRegion`
- (Optional) `SpeechTranscriptionLocale`

AIQuestionsProcessing:
- `AzureWebJobsStorage`
- `TranscriptsStorage`
- `AzureOpenAI__Endpoint`
- `AzureOpenAI__ApiKey`
- `AzureOpenAI__DeploymentName`

DecisionTreeFunction:
- `AzureWebJobsStorage`
- `TranscriptsStorage`
- `AzureOpenAI__Endpoint`
- `AzureOpenAI__ApiKey`
- `AzureOpenAI__DeploymentName`

## Storage Containers / Queue Summary (Primary Account)
- `audio-input`: Input audio (trigger)
- `transcript-output`: Text transcripts produced by poller
- `final-output`: AI question results + decision tree results
- Queue `transcription-jobs`: Batch job polling messages

## Building
```bash
dotnet restore
dotnet build
```

## Audio File Guidance
- Prefer WAV (PCM) 16 kHz, mono or stereo (MP3 supported)
- Clear speech, minimal background noise

## Transcript Format Example
```
Topic: generic
Speaker 1: Hello thank you for calling...
Speaker 2: I'm calling about my benefits...
```
(Topic can be customized; update the first line to influence which questions file and decision tree logic may be applied.)

## Adding / Customizing Questions
Add `*.txt` files in `AIQuestionsProcessing/Questions/`. Build copies them to output. The function searches for `<topic>.txt` (case variants) else uses `generic.txt`.

## Decision Tree Authoring Workflow
1. Run RulesEditor (`/editor`).
2. Create or modify nodes visually; save writes `rules.json` to project root.
3. Deploy or copy updated `rules.json` to DecisionTreeFunction artifact / content root.
4. New transcripts will use the updated tree automatically (function loads once per cold start).

Output File Produced: `{original_transcript_name}_decisiontree.txt`

## Dependencies (Key)
AudioTranscriptionFunction:
- Microsoft.Azure.Functions.Worker 2.x
- Microsoft.Azure.Functions.Worker.Extensions.Storage.* 6.8.0
- Microsoft.CognitiveServices.Speech 1.46.0
- Azure.Storage.Queues 12.x

AIQuestionsProcessing / DecisionTreeFunction:
- Microsoft.Azure.Functions.Worker 2.x
- Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs 6.8.0
- Azure.AI.OpenAI 1.0.0-beta.*

Shared / Web:
- Blazor Server (.NET 8)

## Limitations / Notes
- Demo only; not production hardened (error handling, security, PII/HIPAA compliance, throttling, retries beyond basic, cost controls, etc.)
- Batch transcription requires real Azure resources; Azurite unsupported for Speech ingest
- No automatic cleanup of old queue messages or transcripts
- Azure OpenAI costs accrue per token; keep prompts / questions concise
- Decision tree validation will reject cycles, unreachable nodes, or broken references

## License
You may copy, use, or modify the code for any purpose, including commercial applications, without any restrictions or attribution required.
Provided "as is" without warranty of any kind.

## Disclaimer
Not for production use. You are responsible for security, compliance, and data protection in your own deployment.
