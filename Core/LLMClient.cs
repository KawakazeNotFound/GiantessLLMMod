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
        /// Supports Tool Calling.
        /// </summary>
        public void SendRequest(GameStateSnapshot state, Action<LLMActionResponse> onSuccess, Action<string> onError, List<ToolDefinition> tools = null)
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
                    const int maxToolRounds = 3;

                    for (int round = 0; round <= maxToolRounds; round++)
                    {
                        string apiUrl = _config.ApiBaseUrl.Value.Trim();
                        string apiKey = NormalizeApiKey(_config.ApiKey.Value);
                        bool isOllamaGenerate = apiUrl.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase);

                        string requestJson;
                        if (isOllamaGenerate)
                        {
                            var sb = new StringBuilder();
                            foreach (var msg in requestMessages)
                            {
                                if (msg.Content != null)
                                {
                                    sb.AppendLine($"{msg.Role.ToUpper()}:");
                                    sb.AppendLine(msg.Content);
                                    sb.AppendLine();
                                }
                            }
                            sb.AppendLine("ASSISTANT:");
                            
                            var ollamaReq = new OllamaGenerateRequest
                            {
                                Model = _config.ModelName.Value,
                                Prompt = sb.ToString(),
                                Stream = false,
                                Options = new OllamaOptions
                                {
                                    Temperature = _config.Temperature.Value,
                                    NumPredict = _config.MaxTokens.Value
                                }
                            };
                            requestJson = JsonConvert.SerializeObject(ollamaReq, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                        }
                        else
                        {
                            var request = new ChatCompletionRequest
                            {
                                Model = _config.ModelName.Value,
                                Messages = requestMessages,
                                Temperature = _config.Temperature.Value,
                                MaxTokens = _config.MaxTokens.Value,
                                Tools = tools
                            };

                            requestJson = JsonConvert.SerializeObject(request, new JsonSerializerSettings 
                            { 
                                NullValueHandling = NullValueHandling.Ignore 
                            });
                        }

                        string responseJson = DoHttpPost(
                            apiUrl,
                            requestJson,
                            apiKey,
                            _config.ApiTimeoutMs.Value
                        );

                        if (isOllamaGenerate)
                        {
                            var response = JsonConvert.DeserializeObject<OllamaGenerateResponse>(responseJson);
                            if (string.IsNullOrEmpty(response?.Response))
                            {
                                EnqueueMainThread(() =>
                                {
                                    _isBusy = false;
                                    onError?.Invoke("Empty response from Ollama API");
                                });
                                return;
                            }
                            assistantContent = response.Response;
                        }
                        else
                        {
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

                            var choice = response.Choices[0];
                            assistantContent = choice.Message.Content;

                            if (choice.Message.ToolCalls != null && choice.Message.ToolCalls.Count > 0)
                            {
                                requestMessages.Add(choice.Message);
                                requestMessages.AddRange(ExecuteToolCallsOnMainThread(choice.Message.ToolCalls));
                                assistantContent = null;
                                continue;
                            }
                        }

                        if (!string.IsNullOrEmpty(assistantContent))
                        {
                            actionResponse = ParseActionResponse(assistantContent, normalizeInvalid: false);
                            if (IsValidActionResponse(actionResponse))
                            {
                                NormalizeActionResponse(actionResponse);
                                break;
                            }
                        }
                    }

                    if (actionResponse == null)
                        actionResponse = new LLMActionResponse
                        {
                            Action = "face_player",
                            Emotion = "curious",
                            Dialogue = null
                        };

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
                catch (WebException ex)
                {
                    string detail = ReadWebExceptionDetail(ex);
                    _log.LogError($"LLM request failed: {detail}");
                    EnqueueMainThread(() => { _isBusy = false; onError?.Invoke(detail); });
                }
                catch (Exception ex)
                {
                    _log.LogError($"LLM request failed: {ex.Message}");
                    EnqueueMainThread(() => { _isBusy = false; onError?.Invoke(ex.Message); });
                }
            });
        }

        private List<ChatMessage> ExecuteToolCallsOnMainThread(List<ToolCall> toolCalls)
        {
            var done = new ManualResetEvent(false);
            List<ChatMessage> results = null;

            EnqueueMainThread(() =>
            {
                try
                {
                    results = new List<ChatMessage>();
                    foreach (var call in toolCalls)
                    {
                        string output = call?.Function == null
                            ? "{\"success\":false,\"error\":\"Malformed tool call\"}"
                            : ToolBridge.ExecuteTool(call.Function.Name, call.Function.Arguments);
                        results.Add(new ChatMessage("tool", output) { ToolCallId = call?.Id });
                    }
                }
                finally
                {
                    done.Set();
                }
            });

            if (!done.WaitOne(10000))
                throw new TimeoutException("Timed out waiting for tool execution on Unity main thread");

            return results ?? new List<ChatMessage>();
        }

        private string ReadWebExceptionDetail(WebException ex)
        {
            var response = ex.Response as HttpWebResponse;
            if (response == null)
                return ex.Message;

            string responseBody = "";
            try
            {
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    responseBody = reader.ReadToEnd();
                }
            }
            catch
            {
                // Preserve the status line if the response body cannot be read.
            }

            string status = $"{(int)response.StatusCode} {response.StatusDescription}";
            return string.IsNullOrWhiteSpace(responseBody)
                ? $"HTTP {status}: {ex.Message}"
                : $"HTTP {status}: {responseBody}";
        }

        private string NormalizeApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return "";

            string normalized = apiKey.Trim().Trim('"', '\'');
            const string bearerPrefix = "Bearer ";
            if (normalized.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(bearerPrefix.Length).Trim();

            return normalized;
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

            if (state.SceneObjects != null && state.SceneObjects.Count > 0)
            {
                sb.AppendLine("Scene object candidates:");
                foreach (var obj in state.SceneObjects)
                {
                    sb.AppendLine(
                        $"- id={obj.Id} kind={obj.Kind} name={obj.Name} pos=({obj.X:F0},{obj.Y:F0},{obj.Z:F0}) topY={obj.TopY:F1} size=({obj.Width:F1},{obj.Depth:F1}) dist={obj.DistanceToPlayer:F0}");
                }
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
