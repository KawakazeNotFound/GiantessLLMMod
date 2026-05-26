using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx.Logging;
using GiantessLLMMod.Models;
using Newtonsoft.Json;

namespace GiantessLLMMod.Core
{
    /// <summary>
    /// OpenAI-compatible HTTP client for LLM communication.
    /// Uses HttpWebRequest + ThreadPool for non-blocking calls in Unity's Mono runtime.
    /// </summary>
    public class LLMClient
    {
        private readonly ManualLogSource _log;
        private readonly ConfigManager _config;
        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private readonly object _historyLock = new object();

        // Thread-safe callback queue for main thread dispatch
        private readonly Queue<Action> _mainThreadQueue = new Queue<Action>();
        private readonly object _queueLock = new object();

        private bool _isBusy = false;
        public bool IsBusy => _isBusy;

        public LLMClient(ManualLogSource log, ConfigManager config)
        {
            _log = log;
            _config = config;
        }

        /// <summary>
        /// Must be called from Update() to process callbacks on the main thread.
        /// </summary>
        public void ProcessMainThreadCallbacks()
        {
            lock (_queueLock)
            {
                while (_mainThreadQueue.Count > 0)
                {
                    try { _mainThreadQueue.Dequeue()?.Invoke(); }
                    catch (Exception ex) { _log.LogError($"Callback error: {ex}"); }
                }
            }
        }

