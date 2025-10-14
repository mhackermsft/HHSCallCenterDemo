using System.Text.Json.Serialization;

namespace DecisionTreeShared.Models
{
    /// <summary>
    /// Root of the decision tree loaded from JSON
    /// </summary>
    public class DecisionTree
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("startNodeId")]
        public string StartNodeId { get; set; } = string.Empty;

        [JsonPropertyName("nodes")]
        public Dictionary<string, DecisionNode> Nodes { get; set; } = new();
    }

    /// <summary>
    /// Represents a node in the decision tree
    /// </summary>
    public class DecisionNode
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; set; }

        [JsonPropertyName("rules")]
        public List<Rule>? Rules { get; set; }

        [JsonPropertyName("defaultNextNodeId")]
        public string? DefaultNextNodeId { get; set; }
    }

    /// <summary>
    /// Represents a choice option for SingleChoice nodes
    /// </summary>
    public class Choice
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("nextNodeId")]
        public string NextNodeId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a rule for Number type nodes
    /// </summary>
    public class Rule
    {
        [JsonPropertyName("operator")]
        public string Operator { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("nextNodeId")]
        public string NextNodeId { get; set; } = string.Empty;
    }
}
