using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using GiantessLLMMod.Core;
using GiantessLLMMod.Models;
using GiantessLLMMod.UI;
using Newtonsoft.Json;
using UnityEngine;

namespace GiantessLLMMod
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.giantess.llmmod";
        public const string PLUGIN_NAME = "Giantess LLM Mod";
        public const string PLUGIN_VERSION = "1.0.0";

        // Core systems
        private ConfigManager _config;
        private GameStateCollector _collector;
        private LLMClient _llmClient;
        private ActionExecutor _executor;
        private EventWatcher _eventWatcher;
        private ModOverlayUI _ui;

        // State
        private float _lastAutoTriggerTime = 0f;
        private bool _initialized = false;
        private GameStateSnapshot _lastSnapshot;

        private void Awake()
        {
            Logger.LogInfo($"=== {PLUGIN_NAME} v{PLUGIN_VERSION} loading ===");

            // Initialize config
            _config = new ConfigManager(Config);

            // Initialize systems
            _collector = new GameStateCollector(Logger);
            _llmClient = new LLMClient(Logger, _config);
            _executor = new ActionExecutor(Logger, _collector, _config);
            _eventWatcher = new EventWatcher(Logger);

            // Initialize UI
            _ui = new ModOverlayUI();
            _ui.Init(_config);

            Logger.LogInfo($"{PLUGIN_NAME} loaded. Press {_config.ToggleUIKey.Value} for overlay, " +
                $"{_config.ManualTriggerKey.Value} to trigger LLM, {_config.ProbeKey.Value} for reflection probe.");
        }

        private void Update()
        {
            // Process LLM callbacks on main thread
            _llmClient.ProcessMainThreadCallbacks();

            // Key handlers
            if (Input.GetKeyDown(_config.ToggleUIKey.Value))
            {
                _ui.Toggle();
                Input.ResetInputAxes();
            }

            UpdateCursorForOverlay();

            if (Input.GetKeyDown(_config.ProbeKey.Value))
                RunReflectionProbe();

            if (Input.GetKeyDown(_config.ManualTriggerKey.Value))
                TriggerLLM(null);

            // Check for player input from UI
            string playerInput = _ui.ConsumePendingInput();
            if (playerInput != null && playerInput.Length > 0)
                TriggerLLM(playerInput);

            string testAction = _ui.ConsumePendingTestAction();
            if (!string.IsNullOrEmpty(testAction))
                TestAction(testAction);

            // Lazy initialization - build reflection cache once we're in a gameplay level
            if (!_initialized)
            {
                if (_collector.BuildCache())
                {
                    _initialized = true;
                    _ui.AddLog("Reflection cache built. Mod fully initialized.");
                    Logger.LogInfo("Mod initialized — reflection cache built.");
                }
            }

            if (!_initialized) return;

            // Collect state periodically for UI and events
            _lastSnapshot = _collector.CollectState();
            if (_lastSnapshot != null)
            {
                _ui.SetLastState(_lastSnapshot);
                _eventWatcher.Update(_lastSnapshot);
            }

            // Automatic triggers
            if (!_llmClient.IsBusy)
            {
                float elapsed = Time.time - _lastAutoTriggerTime;
                bool shouldTrigger = false;

                if (_config.EventTriggerEnabled.Value && _eventWatcher.HasEvents && elapsed >= _config.EventTriggerCooldown.Value)
                    shouldTrigger = true;

                if (_config.TimedTriggerEnabled.Value && elapsed >= _config.TimedTriggerInterval.Value)
                    shouldTrigger = true;

                if (shouldTrigger)
                    TriggerLLM(null);
            }
        }

        private void OnGUI()
        {
            _ui.Draw();
        }

        private void UpdateCursorForOverlay()
        {
            if (!_ui.Visible) return;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2))
                Input.ResetInputAxes();
        }

        // ──────────────────── LLM Trigger ────────────────────

        private void TriggerLLM(string playerInput)
        {
            if (_llmClient.IsBusy)
            {
                _ui.AddLog("LLM is busy, skipping...");
                return;
            }

            _lastAutoTriggerTime = Time.time;

            // Collect fresh state
            var state = _collector.CollectState();
            if (state == null)
            {
                _ui.AddLog("Failed to collect game state.");
                return;
            }

            // Add player input if any
            state.PlayerInput = playerInput;

            // Add pending events
            var events = _eventWatcher.FlushEvents();
            state.RecentEvents = events;

            foreach (var e in events)
            {
                _ui.AddChatEntry("Event", e, new Color(0.7f, 0.7f, 0.7f));
            }

            _ui.AddLog($"Sending to LLM (events={events.Count}, input={playerInput != null})...");

            if (_config.DebugLogging.Value)
            {
                Logger.LogInfo($"LLM request: events={events.Count}, playerInput={playerInput}");
            }

            _llmClient.SendRequest(state,
                onSuccess: response =>
                {
                    HandleLLMResponse(response);
                },
                onError: error =>
                {
                    _ui.AddLog($"LLM Error: {error}");
                    _ui.AddChatEntry("System", $"Error: {error}", Color.red);
                    Logger.LogError($"LLM error: {error}");
                }
            );
        }

        private void HandleLLMResponse(LLMActionResponse response)
        {
            if (response == null) return;

            // Log to UI
            string actionDesc = $"[{response.Emotion}] {response.Action}";
            if (!string.IsNullOrEmpty(response.Dialogue))
            {
                _ui.AddChatEntry(GetGiantessName(), response.Dialogue, new Color(1f, 0.9f, 0.4f));
            }
            _ui.AddLog($"LLM: {actionDesc}");

            if (response.Ask != null)
            {
                _ui.AddChatEntry(GetGiantessName(),
                    $"[Ask] {response.Ask.Question} ({response.Ask.Choice1} / {response.Ask.Choice2})",
                    new Color(1f, 0.8f, 0.3f));
            }

            // Execute the action
            _executor.Execute(response);
            _ui.AddLog($"Executed: {_executor.LastExecutionLog}");
        }

        private void TestAction(string action)
        {
            if (!_initialized)
            {
                _ui.AddLog("Cannot test action before reflection cache is ready.");
                return;
            }

            _config.ReloadPromptDefinitions();
            _ui.AddLog($"Testing action: {action}");
            _executor.Execute(new LLMActionResponse
            {
                Action = action,
                Emotion = null,
                Dialogue = null,
                Ask = null
            });
            _ui.AddLog($"Test result: {_executor.LastExecutionLog}");
        }

        private string GetGiantessName()
        {
            if (_lastSnapshot?.Giantesses != null && _lastSnapshot.Giantesses.Count > 0)
                return _lastSnapshot.Giantesses[0].Name ?? "Giantess";
            return "Giantess";
        }

        // ──────────────────── Reflection Probe ────────────────────

        private void RunReflectionProbe()
        {
            Logger.LogInfo("=== Running Reflection Probe ===");
            _ui.AddLog("Running reflection probe...");

            try
            {
                var sb = new StringBuilder();
                var jsonSb = new StringBuilder();
                jsonSb.AppendLine("{");

                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                var gameAssembly = assemblies.FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

                if (gameAssembly == null)
                {
                    Logger.LogError("Assembly-CSharp not found!");
                    _ui.AddLog("ERROR: Assembly-CSharp not found!");
                    return;
                }

                sb.AppendLine($"Assembly-CSharp: {gameAssembly.FullName}");
                sb.AppendLine($"Total types: {gameAssembly.GetTypes().Length}");
                sb.AppendLine();

                // Target classes
                string[] targetClasses = {
                    "GiantessAI", "GiantessActivityInterface", "GiantessMemory", "GiantessEmotion",
                    "GiantessMovement", "GiantessClothes", "GiantessPerception", "GiantessConversation",
                    "GiantessPersonality", "GiantessBoneLayout", "GiantessAutomation", "GiantessCache",
                    "GiantessScript", "StomachLogic", "OpenMouthTrigger", "MouthTrigger",
                    "FPSBehaviour", "FirstPersonAIO", "ReimuAnimationController", "DigestableObject",
                    "HeightCache", "AdvancedPlayerBot", "PickUpBehaviour", "ToolGunController",
                    "EntityMemory", "SubCacheClass", "Tablet"
                };

                string[] targetEnums = {
                    "MouthFlexType", "EyesFlexType", "EyeTargetType", "ThroatFlexType",
                    "TongueFlexType", "HeadTiltType", "GiantessStateType", "LocationType",
                    "GiantessRelationshipType", "GiantessPredatorType", "EHandCutsceneType",
                    "EProcAnimType", "ClothesType"
                };

                // Probe classes
                jsonSb.AppendLine("  \"classes\": {");
                bool first = true;
                foreach (var className in targetClasses)
                {
                    var type = gameAssembly.GetTypes().FirstOrDefault(t => t.Name == className);
                    if (type != null)
                    {
                        if (!first) jsonSb.AppendLine(",");
                        first = false;
                        ProbeClassToJson(type, sb, jsonSb);
                    }
                    else
                    {
                        sb.AppendLine($"[NOT FOUND] {className}");
                    }
                }
                jsonSb.AppendLine("\n  },");

                // Probe enums
                jsonSb.AppendLine("  \"enums\": {");
                first = true;
                foreach (var enumName in targetEnums)
                {
                    var type = gameAssembly.GetTypes().FirstOrDefault(t => t.Name == enumName);
                    if (type != null && type.IsEnum)
                    {
                        if (!first) jsonSb.AppendLine(",");
                        first = false;
                        ProbeEnumToJson(type, sb, jsonSb);
                    }
                    else
                    {
                        sb.AppendLine($"[ENUM NOT FOUND] {enumName}");
                    }
                }
                jsonSb.AppendLine("\n  },");

                // All relevant types
                jsonSb.AppendLine("  \"all_relevant_types\": [");
                var relevant = gameAssembly.GetTypes()
                    .Where(t => !t.IsNested && (
                        t.Name.Contains("Giantess") || t.Name.Contains("Stomach") ||
                        t.Name.Contains("Player") || t.Name.Contains("FPS") ||
                        t.Name.Contains("Digest") || t.Name.Contains("Entity") ||
                        t.Name.Contains("Memory") || t.Name.Contains("Emotion") ||
                        t.Name.Contains("Mouth") || t.Name.Contains("Script") ||
                        t.Name.Contains("Activity") || t.Name.Contains("Cache") ||
                        t.Name.Contains("Bone") || t.Name.Contains("Height")
                    ))
                    .OrderBy(t => t.Name).ToList();

                for (int i = 0; i < relevant.Count; i++)
                {
                    string kind = relevant[i].IsEnum ? "enum" : "class";
                    jsonSb.Append($"    \"{relevant[i].FullName} ({kind})\"");
                    if (i < relevant.Count - 1) jsonSb.AppendLine(",");
                    sb.AppendLine($"  [{kind}] {relevant[i].FullName}");
                }
                jsonSb.AppendLine("\n  ]");
                jsonSb.AppendLine("}");

                // Write output files
                string bepDir = Path.GetDirectoryName(typeof(BaseUnityPlugin).Assembly.Location);
                if (string.IsNullOrEmpty(bepDir))
                    bepDir = Path.Combine(Application.dataPath, "..", "BepInEx");

                string txtPath = Path.Combine(bepDir, "probe_output.txt");
                string jsonPath = Path.Combine(bepDir, "probe_output.json");

                File.WriteAllText(txtPath, sb.ToString());
                File.WriteAllText(jsonPath, jsonSb.ToString());

                Logger.LogInfo($"Probe complete! {txtPath}");
                _ui.AddLog($"Probe done → {txtPath}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Probe error: {ex}");
                _ui.AddLog($"Probe error: {ex.Message}");
            }
        }

        private void ProbeClassToJson(Type type, StringBuilder sb, StringBuilder jsonSb)
        {
            string baseName = type.BaseType?.Name ?? "none";
            sb.AppendLine($"[class] {type.FullName} : {baseName}");

            jsonSb.AppendLine($"    \"{type.Name}\": {{");
            jsonSb.AppendLine($"      \"fullName\": \"{type.FullName}\",");
            jsonSb.AppendLine($"      \"baseType\": \"{baseName}\",");

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

            // Fields
            var fields = type.GetFields(flags);
            jsonSb.AppendLine("      \"fields\": [");
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                string acc = f.IsPublic ? "public" : "private";
                string stat = f.IsStatic ? " static" : "";
                sb.AppendLine($"  {acc}{stat} {f.FieldType.Name} {f.Name}");
                string safeName = f.FieldType.Name.Replace("\"", "'");
                jsonSb.Append($"        {{\"name\": \"{f.Name}\", \"type\": \"{safeName}\", \"access\": \"{acc}{stat}\"}}");
                jsonSb.AppendLine(i < fields.Length - 1 ? "," : "");
            }
            jsonSb.AppendLine("      ],");

            // Methods (declared only, non-special)
            var methods = type.GetMethods(flags | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName).ToArray();
            jsonSb.AppendLine("      \"methods\": [");
            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                string acc = m.IsPublic ? "public" : "private";
                string stat = m.IsStatic ? " static" : "";
                var pars = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                sb.AppendLine($"  {acc}{stat} {m.ReturnType.Name} {m.Name}({pars})");
                pars = pars.Replace("\"", "'");
                jsonSb.Append($"        {{\"name\": \"{m.Name}\", \"return\": \"{m.ReturnType.Name}\", \"params\": \"{pars}\", \"access\": \"{acc}{stat}\"}}");
                jsonSb.AppendLine(i < methods.Length - 1 ? "," : "");
            }
            jsonSb.AppendLine("      ]");
            jsonSb.Append("    }");
            sb.AppendLine();
        }

        private void ProbeEnumToJson(Type type, StringBuilder sb, StringBuilder jsonSb)
        {
            sb.AppendLine($"[enum] {type.FullName}");
            var names = Enum.GetNames(type);
            var values = Enum.GetValues(type);

            jsonSb.AppendLine($"    \"{type.Name}\": {{");
            jsonSb.AppendLine($"      \"fullName\": \"{type.FullName}\",");
            jsonSb.AppendLine("      \"values\": [");
            for (int i = 0; i < names.Length; i++)
            {
                int v = Convert.ToInt32(values.GetValue(i));
                sb.AppendLine($"  {names[i]} = {v}");
                jsonSb.Append($"        {{\"name\": \"{names[i]}\", \"value\": {v}}}");
                jsonSb.AppendLine(i < names.Length - 1 ? "," : "");
            }
            jsonSb.AppendLine("      ]");
            jsonSb.Append("    }");
            sb.AppendLine();
        }
    }
}