        /// <summary>
        /// Send game state to LLM and get an action response (async via ThreadPool).
        /// </summary>
        public void SendRequest(GameStateSnapshot state, Action<LLMActionResponse> onSuccess, Action<string> onError)
        {
            if (_isBusy)
            {
                onError?.Invoke("LLM client is busy with a previous request");
                return;
            }

            // Dry run mode
            if (_config.DryRunMode.Value)
            {
                var dryResponse = GetDryRunResponse(state);
                onSuccess?.Invoke(dryResponse);
                return;
            }

            _isBusy = true;

            // Build the user message from game state
            string userContent = FormatGameStateForLLM(state);

            // Build messages array
            var messages = new List<ChatMessage>();
            messages.Add(new ChatMessage("system", _config.GetSystemPrompt()));

            lock (_historyLock)
            {
                messages.AddRange(_history);
            }

            messages.Add(new ChatMessage("user", userContent));

            // Fire off to thread pool
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string assistantContent = null;
                    LLMActionResponse actionResponse = null;
                    var requestMessages = new List<ChatMessage>(messages);
                    const int maxRetries = 2;

                    for (int attempt = 0; attempt <= maxRetries; attempt++)
                    {
                        var request = new ChatCompletionRequest
                        {
                            Model = _config.ModelName.Value,
                            Messages = requestMessages,
                            Temperature = _config.Temperature.Value,
                            MaxTokens = _config.MaxTokens.Value
                        };

                        string requestJson = JsonConvert.SerializeObject(request);
                        string responseJson = DoHttpPost(
                            _config.ApiBaseUrl.Value.TrimEnd('/') + "/chat/completions",
                            requestJson,
                            _config.ApiKey.Value,
                            30000 // 30s timeout
                        );

                        var response = JsonConvert.DeserializeObject<ChatCompletionResponse>(responseJson);
                        if (response?.Choices == null || response.Choices.Count == 0)
                        {
                            EnqueueMainThread(() =>
                            {
                                _isBusy = false;
                                onError?.Invoke("Empty response from LLM");
                            });
                            return;
                        }

                        assistantContent = response.Choices[0].Message.Content;

                        if (_config.DebugLogging.Value)
                            _log.LogInfo($"LLM raw response attempt {attempt + 1}: {assistantContent}");

                        actionResponse = ParseActionResponse(assistantContent, normalizeInvalid: false);
                        if (IsValidActionResponse(actionResponse))
                        {
                            NormalizeActionResponse(actionResponse);
                            break;
                        }

                        string badAction = actionResponse?.Action ?? "<missing>";
                        if (attempt >= maxRetries)
                        {
                            _log.LogWarning($"LLM returned invalid action after retries: {badAction}");
                            actionResponse = ParseActionResponse(assistantContent, normalizeInvalid: true);
                            break;
                        }

                        requestMessages.Add(new ChatMessage("assistant", assistantContent ?? ""));
                        requestMessages.Add(new ChatMessage(
                            "user",
                            "Invalid response: action must be exactly one of the whitelist. " +
                            $"Your action was '{badAction}'. Return only valid JSON. " +
                            ActionDefinitions.GetActionsDescription()
                        ));
                    }

                    // Update conversation history
                    lock (_historyLock)
                    {
                        _history.Add(new ChatMessage("user", userContent));
                        _history.Add(new ChatMessage("assistant", assistantContent));

                        // Trim history
                        int max = _config.MaxConversationHistory.Value;
                        while (_history.Count > max)
                        {
                            _history.RemoveAt(0);
                        }
                    }

                    EnqueueMainThread(() =>
                    {
                        _isBusy = false;
                        onSuccess?.Invoke(actionResponse);
                    });
                }
                catch (Exception ex)
                {
                    _log.LogError($"LLM request failed: {ex.Message}");

                    EnqueueMainThread(() =>
                    {
                        _isBusy = false;
                        onError?.Invoke(ex.Message);
                    });
                }
            });
        }

        /// <summary>
        /// Clear conversation history.
        /// </summary>
        public void ClearHistory()
        {
            lock (_historyLock) { _history.Clear(); }
        }

        public int HistoryCount
        {
            get { lock (_historyLock) { return _history.Count; } }
        }

        // ──────────────────── HTTP ────────────────────

        private string DoHttpPost(string url, string body, string apiKey, int timeoutMs)
        {
            var httpReq = (HttpWebRequest)WebRequest.Create(url);
            httpReq.Method = "POST";
            httpReq.ContentType = "application/json";
            httpReq.Timeout = timeoutMs;
            httpReq.ReadWriteTimeout = timeoutMs;

            if (!string.IsNullOrEmpty(apiKey))
            {
                httpReq.Headers["Authorization"] = $"Bearer {apiKey}";
            }

            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            httpReq.ContentLength = bodyBytes.Length;

            using (var stream = httpReq.GetRequestStream())
            {
                stream.Write(bodyBytes, 0, bodyBytes.Length);
            }

            using (var httpResp = (HttpWebResponse)httpReq.GetResponse())
            using (var reader = new StreamReader(httpResp.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        // ──────────────────── Response Parsing ────────────────────

        private LLMActionResponse ParseActionResponse(string rawContent, bool normalizeInvalid = true)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
                return normalizeInvalid
                    ? new LLMActionResponse { Action = "idle", Emotion = "neutral" }
                    : null;

            // Strip markdown code fences if present
            string json = rawContent.Trim();
            var fenceMatch = Regex.Match(json, @"```(?:json)?\s*\n?(.*?)\n?\s*```", RegexOptions.Singleline);
            if (fenceMatch.Success)
            {
                json = fenceMatch.Groups[1].Value.Trim();
            }

            // Try to find JSON object
            int braceStart = json.IndexOf('{');
            int braceEnd = json.LastIndexOf('}');
            if (braceStart >= 0 && braceEnd > braceStart)
            {
                json = json.Substring(braceStart, braceEnd - braceStart + 1);
            }

            try
            {
                var response = JsonConvert.DeserializeObject<LLMActionResponse>(json);
                if (response != null)
                {
                    if (normalizeInvalid)
                        NormalizeActionResponse(response);
                    return response;
                }
            }
            catch (JsonException ex)
            {
                _log.LogWarning($"Failed to parse LLM JSON: {ex.Message}");
            }

            if (!normalizeInvalid) return null;

            return new LLMActionResponse { Action = "face_player", Emotion = "curious", Dialogue = null };
        }

        private bool IsValidActionResponse(LLMActionResponse response)
        {
            return response != null
                && !string.IsNullOrEmpty(response.Action)
                && ActionDefinitions.AvailableActions.ContainsKey(response.Action);
        }

        private void NormalizeActionResponse(LLMActionResponse response)
        {
            if (response == null) return;

            if (string.IsNullOrEmpty(response.Action) || !ActionDefinitions.AvailableActions.ContainsKey(response.Action))
                response.Action = "idle";

            if (string.IsNullOrEmpty(response.Emotion) || !ActionDefinitions.EmotionMap.ContainsKey(response.Emotion))
                response.Emotion = "neutral";

            if (response.Dialogue != null && response.Dialogue.Length > 220)
                response.Dialogue = response.Dialogue.Substring(0, 217) + "...";
        }

        // ──────────────────── State Formatting ────────────────────

        /// <summary>
        /// Format game state as a human-readable summary for the LLM (not raw JSON).
        /// </summary>
        private string FormatGameStateForLLM(GameStateSnapshot state)
        {
            var sb = new StringBuilder();

            // Player
            if (state.Player != null)
            {
                var p = state.Player;
                sb.AppendLine($"P hp={p.Health:F0}/{p.MaxHealth:F0} alive={p.IsAlive} pos=({p.X:F0},{p.Y:F0},{p.Z:F0}) stomach={p.InStomach} mouth={p.InMouth} held={p.IsBeingHeld}");
            }

            // Giantesses
            foreach (var g in state.Giantesses)
            {
                sb.Append($"G name={g.Name} state={g.CurrentState} dist={g.DistanceToPlayer:F0} pred={g.PredatorType} hunger={g.Hunger:F1} horny={g.Horniness:F1} stom={g.StomachActivity:F1}");

                if (Math.Abs(g.StomachActivityRate) > 0.05f)
                    sb.Append($" stomRate={g.StomachActivityRate:+0.0;-0.0}");

                sb.Append($" acid={g.StomachAcid:F1}");

                if (Math.Abs(g.StomachAcidRate) > 0.05f)
                    sb.Append($" acidRate={g.StomachAcidRate:+0.0;-0.0}");

                sb.Append($" burp={g.BurpBuildUp:F1}");

                if (Math.Abs(g.BurpBuildUpRate) > 0.05f)
                    sb.Append($" burpRate={g.BurpBuildUpRate:+0.0;-0.0}");

                sb.Append($" mouthObj={g.HasObjectInMouth}");

                if (g.HeldObjectName != null)
                    sb.Append($" held={g.HeldObjectName}");

                if (g.PlayerMemory != null)
                {
                    var m = g.PlayerMemory;
                    sb.Append($" memSee={m.CanSee} loc={m.CurrentLocation} rel={m.Relationship} int={m.Interest:F1} anger={m.AngerValue:F1} looking={m.IsLookingAtUs}");
                }

                sb.AppendLine();
            }

            // Events
            if (state.RecentEvents != null && state.RecentEvents.Count > 0)
            {
                sb.AppendLine("Events: " + string.Join(" | ", state.RecentEvents));
            }

            // Player input
            if (!string.IsNullOrEmpty(state.PlayerInput))
            {
                sb.AppendLine($"Player says: {state.PlayerInput}");
            }

            if (!string.IsNullOrEmpty(state.PlayerChoice))
            {
                sb.AppendLine($"Player chose: {state.PlayerChoice}");
            }

            return sb.ToString();
        }

        // ──────────────────── Dry Run ────────────────────

        private LLMActionResponse GetDryRunResponse(GameStateSnapshot state)
        {
            if (state.Player != null && state.Player.InStomach)
            {
                return new LLMActionResponse
                {
                    Action = "pat_stomach",
                    Emotion = "smug",
                    Dialogue = "Still tucked away in there, are you? How cozy~"
                };
            }

            if (state.Player != null && state.Player.InMouth)
            {
                return new LLMActionResponse
                {
                    Action = "idle",
                    Emotion = "playful",
                    Dialogue = "Mmm, you taste interesting~"
                };
            }

            if (state.Player != null && state.Player.IsBeingHeld)
            {
                return new LLMActionResponse
                {
                    Action = "idle",
                    Emotion = "curious",
                    Dialogue = "Hehe, gotcha! What should I do with you?"
                };
            }

            return new LLMActionResponse
            {
                Action = "face_player",
                Emotion = "curious",
                Dialogue = "Hmm... what are you up to, little one?"
            };
        }

        // ──────────────────── Thread Safety ────────────────────

        private void EnqueueMainThread(Action action)
        {
            lock (_queueLock) { _mainThreadQueue.Enqueue(action); }
        }
    }
}
