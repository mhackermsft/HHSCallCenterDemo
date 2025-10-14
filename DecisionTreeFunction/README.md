# DecisionTreeFunction

An Azure Function that processes call center transcripts through a configurable decision tree using Azure OpenAI.

## Overview

This function is triggered when a new transcript blob is uploaded to the `transcript-output` container. It:

1. Loads a decision tree from `rules.json`
2. Validates the tree structure (missing links, cycles, unreachable nodes)
3. Processes the transcript through the tree by asking questions
4. Uses Azure OpenAI to analyze the transcript and answer each question
5. Saves the complete Q&A history and final outcome to the `final-output` container

## Project Structure

```
DecisionTreeFunction/
├── DecisionTreeTrigger.cs          # Main Azure Function (blob trigger)
├── Models/
│   └── DecisionTree.cs             # Data models for decision tree JSON
├── Engine/
│   └── DecisionTreeEngine.cs       # Decision tree validation and traversal logic
├── rules.json                      # Decision tree configuration (example included)
├── Program.cs                      # Azure Functions host setup
├── host.json                       # Function app configuration
└── local.settings.json.template    # Configuration template
```

## Configuration

Create a `local.settings.json` file based on `local.settings.json.template`:

```json
{
    "IsEncrypted": false,
    "Values": {
        "AzureWebJobsStorage": "UseDevelopmentStorage=true",
        "TranscriptsStorage": "<PRIMARY_STORAGE_CONNECTION_STRING>",
        "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
        "AzureOpenAI__Endpoint": "https://<YOUR_AOAI_RESOURCE>.openai.azure.com/",
        "AzureOpenAI__ApiKey": "<YOUR_AOAI_KEY>",
        "AzureOpenAI__DeploymentName": "<YOUR_DEPLOYMENT_NAME>"
    }
}
```

### Required Settings

- **TranscriptsStorage**: Connection string to Azure Storage account containing transcript blobs
- **AzureOpenAI__Endpoint**: Azure OpenAI service endpoint
- **AzureOpenAI__ApiKey**: Azure OpenAI API key
- **AzureOpenAI__DeploymentName**: Name of your deployed model (e.g., gpt-4, gpt-35-turbo)

## Decision Tree Format

The decision tree is defined in `rules.json`. See the included example for the full schema.

### Key Concepts

- **Nodes**: Questions or end states in the decision tree
- **Start Node**: Where the tree begins (defined by `startNodeId`)
- **End Nodes**: Terminal nodes with `type: "End"` that conclude the flow
- **Single Choice Nodes**: Present multiple options, match AI response to choice
- **Number Nodes**: Evaluate numeric responses against rules
- **Validation**: The engine validates the tree for missing links, cycles, and unreachable nodes

### Example Node Types

#### Single Choice Node
```json
{
  "id": "q1",
  "prompt": "What type of issue is this?",
  "type": "SingleChoice",
  "choices": [
    { "key": "billing", "label": "Billing", "nextNodeId": "q_billing" },
    { "key": "tech", "label": "Technical", "nextNodeId": "q_tech" }
  ]
}
```

#### Number Node
```json
{
  "id": "q_age",
  "prompt": "How old is the device (in years)?",
  "type": "Number",
  "rules": [
    { "operator": "LessThan", "value": "1", "nextNodeId": "end_warranty" },
    { "operator": "GreaterOrEqual", "value": "1", "nextNodeId": "end_oow" }
  ]
}
```

#### End Node
```json
{
  "id": "end_refund",
  "type": "End",
  "prompt": "Route to billing specialist for refund."
}
```

## How It Works

1. **Trigger**: A new transcript file is uploaded to `transcript-output` container
2. **Load Tree**: The engine loads and validates `rules.json`
3. **Start**: Begin at the `startNodeId` node
4. **Loop**:
   - Present the current node's prompt to Azure OpenAI along with the transcript
   - Get AI's response
   - Record the question and response
   - Determine next node based on the response
   - Continue until reaching an End node
5. **Save**: Write all Q&A pairs and final outcome to `final-output` container

## Output Format

Results are saved to `{original_filename}_decisiontree.txt` in the `final-output` container:

```
Question 1 (Node: q1): What type of issue is this?
AI Response: Technical

---

Question 2 (Node: q_tech): Have you tried power-cycling the device?
AI Response: Yes

---

End Node: end_windows
Outcome: Windows Tier-2 queue.
```

## Building and Running

```bash
# Build the project
dotnet build

# Run locally (requires Azure Functions Core Tools)
cd DecisionTreeFunction
func start
```

## Notes

- The decision tree is loaded once and cached for performance
- The engine validates the tree structure on load to prevent runtime errors
- AI responses are matched case-insensitively against choice keys and labels
- For number nodes, the engine attempts to extract numeric values from AI responses
- Default next node IDs provide fallback routing when no match is found
