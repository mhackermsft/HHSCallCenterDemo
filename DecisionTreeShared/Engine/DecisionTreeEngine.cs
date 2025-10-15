using DecisionTreeShared.Models;
using System.Text.Json;

namespace DecisionTreeShared.Engine
{
    /// <summary>
    /// Engine for loading, validating, and traversing decision trees
    /// </summary>
    public class DecisionTreeEngine
    {
        private DecisionTree _tree = null!;

        // Helper to compare node types case-insensitively
        private static bool IsType(DecisionNode node, string type) =>
            string.Equals(node.Type, type, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Loads and validates a decision tree from a JSON file
        /// </summary>
        public void LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Decision tree file not found: {filePath}");
            }

            var json = File.ReadAllText(filePath);
            var tree = JsonSerializer.Deserialize<DecisionTree>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (tree == null)
            {
                throw new InvalidOperationException("Failed to deserialize decision tree");
            }

            ValidateTree(tree);
            _tree = tree;
        }

        /// <summary>
        /// Validates the decision tree for missing links, cycles, and unreachable nodes
        /// </summary>
        private void ValidateTree(DecisionTree tree)
        {
            if (string.IsNullOrEmpty(tree.StartNodeId))
            {
                throw new InvalidOperationException("Decision tree must have a startNodeId");
            }

            if (!tree.Nodes.ContainsKey(tree.StartNodeId))
            {
                throw new InvalidOperationException($"Start node '{tree.StartNodeId}' not found in nodes");
            }

            // Validate all node references and check for missing links
            var allNodeIds = tree.Nodes.Keys.ToHashSet();
            foreach (var kvp in tree.Nodes)
            {
                var node = kvp.Value;

                // Validate choices
                if (node.Choices != null)
                {
                    foreach (var choice in node.Choices)
                    {
                        if (!string.IsNullOrEmpty(choice.NextNodeId) && !allNodeIds.Contains(choice.NextNodeId))
                        {
                            throw new InvalidOperationException(
                                $"Node '{node.Id}' choice '{choice.Key}' references non-existent node '{choice.NextNodeId}'");
                        }
                    }
                }

                // Validate rules
                if (node.Rules != null)
                {
                    foreach (var rule in node.Rules)
                    {
                        if (!string.IsNullOrEmpty(rule.NextNodeId) && !allNodeIds.Contains(rule.NextNodeId))
                        {
                            throw new InvalidOperationException(
                                $"Node '{node.Id}' rule references non-existent node '{rule.NextNodeId}'");
                        }
                    }
                }

                // Validate defaultNextNodeId
                if (!string.IsNullOrEmpty(node.DefaultNextNodeId) && !allNodeIds.Contains(node.DefaultNextNodeId))
                {
                    throw new InvalidOperationException(
                        $"Node '{node.Id}' defaultNextNodeId references non-existent node '{node.DefaultNextNodeId}'");
                }
            }

            // Check for cycles using DFS
            DetectCycles(tree);

            // Check for unreachable nodes
            DetectUnreachableNodes(tree);
        }

        /// <summary>
        /// Detects cycles in the decision tree using DFS
        /// </summary>
        private void DetectCycles(DecisionTree tree)
        {
            var visited = new HashSet<string>();
            var recursionStack = new HashSet<string>();

            bool HasCycle(string nodeId)
            {
                if (recursionStack.Contains(nodeId))
                {
                    return true;
                }

                if (visited.Contains(nodeId))
                {
                    return false;
                }

                if (!tree.Nodes.TryGetValue(nodeId, out var node))
                {
                    return false;
                }

                // End nodes don't have cycles
                if (IsType(node, "End"))
                {
                    visited.Add(nodeId);
                    return false;
                }

                visited.Add(nodeId);
                recursionStack.Add(nodeId);

                var nextNodes = GetNextNodeIds(node);
                foreach (var nextNodeId in nextNodes)
                {
                    if (HasCycle(nextNodeId))
                    {
                        return true;
                    }
                }

                recursionStack.Remove(nodeId);
                return false;
            }

            if (HasCycle(tree.StartNodeId))
            {
                throw new InvalidOperationException("Decision tree contains cycles");
            }
        }

