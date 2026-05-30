using System;
using System.Collections.Generic;
using BepInEx.Logging;
using GiantessLLMMod.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GiantessLLMMod.Core
{
    /// <summary>
    /// OpenAI tool-calling bridge for game-state reads and controlled commands.
    /// This is not a standalone MCP server; it is intentionally scoped to chat tools.
    /// </summary>
    public static class ToolBridge
    {
        private static ManualLogSource _log;
        private static ActionExecutor _executor;
        private static GameStateCollector _collector;
        private static ConfigManager _config;

        public static void Init(ManualLogSource log, ActionExecutor executor, GameStateCollector collector, ConfigManager config)
        {
            _log = log;
            _executor = executor;
            _collector = collector;
            _config = config;
        }

        public static List<ToolDefinition> GetToolDefinitions()
        {
            var tools = new List<ToolDefinition>
            {
                new ToolDefinition {
                    Function = new FunctionDefinition {
                        Name = "get_ai_properties",
                        Description = "Read specific internal properties of a Giantess AI instance. Prefer this for observation only.",
                        Parameters = new {
                            type = "object",
                            properties = new {
                                target = new { type = "string", description = "AI name or 'first'." },
                                properties = new {
                                    type = "array",
                                    items = new { type = "string" },
                                    description = "Property paths like 'm_Hunger', 'm_Stomach.m_AcidFillAmount', or 'stomach.m_BurpBuildUp'."
                                }
                            },
                            required = new[] { "target", "properties" }
                        }
                    }
                },
                new ToolDefinition {
                    Function = new FunctionDefinition {
                        Name = "interrupt_ai",
                        Description = "Clear current native action and movement queues for a Giantess AI if she is stuck or ignoring commands.",
                        Parameters = new {
                            type = "object",
                            properties = new {
                                target = new { type = "string", description = "AI name or 'first'." }
                            },
                            required = new[] { "target" }
                        }
                    }
                }
            };

            if (_config?.EnableDebugPropertyTools.Value == true)
            {
                tools.Add(new ToolDefinition {
                    Function = new FunctionDefinition {
                        Name = "debug_set_ai_float_property",
                        Description = "Debug-only: set an allowlisted float property on a Giantess AI. Only use when explicitly asked.",
                        Parameters = new {
                            type = "object",
                            properties = new {
                                target = new { type = "string", description = "AI name or 'first'." },
                                property = new { type = "string", description = "Allowlisted property path, such as 'm_Hunger' or 'm_Horniness'." },
                                value = new { type = "number" }
                            },
                            required = new[] { "target", "property", "value" }
                        }
                    }
                });
            }

            return tools;
        }

        public static string ExecuteTool(string name, string argumentsJson)
        {
            try
            {
                var args = string.IsNullOrWhiteSpace(argumentsJson)
                    ? new JObject()
                    : JObject.Parse(argumentsJson);

                string targetName = args["target"]?.ToString() ?? "first";
                var ai = _executor.FindGiantess(targetName);
                if (ai == null)
                    return Error("AI not found", targetName);

                switch (name)
                {
                    case "get_ai_properties":
                        return GetProperties(ai, args["properties"]?.ToObject<List<string>>());

                    case "interrupt_ai":
                        return InterruptAI(ai);

                    case "debug_set_ai_float_property":
                        return SetDebugFloatProperty(ai, args);

                    default:
                        return Error("Unknown tool", name);
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"Tool {name} failed: {ex.Message}");
                return Error(ex.Message, name);
            }
        }

        private static string GetProperties(Component ai, List<string> paths)
        {
            if (paths == null || paths.Count == 0)
                return Error("No properties requested", "get_ai_properties");

            var results = new Dictionary<string, object>();
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                results[path] = _collector.GetRawProperty(ai, path.Trim());
            }

            return Ok(new { properties = results });
        }

        private static string InterruptAI(Component ai)
        {
            bool success = _executor.ForceInterrupt(ai);
            return success
                ? Ok(new { interrupted = true })
                : Error("Interrupt failed", "interrupt_ai");
        }

        private static string SetDebugFloatProperty(Component ai, JObject args)
        {
            if (_config?.EnableDebugPropertyTools.Value != true)
                return Error("Debug property tools are disabled", "debug_set_ai_float_property");

            string path = args["property"]?.ToString();
            if (string.IsNullOrWhiteSpace(path))
                return Error("Missing property", "debug_set_ai_float_property");

            float value = args["value"]?.Value<float>() ?? 0f;
            bool success = _executor.TrySetDebugFloatProperty(ai, path.Trim(), value, out string message);

            return success
                ? Ok(new { property = path, value, message })
                : Error(message, path);
        }

        private static string Ok(object payload)
        {
            return JsonConvert.SerializeObject(new { success = true, result = payload });
        }

        private static string Error(string message, string context)
        {
            return JsonConvert.SerializeObject(new { success = false, error = message, context });
        }
    }
}
