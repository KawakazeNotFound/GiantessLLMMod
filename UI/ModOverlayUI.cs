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
        private readonly string[] _tabs = { "Status", "Chat", "Config", "Log" };

        // Chat state
        private readonly List<ChatEntry> _chatLog = new List<ChatEntry>();
        private string _chatInput = "";
        private Vector2 _chatScroll;

        // Status scroll
        private Vector2 _statusScroll;

        // Config temp values
        private string _cfgApiUrl, _cfgApiKey, _cfgModel;
        private string _testActionInput = "face_player";
        private bool _cfgTimedTrigger, _cfgEventTrigger, _cfgDebug, _cfgNativeDialogue, _cfgDryRun;
        private float _cfgTimedTriggerInterval, _cfgEventTriggerCooldown;

        // Log
        private readonly List<string> _logEntries = new List<string>();
        private Vector2 _logScroll;

        // References
        private ConfigManager _config;
        private GameStateSnapshot _lastState;

        // Styles (lazily initialized)
        private GUIStyle _boxStyle, _labelStyle, _headerStyle, _chatStyle;
        private bool _stylesInit = false;

        // Public accessors
        public bool Visible => _visible;
        public string PendingPlayerInput { get; private set; }
        public string PendingTestAction { get; private set; }

        public void Init(ConfigManager config)
        {
            _config = config;
            SyncConfigValues();
        }

        public void Toggle() => _visible = !_visible;

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
            _windowRect = GUI.Window(98765, _windowRect, DrawWindow, "GiantessLLMMod v1.0");

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
                case 2: DrawConfigTab(); break;
                case 3: DrawLogTab(); break;
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
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
            _chatInput = GUILayout.TextField(_chatInput, GUILayout.ExpandWidth(true));

            bool sendClicked = GUILayout.Button("Send", GUILayout.Width(60));
            bool enterPressed = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return
                && GUI.GetNameOfFocusedControl() == "chatInput";

            if ((sendClicked || enterPressed) && !string.IsNullOrWhiteSpace(_chatInput))
            {
                PendingPlayerInput = _chatInput.Trim();
                AddChatEntry("You", PendingPlayerInput, new Color(0.5f, 1f, 1f));
                _chatInput = "";
                GUI.FocusControl(null);
            }
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Manual LLM Trigger (F7)"))
            {
                // Handled by Plugin.cs checking for this
                PendingPlayerInput = PendingPlayerInput ?? "";
            }
        }

        // ──────────────────── Config Tab ────────────────────

        private void DrawConfigTab()
        {
            GUILayout.Label("── LLM API ──", _headerStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("URL:", GUILayout.Width(50));
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
            if (GUILayout.Button("Apply Settings"))
            {
                _config.ApiBaseUrl.Value = _cfgApiUrl;
                _config.ApiKey.Value = _cfgApiKey;
                _config.ModelName.Value = _cfgModel;
                _config.TimedTriggerEnabled.Value = _cfgTimedTrigger;
                _config.TimedTriggerInterval.Value = Mathf.Clamp(_cfgTimedTriggerInterval, 5f, 120f);
                _config.EventTriggerEnabled.Value = _cfgEventTrigger;
                _config.EventTriggerCooldown.Value = Mathf.Clamp(_cfgEventTriggerCooldown, 5f, 300f);
                _config.EnableNativeDialogue.Value = _cfgNativeDialogue;
                _config.DryRunMode.Value = _cfgDryRun;
                _config.DebugLogging.Value = _cfgDebug;
                AddLog("Settings applied.");
            }

            if (GUILayout.Button("Reset to Defaults"))
            {
                SyncConfigValues();
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

        private struct ChatEntry
        {
            public string Sender;
            public string Message;
            public Color Color;
            public float Time;
        }
    }
}
