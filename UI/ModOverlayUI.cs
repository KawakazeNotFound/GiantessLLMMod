using System.Collections.Generic;
using GiantessLLMMod.Core;
using GiantessLLMMod.Models;
using UnityEngine;

namespace GiantessLLMMod.UI
{
    /// <summary>
    /// IMGUI overlay providing status display, chat, config, and log tabs.
    /// </summary>
    public class ModOverlayUI
    {
        private bool _visible = false;
        private Rect _windowRect = new Rect(20, 20, 520, 420);
        private int _currentTab = 0;
        private readonly string[] _tabs = { "Status", "Chat", "Performance", "Config", "Log" };

        // Chat state
        private readonly List<ChatEntry> _chatLog = new List<ChatEntry>();
        private string _chatInput = "";
        private Vector2 _chatScroll;

        // Status scroll
        private Vector2 _statusScroll;
        private Vector2 _performanceScroll;
        private float _nextPerformanceViewRefresh;
        private List<PerfMetricSnapshot> _performanceView = new List<PerfMetricSnapshot>();
        private PerfHistorySnapshot _performanceHistory;

        // Config scroll
        private Vector2 _configScroll;

        // Config temp values
        private string _cfgApiUrl, _cfgApiKey, _cfgModel;
        private string _cfgMaxTokens;
        private int _cfgTokenLimitMode;
        private static readonly string[] TokenLimitModes = { "Auto", "max_tokens", "max_completion_tokens" };
        private string _testActionInput = "face_player";
        private bool _cfgTimedTrigger, _cfgEventTrigger, _cfgDebug, _cfgNativeDialogue, _cfgDryRun, _cfgForceInterruptBusyActions;
        private float _cfgTimedTriggerInterval, _cfgEventTriggerCooldown;

        // Log
        private readonly List<string> _logEntries = new List<string>();
        private Vector2 _logScroll;

        // References
        private ConfigManager _config;
        private PerformanceMonitor _performance;
        private GameStateSnapshot _lastState;

        // Styles (lazily initialized)
        private GUIStyle _boxStyle, _labelStyle, _headerStyle, _chatStyle;
        private bool _stylesInit = false;

        // Public accessors
        public bool Visible => _visible;
        public string PendingPlayerInput { get; private set; }
        public string PendingTestAction { get; private set; }

        public void Init(ConfigManager config, PerformanceMonitor performance = null)
        {
            _config = config;
            _performance = performance;
            SyncConfigValues();
        }

        public void Toggle() => _visible = !_visible;

        public bool ContainsScreenMouse()
        {
            Vector2 guiMouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return _windowRect.Contains(guiMouse);
        }

        public void SetLastState(GameStateSnapshot state) => _lastState = state;

        public void AddChatEntry(string sender, string message, Color color)
        {
            _chatLog.Add(new ChatEntry { Sender = sender, Message = message, Color = color, Time = Time.time });
            if (_chatLog.Count > 200) _chatLog.RemoveAt(0);
            _chatScroll.y = float.MaxValue; // Auto-scroll
        }

        public void AddLog(string entry)
        {
            _logEntries.Add($"[{Time.time:F1}] {entry}");
            if (_logEntries.Count > 500) _logEntries.RemoveAt(0);
        }

        /// <summary>
        /// Consumes and returns any pending player input, or null.
        /// </summary>
        public string ConsumePendingInput()
        {
            var input = PendingPlayerInput;
            PendingPlayerInput = null;
            return input;
        }

        public string ConsumePendingTestAction()
        {
            var action = PendingTestAction;
            PendingTestAction = null;
            return action;
        }

        public void Draw()
        {
            if (!_visible) return;
            InitStyles();
            _windowRect = GUI.Window(98765, _windowRect, DrawWindow, "GiantessLLMMod — F8 Menu");

            var e = Event.current;
            if (e != null && _windowRect.Contains(e.mousePosition) &&
                (e.isMouse || e.type == EventType.ScrollWheel))
            {
                e.Use();
            }
        }

