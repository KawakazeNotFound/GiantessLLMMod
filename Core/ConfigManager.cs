using BepInEx;
using BepInEx.Configuration;
using GiantessLLMMod.Models;
using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace GiantessLLMMod.Core
{
    /// <summary>
    /// Manages all mod configuration via BepInEx ConfigEntry system.
    /// Config file: BepInEx/config/com.giantess.llmmod.cfg
    /// </summary>
    public class ConfigManager
    {
        // ─── LLM API ───
        public ConfigEntry<string> ApiBaseUrl;
        public ConfigEntry<string> ApiKey;
        public ConfigEntry<string> ModelName;
        public ConfigEntry<float> Temperature;
        public ConfigEntry<int> MaxTokens;

        // ─── Behavior ───
        public ConfigEntry<bool> AutoTriggerEnabled;
        public ConfigEntry<float> AutoTriggerInterval;
        public ConfigEntry<bool> TimedTriggerEnabled;
        public ConfigEntry<float> TimedTriggerInterval;
        public ConfigEntry<bool> EventTriggerEnabled;
        public ConfigEntry<float> EventTriggerCooldown;
        public ConfigEntry<int> MaxConversationHistory;
        public ConfigEntry<bool> EnableNativeDialogue;
        public ConfigEntry<int> DialogueTimeout;
        public ConfigEntry<int> MaxDialogueLength;
        public ConfigEntry<bool> DryRunMode;
        public ConfigEntry<bool> PreventActionConflicts;
        public ConfigEntry<bool> ForceInterruptBusyActions;
        public ConfigEntry<string> ActionConflictPolicy;
        public ConfigEntry<bool> AllowActionsDuringScriptedState;
        public ConfigEntry<int> IdleStateId;

        // ─── Reflection & Collection ───
        public ConfigEntry<int> MaxSceneObjects;
        public ConfigEntry<string> TableKeywords;
        public ConfigEntry<string> BedKeywords;
        public ConfigEntry<float> TrendThreshold;
        public ConfigEntry<float> EmotionBlendTime;
        public ConfigEntry<int> ApiTimeoutMs;

        // ─── Keys ───
        public ConfigEntry<KeyCode> ToggleUIKey;
        public ConfigEntry<KeyCode> ManualTriggerKey;
        public ConfigEntry<KeyCode> ProbeKey;

        // ─── Debug ───
        public ConfigEntry<bool> DebugLogging;
        public ConfigEntry<bool> EnableDebugPropertyTools;

        // ─── System Prompt ───
        public ConfigEntry<string> CustomSystemPrompt;
        public ConfigEntry<string> SystemPromptFile;

        private ConfigFile _configFile;

        public ConfigManager(ConfigFile config)
        {
            _configFile = config;
            // LLM API
            ApiBaseUrl = config.Bind("LLM API", "ApiBaseUrl",
                "http://127.0.0.1:1234/v1/chat/completions",
                "Full URL for the OpenAI-compatible API endpoint (e.g. http://127.0.0.1:11434/v1/chat/completions or http://127.0.0.1:11434/api/generate)");

            ApiKey = config.Bind("LLM API", "ApiKey",
                "",
                "API key (leave empty for local LLM servers that don't require auth)");

            ModelName = config.Bind("LLM API", "Model",
                "local-model",
                "Model name to use for chat completions");

            Temperature = config.Bind("LLM API", "Temperature",
                0.7f,
                new ConfigDescription("Sampling temperature", new AcceptableValueRange<float>(0f, 2f)));

            MaxTokens = config.Bind("LLM API", "MaxTokens",
                700,
                "Maximum tokens in LLM response");

            // Behavior
            AutoTriggerEnabled = config.Bind("Behavior", "AutoTriggerEnabled",
                true,
                "Legacy setting. Use TimedTriggerEnabled and EventTriggerEnabled instead.");

            AutoTriggerInterval = config.Bind("Behavior", "AutoTriggerInterval",
                15f,
                new ConfigDescription("Legacy setting. Use TimedTriggerInterval instead.", new AcceptableValueRange<float>(5f, 120f)));

            TimedTriggerEnabled = config.Bind("Behavior", "TimedTriggerEnabled",
                AutoTriggerEnabled.Value,
                "Enable fixed-interval LLM calls.");

            TimedTriggerInterval = config.Bind("Behavior", "TimedTriggerInterval",
                AutoTriggerInterval.Value,
                new ConfigDescription("Seconds between fixed-interval LLM calls.", new AcceptableValueRange<float>(5f, 120f)));

            EventTriggerEnabled = config.Bind("Behavior", "EventTriggerEnabled",
                true,
                "Enable LLM calls after important game events. Uses a short 5 second cooldown.");

            EventTriggerCooldown = config.Bind("Behavior", "EventTriggerCooldown",
                20f,
                new ConfigDescription("Seconds between event-triggered LLM calls.", new AcceptableValueRange<float>(5f, 300f)));

            MaxConversationHistory = config.Bind("Behavior", "MaxConversationHistory",
                20,
                "Maximum number of messages to keep in conversation history");

            EnableNativeDialogue = config.Bind("Behavior", "EnableNativeDialogue",
                true,
                "Use game's native Say/Ask for dialogue (falls back to IMGUI if unavailable)");

            DialogueTimeout = config.Bind("Behavior", "DialogueTimeout",
                8,
                "Seconds before dialogue auto-dismisses (native mode)");

            MaxDialogueLength = config.Bind("Behavior", "MaxDialogueLength",
                200,
                "Maximum characters for LLM dialogue before truncation.");

            DryRunMode = config.Bind("Behavior", "DryRunMode",
                false,
                "If true, don't actually call LLM — use hardcoded test responses");

            PreventActionConflicts = config.Bind("Behavior", "PreventActionConflicts",
                true,
                "If true, validate LLM actions before enqueueing them into the game's native activity interface.");

            ForceInterruptBusyActions = config.Bind("Behavior", "ForceInterruptBusyActions",
                false,
                "If true, clear the giantess' current native action queues when busy, then execute the latest LLM action.");

            ActionConflictPolicy = config.Bind("Behavior", "ActionConflictPolicy",
                "SkipWhenBusy",
                "Fallback behavior when ForceInterruptBusyActions is false and the native activity queue is busy. Supported values: SkipWhenBusy, ClearCurrentQueue, ClearAllQueues, Append.");

            AllowActionsDuringScriptedState = config.Bind("Behavior", "AllowActionsDuringScriptedState",
                true,
                "Allow actions while the AI is in GTSSCRIPT/EXEC_SCRIPT. Busy native queues are still handled by ActionConflictPolicy.");

            IdleStateId = config.Bind("Behavior", "IdleStateId",
                0,
                "The state ID to set when interrupting AI (usually 0 for IDLE).");

            // Reflection & Collection
            MaxSceneObjects = config.Bind("Reflection", "MaxSceneObjects",
                16,
                "Maximum number of nearby objects to send to LLM.");

            TableKeywords = config.Bind("Reflection", "TableKeywords",
                "table,desk,桌,counter,bench",
                "Comma-separated keywords to identify table-like surfaces.");

            BedKeywords = config.Bind("Reflection", "BedKeywords",
                "bed,床,sofa,couch",
                "Comma-separated keywords to identify bed-like surfaces.");

            TrendThreshold = config.Bind("Reflection", "TrendThreshold",
                0.05f,
                "Minimum change rate to report trend (+/-) for stomach/acid.");

            EmotionBlendTime = config.Bind("Reflection", "EmotionBlendTime",
                0.25f,
                "Seconds to blend facial expressions.");

            ApiTimeoutMs = config.Bind("LLM API", "ApiTimeoutMs",
                45000,
                "Timeout in milliseconds for LLM API calls.");

            // Keys
            ToggleUIKey = config.Bind("Keys", "ToggleUI",
                KeyCode.F8,
                "Key to toggle the mod overlay UI");

            ManualTriggerKey = config.Bind("Keys", "ManualTrigger",
                KeyCode.F7,
                "Key to manually trigger an LLM call");

            ProbeKey = config.Bind("Keys", "ReflectionProbe",
                KeyCode.F9,
                "Key to run the reflection probe (debug)");

            // Debug
            DebugLogging = config.Bind("Debug", "DebugLogging",
                false,
                "Enable verbose debug logging");

            EnableDebugPropertyTools = config.Bind("Debug", "EnableDebugPropertyTools",
                false,
                "Expose debug-only float property write tools to the LLM. Keep disabled unless actively testing.");

            // System Prompt
            CustomSystemPrompt = config.Bind("Prompt", "SystemPrompt",
                "",
                "Legacy inline system prompt override. If empty, SystemPromptFile is used.");

            SystemPromptFile = config.Bind("Prompt", "SystemPromptFile",
                "llm_system_prompt.conf",
                "System prompt config file path. Relative paths are resolved under BepInEx/plugins/GiantessLLMMod, then BepInEx/plugins.");

            ReloadPromptDefinitions();
        }

        /// <summary>
        /// Builds the full system prompt with action/emotion lists injected.
        /// </summary>
        public string GetSystemPrompt()
        {
            if (!string.IsNullOrEmpty(CustomSystemPrompt.Value))
                return CustomSystemPrompt.Value;

            return LoadPromptFromFile();
        }

        private string LoadPromptFromFile()
        {
            string path = ResolvePromptPath();

            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, "# Missing prompt. Put your full prompt in [system_prompt].", Encoding.UTF8);
                }

                string text = File.ReadAllText(path, Encoding.UTF8);
                ActionDefinitions.LoadFromPromptConfig(text);
                string prompt = ParsePromptConfig(text);
                return string.IsNullOrWhiteSpace(prompt)
                    ? "Reply only with a valid JSON action object."
                    : prompt;
            }
            catch
            {
                return "Reply only with a valid JSON action object.";
            }
        }

        private string ParsePromptConfig(string text)
        {
            bool useCustomPrompt = false;
            var system = new StringBuilder();
            var custom = new StringBuilder();
            StringBuilder current = null;

            using (var reader = new StringReader(text ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();

                    if (trimmed.StartsWith("#") || trimmed.StartsWith(";"))
                        continue;

                    if (trimmed.Equals("[system_prompt]", StringComparison.OrdinalIgnoreCase))
                    {
                        current = system;
                        continue;
                    }

                    if (trimmed.Equals("[custom_prompt]", StringComparison.OrdinalIgnoreCase))
                    {
                        current = custom;
                        continue;
                    }

                    if (trimmed.Equals("[action_whitelist]", StringComparison.OrdinalIgnoreCase)
                        || trimmed.Equals("[emotion_whitelist]", StringComparison.OrdinalIgnoreCase))
                    {
                        current = null;
                        continue;
                    }

                    int eq = trimmed.IndexOf('=');
                    if (eq > 0 && current == null)
                    {
                        string key = trimmed.Substring(0, eq).Trim();
                        string value = trimmed.Substring(eq + 1).Trim();
                        if (key.Equals("use_custom_prompt", StringComparison.OrdinalIgnoreCase))
                        {
                            useCustomPrompt = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                                || value.Equals("1", StringComparison.OrdinalIgnoreCase)
                                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                                || value.Equals("on", StringComparison.OrdinalIgnoreCase);
                            continue;
                        }
                    }

                    if (current != null)
                        current.AppendLine(line);
                }
            }

            string result = system.ToString().Trim();
            if (useCustomPrompt)
            {
                string customText = custom.ToString().Trim();
                if (!string.IsNullOrEmpty(customText))
                    result += "\n\n" + customText;
            }

            return result;
        }

        public string ResolvePromptPath()
        {
            string configured = SystemPromptFile.Value;
            if (string.IsNullOrWhiteSpace(configured))
                configured = "llm_system_prompt.txt";

            if (Path.IsPathRooted(configured))
                return configured;

            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string modDir = Path.Combine(assemblyDir, "GiantessLLMMod");

            if (Directory.Exists(modDir))
                return Path.Combine(modDir, configured);

            return Path.Combine(assemblyDir, configured);
        }

        public void ReloadPromptDefinitions()
        {
            try
            {
                string path = ResolvePromptPath();
                if (File.Exists(path))
                    ActionDefinitions.LoadFromPromptConfig(File.ReadAllText(path, Encoding.UTF8));
            }
            catch
            {
                ActionDefinitions.ResetToDefaults();
            }
        }

        public void Save()
        {
            _configFile?.Save();
        }
    }
}
