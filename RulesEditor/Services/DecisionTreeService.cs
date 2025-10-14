using DecisionTreeShared.Models;
using DecisionTreeShared.Engine;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace RulesEditor.Services
{
    /// <summary>
    /// Service for managing decision tree JSON files with validation
    /// </summary>
    public class DecisionTreeService
    {
        private readonly IWebHostEnvironment _env;
        private DecisionTree? _currentTree;

        public DecisionTreeService(IWebHostEnvironment env)
        {
            _env = env;
        }

        /// <summary>
        /// Returns the default rules.json path at the project content root
        /// </summary>
        public string GetDefaultRulesPath() => Path.Combine(_env.ContentRootPath, "rules.json");

        /// <summary>
        /// Load decision tree from JSON string
        /// </summary>
        public (bool Success, string? ErrorMessage, DecisionTree? Tree) LoadFromJson(string json)
        {
            try
            {
                var tree = JsonSerializer.Deserialize<DecisionTree>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    WriteIndented = true
                });

                if (tree == null)
                {
                    return (false, "Failed to deserialize decision tree JSON", null);
                }

                // Validate using DecisionTreeEngine
                var engine = new DecisionTreeEngine();
                
                // Save to temp file for validation
                var tempFile = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(tempFile, json);
                    engine.LoadFromFile(tempFile);
                }
                finally
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }

                _currentTree = tree;
                return (true, null, tree);
            }
            catch (JsonException ex)
            {
                return (false, $"JSON Parse Error: {ex.Message}", null);
            }
            catch (InvalidOperationException ex)
            {
                return (false, $"Validation Error: {ex.Message}", null);
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Load decision tree from file
        /// </summary>
        public (bool Success, string? ErrorMessage, DecisionTree? Tree) LoadFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return (false, $"File not found: {filePath}", null);
                }

                var json = File.ReadAllText(filePath);
                return LoadFromJson(json);
            }
            catch (Exception ex)
            {
                return (false, $"Error reading file: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Attempts to load the default rules.json if it exists
        /// </summary>
        public (bool Success, string? ErrorMessage, DecisionTree? Tree) LoadDefaultFile()
        {
            var path = GetDefaultRulesPath();
            return LoadFromFile(path);
        }

        /// <summary>
        /// Save decision tree to JSON string
        /// </summary>
        public string SerializeToJson(DecisionTree tree)
        {
            return JsonSerializer.Serialize(tree, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            });
        }

        /// <summary>
        /// Save decision tree to file
        /// </summary>
        public (bool Success, string? ErrorMessage) SaveToFile(DecisionTree tree, string filePath)
        {
            try
            {
                var json = SerializeToJson(tree);
                File.WriteAllText(filePath, json);
                _currentTree = tree;
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, $"Error saving file: {ex.Message}");
            }
        }

        /// <summary>
        /// Save to the default rules.json in project root
        /// </summary>
        public (bool Success, string? ErrorMessage) SaveDefaultFile(DecisionTree tree)
        {
            var path = GetDefaultRulesPath();
            return SaveToFile(tree, path);
        }

        /// <summary>
        /// Validate JSON string without loading
        /// </summary>
        public (bool Success, string? ErrorMessage) ValidateJson(string json)
        {
            var result = LoadFromJson(json);
            return (result.Success, result.ErrorMessage);
        }

        /// <summary>
        /// Get current tree
        /// </summary>
        public DecisionTree? GetCurrentTree() => _currentTree;

        /// <summary>
        /// Create a new empty decision tree
        /// </summary>
        public DecisionTree CreateNewTree()
        {
            _currentTree = new DecisionTree
            {
                Id = "new-tree",
                Version = "1.0.0",
                StartNodeId = "start",
                Nodes = new Dictionary<string, DecisionNode>
                {
                    ["start"] = new DecisionNode
                    {
                        Id = "start",
                        Type = "End",
                        Prompt = "This is the start node. Change the type to begin building your decision tree."
                    }
                }
            };
            return _currentTree;
        }
    }
}