        private void DrawWindow(int id)
        {
            _currentTab = GUILayout.Toolbar(_currentTab, _tabs);
            GUILayout.Space(5);

            switch (_currentTab)
            {
                case 0: DrawStatusTab(); break;
                case 1: DrawChatTab(); break;
                case 2: DrawPerformanceTab(); break;
                case 3: DrawConfigTab(); break;
                case 4: DrawLogTab(); break;
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        // ──────────────────── Performance Tab ────────────────────

        private void DrawPerformanceTab()
        {
            _performanceScroll = GUILayout.BeginScrollView(_performanceScroll);

            if (_performance == null)
            {
                GUILayout.Label("Performance counters are not active for this client.", _labelStyle);
                GUILayout.EndScrollView();
                return;
            }

            GUILayout.Label("── Runtime ──", _headerStyle);
            GUILayout.Label($"FPS: {_performance.Fps:F1}    Frame: {_performance.FrameMs:F2} ms", _labelStyle);
            GUILayout.Label($"Managed memory: {FormatBytes(_performance.ManagedBytes)}", _labelStyle);
            GUILayout.Label($"GC collections (last sample): {_performance.GcCollections}", _labelStyle);

            GUILayout.Space(6);
            bool autoReturn = GUILayout.Toggle(
                _config.AutoReturnToGameOnOutsideClick.Value,
                "Click outside window to return controls to game");
            if (autoReturn != _config.AutoReturnToGameOnOutsideClick.Value)
            {
                _config.AutoReturnToGameOnOutsideClick.Value = autoReturn;
                _config.Save();
            }
            GUILayout.Label(
                autoReturn
                    ? "F8 menu stays visible; click inside it to capture the cursor again."
                    : "F8 menu keeps keyboard and mouse focus until closed or focus mode is enabled.",
                _labelStyle);

            if (_performance.SceneStatsAvailable)
            {
                GUILayout.Space(6);
                GUILayout.Label("── Active Scene Data ──", _headerStyle);
                if (_performance.ActiveColliderCount > 0)
                    GUILayout.Label($"Active non-trigger colliders: {_performance.ActiveColliderCount}", _labelStyle);
                if (_performance.GiantessCount > 0)
                    GUILayout.Label($"GiantessAI: {_performance.GiantessCount}", _labelStyle);
                if (_performance.MouthTriggerCount > 0)
                    GUILayout.Label($"MouthTrigger: {_performance.MouthTriggerCount}", _labelStyle);
                if (_performance.CachedSurfaces > 0)
                    GUILayout.Label($"Cached surfaces: {_performance.CachedSurfaces}", _labelStyle);
            }

            if (Time.unscaledTime >= _nextPerformanceViewRefresh)
            {
                _nextPerformanceViewRefresh = Time.unscaledTime + 0.5f;
                _performanceView = _performance.GetActiveMetrics();
                _performanceHistory = _performance.GetHistory();
            }

            if (_performanceHistory != null && _performanceHistory.Count > 1)
            {
                GUILayout.Space(6);
                GUILayout.Label("── 30 Second History ──", _headerStyle);
                DrawHistoryChart("Frame time (ms)", _performanceHistory.FrameMs, new Color(1f, 0.75f, 0.2f), true);
                DrawHistoryChart("FPS", _performanceHistory.Fps, new Color(0.35f, 1f, 0.45f), true);
                DrawHistoryChart("Managed memory (MB)", _performanceHistory.ManagedMb, new Color(0.3f, 0.85f, 1f), false);
            }

            if (_performanceView.Count > 0)
            {
                GUILayout.Space(6);
                GUILayout.Label("── Available Instrumentation ──", _headerStyle);
                GUILayout.Label("Element                         Last     Avg      Max    Calls", _labelStyle);
                foreach (var metric in _performanceView)
                {
                    GUILayout.Label(
                        $"{metric.Name,-28} {metric.LastMs,7:F3} {metric.AverageMs,7:F3} {metric.MaxMs,7:F3} {metric.Calls,7}",
                        _labelStyle);
                }
            }

            GUILayout.Space(8);
            if (GUILayout.Button("Reset Counters", GUILayout.Width(130)))
            {
                _performance.Reset();
                _performanceView.Clear();
                _performanceHistory = null;
                _nextPerformanceViewRefresh = 0f;
            }

            GUILayout.EndScrollView();
        }

        // ──────────────────── Status Tab ────────────────────

        private void DrawStatusTab()
        {
            _statusScroll = GUILayout.BeginScrollView(_statusScroll);

            if (_lastState == null)
            {
                GUILayout.Label("No game state collected yet. Enter a level.", _labelStyle);
                GUILayout.EndScrollView();
                return;
            }

            var p = _lastState.Player;
            if (p != null)
            {
                GUILayout.Label("── Player ──", _headerStyle);
                GUILayout.Label($"  Position: ({p.X:F1}, {p.Y:F1}, {p.Z:F1})", _labelStyle);
                GUILayout.Label($"  Health: {p.Health:F0}  Alive: {p.IsAlive}", _labelStyle);
                GUILayout.Label($"  InStomach: {p.InStomach}  InMouth: {p.InMouth}  Held: {p.IsBeingHeld}", _labelStyle);
            }

            foreach (var g in _lastState.Giantesses)
            {
                GUILayout.Space(5);
                GUILayout.Label($"── Giantess: {g.Name} ──", _headerStyle);
                GUILayout.Label($"  State: {g.CurrentState}  Distance: {g.DistanceToPlayer:F1}", _labelStyle);
                GUILayout.Label($"  Hunger: {g.Hunger:F1}  Horny: {g.Horniness:F1}", _labelStyle);
                GUILayout.Label($"  Stomach: act={g.StomachActivity:F1} acid={g.StomachAcid:F1} burp={g.BurpBuildUp:F1}", _labelStyle);
                if (g.HeldObjectName != null)
                    GUILayout.Label($"  Holding: {g.HeldObjectName}", _labelStyle);
                GUILayout.Label($"  Mouth occupied: {g.HasObjectInMouth}", _labelStyle);

                if (g.PlayerMemory != null)
                {
                    var m = g.PlayerMemory;
                    GUILayout.Label($"  Memory: see={m.CanSee} loc={m.CurrentLocation} rel={m.Relationship}", _labelStyle);
                    GUILayout.Label($"  Interest: {m.Interest:F1}  Anger: {m.AngerValue:F1}", _labelStyle);
                }
            }

            GUILayout.EndScrollView();
        }

        // ──────────────────── Chat Tab ────────────────────

        private void DrawChatTab()
        {
            _chatScroll = GUILayout.BeginScrollView(_chatScroll, GUILayout.Height(300));
            foreach (var entry in _chatLog)
            {
                var oldColor = GUI.color;
                GUI.color = entry.Color;
                GUILayout.Label($"[{entry.Sender}] {entry.Message}", _chatStyle);
                GUI.color = oldColor;
            }
            GUILayout.EndScrollView();

            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("chatInput");
            _chatInput = GUILayout.TextField(_chatInput, GUILayout.ExpandWidth(true));

            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && PendingPlayerInput == null;
            bool sendClicked = GUILayout.Button("Send", GUILayout.Width(60));
            GUI.enabled = wasEnabled;
            bool enterPressed = wasEnabled && PendingPlayerInput == null
                && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return
                && GUI.GetNameOfFocusedControl() == "chatInput";

            if ((sendClicked || enterPressed) && !string.IsNullOrWhiteSpace(_chatInput))
            {
                PendingPlayerInput = _chatInput.Trim();
                AddChatEntry("You", PendingPlayerInput, new Color(0.5f, 1f, 1f));
                _chatInput = "";
                GUI.FocusControl(null);
                if (enterPressed) Event.current.Use();
            }
            GUILayout.EndHorizontal();

            GUI.enabled = wasEnabled && PendingPlayerInput == null;
            if (GUILayout.Button("Manual LLM Trigger (F7)"))
            {
                // Handled by Plugin.cs checking for this
                PendingPlayerInput = PendingPlayerInput ?? "";
            }
            GUI.enabled = wasEnabled;
            if (PendingPlayerInput != null)
                GUILayout.Label("Queued: waiting for the current request to finish.");
        }

        // ──────────────────── Config Tab ────────────────────

        private void DrawConfigTab()
        {
            _configScroll = GUILayout.BeginScrollView(_configScroll);

            GUILayout.Label("── LLM API ──", _headerStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Full URL:", GUILayout.Width(70));
            _cfgApiUrl = GUILayout.TextField(_cfgApiUrl);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Key:", GUILayout.Width(50));
            _cfgApiKey = GUILayout.PasswordField(_cfgApiKey, '*');
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Model:", GUILayout.Width(50));
            _cfgModel = GUILayout.TextField(_cfgModel);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Token budget:", GUILayout.Width(110));
            _cfgMaxTokens = GUILayout.TextField(_cfgMaxTokens);
            GUILayout.EndHorizontal();
            GUILayout.Label("Token parameter (Auto recommended):");
            _cfgTokenLimitMode = GUILayout.Toolbar(_cfgTokenLimitMode, new[] { "Auto", "Legacy", "Completion" });
            GUILayout.Label("GPT-5/6 and o-series: sampling temperature omitted. GPT-6 Sol/Luna chat tools: reasoning=none.");

            GUILayout.Space(5);
            GUILayout.Label("── Behavior ──", _headerStyle);
            _cfgTimedTrigger = GUILayout.Toggle(_cfgTimedTrigger, "Timed trigger enabled");
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Timed interval: {_cfgTimedTriggerInterval:F0}s", GUILayout.Width(140));
            _cfgTimedTriggerInterval = GUILayout.HorizontalSlider(_cfgTimedTriggerInterval, 5f, 120f);
            GUILayout.EndHorizontal();
            _cfgEventTrigger = GUILayout.Toggle(_cfgEventTrigger, "Event trigger enabled");
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Event cooldown: {_cfgEventTriggerCooldown:F0}s", GUILayout.Width(140));
            _cfgEventTriggerCooldown = GUILayout.HorizontalSlider(_cfgEventTriggerCooldown, 5f, 300f);
            GUILayout.EndHorizontal();
            _cfgNativeDialogue = GUILayout.Toggle(_cfgNativeDialogue, "Use native dialogue (Say/Ask)");
            _cfgDryRun = GUILayout.Toggle(_cfgDryRun, "Dry-run mode (no LLM calls)");
            bool forceInterrupt = GUILayout.Toggle(_cfgForceInterruptBusyActions, "Force interrupt busy actions");
            if (forceInterrupt != _cfgForceInterruptBusyActions)
            {
                _cfgForceInterruptBusyActions = forceInterrupt;
                _config.ForceInterruptBusyActions.Value = forceInterrupt;
                AddLog($"Force interrupt busy actions: {(forceInterrupt ? "ON" : "OFF")}");
            }
            _cfgDebug = GUILayout.Toggle(_cfgDebug, "Debug logging");

            GUILayout.Space(5);
            GUILayout.Label("Action test", _headerStyle);
            GUILayout.BeginHorizontal();
            _testActionInput = GUILayout.TextField(_testActionInput);
            if (GUILayout.Button("Test", GUILayout.Width(60)) && !string.IsNullOrWhiteSpace(_testActionInput))
            {
                PendingTestAction = _testActionInput.Trim();
                AddLog($"Queued action test: {PendingTestAction}");
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply Settings"))
            {
                ApplyAndSave(false);
            }
            if (GUILayout.Button("Save to Disk", GUILayout.Width(100)))
            {
                ApplyAndSave(true);
            }
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Reset to Defaults"))
            {
                SyncConfigValues();
            }

            GUILayout.EndScrollView();
        }

        private void ApplyAndSave(bool saveToDisk)
        {
            if (!int.TryParse(_cfgMaxTokens, out int maxTokens) || maxTokens < 1 || maxTokens > 128000)
            {
                AddLog("Token budget must be an integer between 1 and 128000.");
                return;
            }
            _config.ApiBaseUrl.Value = _cfgApiUrl?.Trim();
            _config.ApiKey.Value = _cfgApiKey?.Trim();
            _config.ModelName.Value = _cfgModel?.Trim();
            _config.MaxTokens.Value = maxTokens;
            _config.TokenLimitParameter.Value = TokenLimitModes[_cfgTokenLimitMode];
            _config.TimedTriggerEnabled.Value = _cfgTimedTrigger;
            _config.TimedTriggerInterval.Value = Mathf.Clamp(_cfgTimedTriggerInterval, 5f, 120f);
            _config.EventTriggerEnabled.Value = _cfgEventTrigger;
            _config.EventTriggerCooldown.Value = Mathf.Clamp(_cfgEventTriggerCooldown, 5f, 300f);
            _config.EnableNativeDialogue.Value = _cfgNativeDialogue;
            _config.DryRunMode.Value = _cfgDryRun;
            _config.ForceInterruptBusyActions.Value = _cfgForceInterruptBusyActions;
            _config.DebugLogging.Value = _cfgDebug;

            if (saveToDisk)
            {
                _config.Save();
                AddLog("Settings applied and saved to disk.");
            }
            else
            {
                AddLog("Settings applied (memory only).");
            }
        }

        // ──────────────────── Log Tab ────────────────────

        private void DrawLogTab()
        {
            if (GUILayout.Button("Clear Log")) _logEntries.Clear();

            _logScroll = GUILayout.BeginScrollView(_logScroll);
            foreach (var entry in _logEntries)
            {
                GUILayout.Label(entry, _labelStyle);
            }
            GUILayout.EndScrollView();
        }

        // ──────────────────── Helpers ────────────────────

        private void SyncConfigValues()
        {
            if (_config == null) return;
            _cfgMaxTokens = _config.MaxTokens.Value.ToString();
            _cfgTokenLimitMode = System.Array.IndexOf(TokenLimitModes, _config.TokenLimitParameter.Value);
            if (_cfgTokenLimitMode < 0) _cfgTokenLimitMode = 0;
            _cfgApiUrl = _config.ApiBaseUrl.Value;
            _cfgApiKey = _config.ApiKey.Value;
            _cfgModel = _config.ModelName.Value;
            _cfgTimedTrigger = _config.TimedTriggerEnabled.Value;
            _cfgTimedTriggerInterval = _config.TimedTriggerInterval.Value;
            _cfgEventTrigger = _config.EventTriggerEnabled.Value;
            _cfgEventTriggerCooldown = _config.EventTriggerCooldown.Value;
            _cfgDebug = _config.DebugLogging.Value;
            _cfgNativeDialogue = _config.EnableNativeDialogue.Value;
            _cfgDryRun = _config.DryRunMode.Value;
            _cfgForceInterruptBusyActions = _config.ForceInterruptBusyActions.Value;
        }

        private void InitStyles()
        {
            if (_stylesInit) return;
            _boxStyle = new GUIStyle(GUI.skin.box);
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            _headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            _chatStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, richText = true };
            _stylesInit = true;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
                return $"{bytes / (1024d * 1024d * 1024d):F2} GB";
            if (bytes >= 1024L * 1024L)
                return $"{bytes / (1024d * 1024d):F1} MB";
            if (bytes >= 1024L)
                return $"{bytes / 1024d:F1} KB";
            return bytes + " B";
        }

        private void DrawHistoryChart(string title, float[] values, Color color, bool zeroBaseline)
        {
            if (values == null || values.Length < 2) return;

            GUILayout.Label(title, _labelStyle);
            Rect rect = GUILayoutUtility.GetRect(460f, 90f, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none, _boxStyle);

            float min = values[0];
            float max = values[0];
            for (int i = 1; i < values.Length; i++)
            {
                if (values[i] < min) min = values[i];
                if (values[i] > max) max = values[i];
            }

            if (zeroBaseline) min = 0f;
            if (max - min < 0.001f) max = min + 1f;

            Rect plot = new Rect(rect.x + 5f, rect.y + 15f, rect.width - 10f, rect.height - 22f);
            for (int i = 1; i < values.Length; i++)
            {
                float x0 = plot.x + plot.width * (i - 1) / (values.Length - 1f);
                float x1 = plot.x + plot.width * i / (values.Length - 1f);
                float y0 = plot.yMax - plot.height * Mathf.InverseLerp(min, max, values[i - 1]);
                float y1 = plot.yMax - plot.height * Mathf.InverseLerp(min, max, values[i]);
                DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), color, 2f);
            }

            GUI.Label(new Rect(rect.x + 6f, rect.y, rect.width - 12f, 18f),
                $"min {min:F1}    max {max:F1}    now {values[values.Length - 1]:F1}", _labelStyle);
        }

        private static void DrawLine(Vector2 start, Vector2 end, Color color, float width)
        {
            Vector2 delta = end - start;
            float length = delta.magnitude;
            if (length <= 0.01f) return;

            Matrix4x4 previousMatrix = GUI.matrix;
            Color previousColor = GUI.color;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            GUIUtility.RotateAroundPivot(angle, start);
            GUI.color = color;
            GUI.DrawTexture(new Rect(start.x, start.y - width * 0.5f, length, width), Texture2D.whiteTexture);
            GUI.matrix = previousMatrix;
            GUI.color = previousColor;
        }

        private struct ChatEntry
        {
            public string Sender;
            public string Message;
            public Color Color;
            public float Time;
        }
    }
}
