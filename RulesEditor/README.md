# Rules Editor Web Application

A modern, easy-to-use web application for creating and editing `rules.json` files used with the DecisionTreeFunction project.

## Features

- **Real-time JSON Validation**: Uses the DecisionTreeEngine to validate JSON as you type
- **Visual Preview**: See your decision tree structure with expandable node details
- **Intuitive Interface**: Simple and user-friendly design with clear visual feedback
- **File Operations**: Load, save, and download rules.json files
- **Example Templates**: Includes example decision trees to get started quickly
- **Comprehensive Documentation**: Built-in documentation for node types, validation rules, and operators

## Getting Started

### Prerequisites

- .NET 8.0 SDK or later
- A modern web browser

### Running the Application

1. Navigate to the RulesEditor directory:
   ```bash
   cd RulesEditor
   ```

2. Run the application:
   ```bash
   dotnet run
   ```

3. Open your browser and navigate to: `http://localhost:5050`

## Using the Editor

### Creating a New Decision Tree

1. Click the **"New"** button to create a blank decision tree template
2. Edit the JSON in the left panel
3. Click **"Validate"** to check your JSON for errors
4. View the structure and node details in the right panel

### Loading an Existing File

1. Click the **"Load File"** button
2. Select your `rules.json` file
3. The JSON will be loaded and automatically validated

### Editing a Decision Tree

The JSON editor supports:
- Syntax highlighting (monospace font)
- Large text area for comfortable editing
- Real-time editing without auto-save

### Validation

The validation feature checks for:
- Valid JSON syntax
- Required fields (id, version, startNodeId, nodes)
- Valid node references (all nextNodeId fields must point to existing nodes)
- No cycles in the decision tree
- All nodes are reachable from the start node
- Proper node types (End, SingleChoice, Number)

### Saving Your Work

1. Click **"Validate"** to ensure your JSON is correct
2. Click **"Save to File"** to save to the local filesystem
3. Or click **"Download"** to download the file

## Decision Tree Structure

### Node Types

- **End**: Terminal node that concludes the flow
- **SingleChoice**: Presents multiple choice options
- **Number**: Evaluates numeric responses against rules

### Example Node - SingleChoice

```json
{
  "id": "q1",
  "prompt": "What type of issue is this?",
  "type": "SingleChoice",
  "choices": [
    {
      "key": "billing",
      "label": "Billing Issue",
      "nextNodeId": "q_billing"
    },
    {
      "key": "technical",
      "label": "Technical Issue",
      "nextNodeId": "q_tech"
    }
  ]
}
```

### Example Node - Number

```json
{
  "id": "q_billing",
  "prompt": "How much is the disputed amount?",
  "type": "Number",
  "rules": [
    {
      "operator": "LessThan",
      "value": "100",
      "nextNodeId": "end_refund"
    },
    {
      "operator": "GreaterOrEqual",
      "value": "100",
      "nextNodeId": "end_escalate"
    }
  ]
}
```

### Example Node - End

```json
{
  "id": "end_refund",
  "type": "End",
  "prompt": "Refund approved. Amount will be credited in 3-5 business days."
}
```

## Operators for Number Nodes

- `LessThan`: Value is less than the specified threshold
- `LessThanOrEqual`: Value is less than or equal to the threshold
- `GreaterThan`: Value is greater than the specified threshold
- `GreaterOrEqual`: Value is greater than or equal to the threshold
- `Equal`: Value equals the specified threshold

## Architecture

The application consists of:

- **RulesEditor** (Blazor Server Web App): The web interface
- **DecisionTreeShared** (Class Library): Shared models and validation engine
- **DecisionTreeService**: Service layer for loading, validating, and saving decision trees

## Technology Stack

- ASP.NET Core 8.0
- Blazor Server
- Bootstrap 5
- Bootstrap Icons

## Screenshots

### Home Page
![Home Page](https://github.com/user-attachments/assets/7e246afd-91e8-4731-9efe-a12b359c5b8c)

### Editor with Validation
![Editor Page](https://github.com/user-attachments/assets/4c144233-af95-4c3f-a8ae-b07657c345df)

### Node Details View
![Expanded Node View](https://github.com/user-attachments/assets/29c86f0e-bc7c-45bc-8e1b-4520bca26def)

## Notes

- The validation engine is the same one used by DecisionTreeFunction, ensuring consistency
- Files are saved to the application's working directory
- The application runs in development mode by default
- For production deployment, configure appropriate security and hosting settings