        /// <summary>
        /// Detects unreachable nodes in the decision tree
        /// </summary>
        private void DetectUnreachableNodes(DecisionTree tree)
        {
            var reachable = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(tree.StartNodeId);
            reachable.Add(tree.StartNodeId);

            while (queue.Count > 0)
            {
                var nodeId = queue.Dequeue();
                if (!tree.Nodes.TryGetValue(nodeId, out var node))
                {
                    continue;
                }

                var nextNodes = GetNextNodeIds(node);
                foreach (var nextNodeId in nextNodes)
                {
                    if (!reachable.Contains(nextNodeId))
                    {
                        reachable.Add(nextNodeId);
                        queue.Enqueue(nextNodeId);
                    }
                }
            }

            var unreachable = tree.Nodes.Keys.Except(reachable).ToList();
            if (unreachable.Any())
            {
                throw new InvalidOperationException(
                    $"Decision tree contains unreachable nodes: {string.Join(", ", unreachable)}");
            }
        }

        /// <summary>
        /// Gets all possible next node IDs from a given node
        /// </summary>
        private List<string> GetNextNodeIds(DecisionNode node)
        {
            var nextNodes = new List<string>();

            if (node.Choices != null)
            {
                nextNodes.AddRange(node.Choices.Select(c => c.NextNodeId).Where(id => !string.IsNullOrEmpty(id)));
            }

            if (node.Rules != null)
            {
                nextNodes.AddRange(node.Rules.Select(r => r.NextNodeId).Where(id => !string.IsNullOrEmpty(id)));
            }

            if (!string.IsNullOrEmpty(node.DefaultNextNodeId))
            {
                nextNodes.Add(node.DefaultNextNodeId);
            }

            return nextNodes;
        }

        /// <summary>
        /// Gets the start node of the tree
        /// </summary>
        public DecisionNode GetStartNode()
        {
            return _tree.Nodes[_tree.StartNodeId];
        }

        /// <summary>
        /// Gets a node by its ID
        /// </summary>
        public DecisionNode? GetNode(string nodeId)
        {
            return _tree.Nodes.TryGetValue(nodeId, out var node) ? node : null;
        }

        /// <summary>
        /// Determines the next node based on the current node and the AI's response
        /// Supports node types (case-insensitive): End, SingleChoice, Number, Text
        /// Text nodes simply advance using defaultNextNodeId.
        /// </summary>
        public string? GetNextNodeId(DecisionNode currentNode, string aiResponse)
        {
            if (IsType(currentNode, "End"))
            {
                return null;
            }

            // For Text nodes (free-form input, no branching rules other than default)
            if (IsType(currentNode, "Text"))
            {
                return currentNode.DefaultNextNodeId; // allow null if not set
            }

            // For SingleChoice nodes
            if (IsType(currentNode, "SingleChoice") && currentNode.Choices != null)
            {
                // Try to find a matching choice by key or label (case-insensitive)
                var normalizedResponse = aiResponse.Trim().ToLowerInvariant();
                
                foreach (var choice in currentNode.Choices)
                {
                    if (choice.Key.ToLowerInvariant() == normalizedResponse ||
                        choice.Label.ToLowerInvariant() == normalizedResponse ||
                        normalizedResponse.Contains(choice.Key.ToLowerInvariant()) ||
                        normalizedResponse.Contains(choice.Label.ToLowerInvariant()))
                    {
                        return choice.NextNodeId;
                    }
                }

                // If no match, use default
                return currentNode.DefaultNextNodeId;
            }

            // For Number nodes
            if (IsType(currentNode, "Number") && currentNode.Rules != null)
            {
                // Try to extract a number from the response
                if (TryExtractNumber(aiResponse, out var number))
                {
                    // Apply rules in order (first match wins)
                    foreach (var rule in currentNode.Rules)
                    {
                        if (double.TryParse(rule.Value, out var ruleValue))
                        {
                            bool match = rule.Operator switch
                            {
                                "LessThan" => number < ruleValue,
                                "LessThanOrEqual" => number <= ruleValue,
                                "GreaterThan" => number > ruleValue,
                                "GreaterOrEqual" => number >= ruleValue,
                                "Equal" => Math.Abs(number - ruleValue) < 0.0001,
                                _ => false
                            };

                            if (match)
                            {
                                return rule.NextNodeId;
                            }
                        }
                    }
                }

                // If no match, use default
                return currentNode.DefaultNextNodeId;
            }

            // Fallback for any other node type
            return currentNode.DefaultNextNodeId;
        }

        /// <summary>
        /// Attempts to extract a number from a text response
        /// </summary>
        private bool TryExtractNumber(string text, out double number)
        {
            number = 0;
            
            // Try direct parse first
            if (double.TryParse(text.Trim(), out number))
            {
                return true;
            }

            // Try to find a number in the text
            var words = text.Split(new[] { ' ', ',', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                if (double.TryParse(word, out number))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
