using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using GiantessLLMMod.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        private int _historyVersion;

        // Thread-safe callback queue for main thread dispatch
        private readonly Queue<Action> _mainThreadQueue = new Queue<Action>();
        private readonly object _queueLock = new object();

        private int _isBusy;
        public bool IsBusy => Volatile.Read(ref _isBusy) != 0;

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
            int count;
            lock (_queueLock) count = _mainThreadQueue.Count;
            // Execute a bounded batch outside the lock so callbacks cannot block
            // producers or recursively drain an unbounded number of new callbacks.
            for (int i = 0; i < count; i++)
            {
                Action callback;
                lock (_queueLock) callback = _mainThreadQueue.Dequeue();
                try { callback?.Invoke(); }
                catch (Exception ex) { _log.LogError($"Callback error: {ex}"); }
            }
        }

        /// <summary>
        /// Send game state to LLM and get an action response (async via ThreadPool).
        /// Supports Tool Calling.
        /// </summary>
        public void SendRequest(GameStateSnapshot state, Action<LLMActionResponse> onSuccess, Action<string> onError, List<ToolDefinition> tools = null)
        {
            if (Interlocked.CompareExchange(ref _isBusy, 1, 0) != 0)
            {
                onError?.Invoke("LLM client is busy with a previous request");
                return;
            }

            // Dry run mode
            if (_config.DryRunMode.Value)
            {
                try { onSuccess?.Invoke(GetDryRunResponse(state)); }
                finally { Interlocked.Exchange(ref _isBusy, 0); }
                return;
            }

            string userContent;
            string systemPrompt;
            string apiUrl, apiKey, model, tokenLimitParameter;
            float temperature;
            int maxTokens, timeoutMs, maxHistory, historyVersion, maxDialogueLength;
            bool debugLogging;
            List<ToolDefinition> requestTools;
            var messages = new List<ChatMessage>();
            try
            {
                if (state == null) throw new ArgumentNullException(nameof(state));
                userContent = FormatGameStateForLLM(state);
                systemPrompt = _config.GetSystemPrompt();
                // Freeze settings for the whole transaction. Applying settings in
                // the overlay must not switch provider/model halfway through tools.
                apiUrl = (_config.ApiBaseUrl.Value ?? "").Trim();
                apiKey = NormalizeApiKey(_config.ApiKey.Value);
                model = _config.ModelName.Value;
                temperature = _config.Temperature.Value;
                maxTokens = _config.MaxTokens.Value;
                tokenLimitParameter = _config.TokenLimitParameter.Value;
                timeoutMs = Math.Max(1, _config.ApiTimeoutMs.Value);
                maxHistory = Math.Max(0, _config.MaxConversationHistory.Value);
                maxHistory -= maxHistory % 2;
                maxDialogueLength = Math.Max(1, _config.MaxDialogueLength.Value);
                debugLogging = _config.DebugLogging.Value;
                requestTools = tools == null ? null : new List<ToolDefinition>(tools);
                messages.Add(new ChatMessage("system", systemPrompt));
                lock (_historyLock)
                {
                    historyVersion = _historyVersion;
                    int start = Math.Max(0, _history.Count - maxHistory);
                    messages.AddRange(_history.GetRange(start, _history.Count - start));
                }
                messages.Add(new ChatMessage("user", userContent));
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _isBusy, 0);
                string error = $"LLM request preparation error: {ex.Message}";
                _log.LogError(error);
                onError?.Invoke(error);
                return;
            }

            // Fire off to thread pool
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string assistantContent = null;
                    LLMActionResponse actionResponse = null;
                    string lastValidationError = "The model did not return a response.";
                    var requestMessages = new List<ChatMessage>(messages);
                    const int maxToolRounds = 3;

                    for (int round = 0; round <= maxToolRounds; round++)
                    {
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
                                Model = model,
                                Prompt = sb.ToString(),
                                Stream = false,
                                Options = new OllamaOptions
                                {
                                    Temperature = temperature,
                                    NumPredict = maxTokens
                                }
                            };
                            requestJson = JsonConvert.SerializeObject(ollamaReq, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                        }
                        else
                        {
                            var request = ChatRequestBuilder.Build(apiUrl, model, requestMessages,
                                temperature, maxTokens, requestTools, tokenLimitParameter);

                            requestJson = JsonConvert.SerializeObject(request, new JsonSerializerSettings 
                            { 
                                NullValueHandling = NullValueHandling.Ignore 
                            });
                        }

                        string responseJson = DoHttpPost(
                            apiUrl,
                            requestJson,
                            apiKey,
                            timeoutMs
                        );

                        if (string.IsNullOrWhiteSpace(responseJson))
                            throw new InvalidDataException("LLM API returned HTTP success with an empty response body.");

                        if (debugLogging)
                            _log.LogInfo($"LLM response received: round={round + 1}, bytes={Encoding.UTF8.GetByteCount(responseJson)}");

                        if (isOllamaGenerate)
                        {
                            var response = JsonConvert.DeserializeObject<OllamaGenerateResponse>(responseJson);
                            if (string.IsNullOrWhiteSpace(response?.Response))
                                throw new InvalidDataException("Ollama returned a response object, but its 'response' text was empty.");
                            assistantContent = response.Response;
                        }
                        else
                        {
                            var response = JsonConvert.DeserializeObject<ChatCompletionResponse>(responseJson);
                            if (response?.Choices == null || response.Choices.Count == 0)
                            {
                                EnqueueMainThread(() =>
                                {
                                    Interlocked.Exchange(ref _isBusy, 0);
                                    onError?.Invoke("Empty response from LLM");
                                });
                                return;
                            }

                            var choice = response.Choices[0];
                            if (choice == null)
                                throw new InvalidDataException("LLM response choices[0] was null.");
                            if (choice.Message == null)
                                throw new InvalidDataException("LLM response choices[0].message was missing.");

                            if (choice.FinishReason == "length")
                                throw new InvalidDataException("LLM response reached the completion token budget (finish_reason=length). Increase LLM API.MaxTokens; reasoning tokens also use this budget.");

                            assistantContent = choice.Message.Content;

                            if (choice.Message.ToolCalls != null && choice.Message.ToolCalls.Count > 0)
                            {
                                if (round >= maxToolRounds)
                                    throw new InvalidDataException($"LLM exceeded the tool-call limit ({maxToolRounds}) without returning a final text response.");

                                requestMessages.Add(choice.Message);
                                requestMessages.AddRange(ExecuteToolCallsOnMainThread(choice.Message.ToolCalls));
                                assistantContent = null;
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(assistantContent))
                            {
                                string finish = string.IsNullOrWhiteSpace(choice.FinishReason) ? "not provided" : choice.FinishReason;
                                throw new InvalidDataException($"LLM returned no message content (finish_reason={finish}).");
                            }
                        }

                        if (TryParseActionResponse(assistantContent, out actionResponse, out lastValidationError)
                            && TryValidateActionResponse(actionResponse, !string.IsNullOrWhiteSpace(state?.PlayerInput), out lastValidationError))
                        {
                            NormalizeActionResponse(actionResponse, maxDialogueLength);
                            break;
                        }

                        _log.LogWarning($"Rejected LLM response (round {round + 1}/{maxToolRounds + 1}): {lastValidationError}");

                        // Make retries corrective instead of sending the same request repeatedly.
                        requestMessages.Add(new ChatMessage("assistant", assistantContent));
                        requestMessages.Add(new ChatMessage("user",
                            $"Your previous response was invalid: {lastValidationError} " +
                            "Return exactly one JSON object with action, emotion, dialogue, ask, and parameters. " +
                            "Put spoken text in dialogue; 'speak' is not an action."));
                        actionResponse = null;
                    }

                    if (actionResponse == null)
                        throw new InvalidDataException(
                            $"LLM returned no usable response after {maxToolRounds + 1} attempts. Last validation error: {lastValidationError}");

                    // Update conversation history
                    lock (_historyLock)
                    {
                        // ClearHistory during a pending request must stay cleared.
                        if (_historyVersion == historyVersion)
                        {
                            _history.Add(new ChatMessage("user", userContent));
                            _history.Add(new ChatMessage("assistant", assistantContent));
                            int remove = _history.Count - maxHistory;
                            if (remove > 0) _history.RemoveRange(0, remove);
                        }
                    }

                    EnqueueMainThread(() =>
                    {
                        Interlocked.Exchange(ref _isBusy, 0);
                        onSuccess?.Invoke(actionResponse);
                    });
                }
                catch (WebException ex)
                {
                    string detail = ReadWebExceptionDetail(ex);
                    _log.LogError($"LLM request failed: {detail}");
                    EnqueueMainThread(() => { Interlocked.Exchange(ref _isBusy, 0); onError?.Invoke(detail); });
                }
                catch (Exception ex)
                {
                    _log.LogError($"LLM request failed: {ex.Message}");
                    EnqueueMainThread(() => { Interlocked.Exchange(ref _isBusy, 0); onError?.Invoke(ex.Message); });
                }
            });
        }

        private List<ChatMessage> ExecuteToolCallsOnMainThread(List<ToolCall> toolCalls, int timeoutMs = 10000)
        {
            var completion = new TaskCompletionSource<List<ChatMessage>>();
            int dispatchState = 0; // 0=pending, 1=started, 2=cancelled before dispatch

            EnqueueMainThread(() =>
            {
                if (Interlocked.CompareExchange(ref dispatchState, 1, 0) != 0)
                    return;
                try
                {
                    var results = new List<ChatMessage>();
                    foreach (var call in toolCalls)
                    {
                        string output = call?.Function == null
                            ? "{\"success\":false,\"error\":\"Malformed tool call\"}"
                            : ToolBridge.ExecuteTool(call.Function.Name, call.Function.Arguments);
                        results.Add(new ChatMessage("tool", output) { ToolCallId = call?.Id });
                    }
                    completion.TrySetResult(results);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });

            if (!completion.Task.Wait(timeoutMs))
            {
                // Paused/stalled main threads must not execute a stale mutation
                // after the request has already reported a timeout.
                Interlocked.CompareExchange(ref dispatchState, 2, 0);
                throw new TimeoutException("Timed out waiting for tool execution on Unity main thread");
            }

            return completion.Task.GetAwaiter().GetResult();
        }

        private string ReadWebExceptionDetail(WebException ex)
        {
            var response = ex.Response as HttpWebResponse;
            if (response == null)
                return ex.Message;

            using (response)
            {
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
            lock (_historyLock)
            {
                _historyVersion++;
                _history.Clear();
            }
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

        private bool TryParseActionResponse(string rawContent, out LLMActionResponse response, out string error)
        {
            response = null;
            error = null;

            if (string.IsNullOrWhiteSpace(rawContent))
            {
                error = "Assistant content was empty.";
                return false;
            }

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
                var obj = JObject.Parse(json);
                var actionToken = obj["action"];
                if (actionToken == null || actionToken.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)actionToken))
                {
                    error = "Required string field 'action' was missing or empty.";
                    return false;
                }

                response = obj.ToObject<LLMActionResponse>();
                if (response == null)
                {
                    error = "JSON object could not be converted to an action response.";
                    return false;
                }

                return true;
            }
            catch (JsonException ex)
            {
                _log.LogWarning($"Failed to parse LLM JSON: {ex.Message}");
                error = $"Invalid JSON: {ex.Message}";
                return false;
            }
        }

        private bool TryValidateActionResponse(LLMActionResponse response, bool playerAskedForReply, out string error)
        {
            error = null;

            if (response == null)
            {
                error = "Parsed response was null.";
                return false;
            }

            if (!ActionDefinitions.AvailableActions.ContainsKey(response.Action))
            {
                error = $"Unknown action '{response.Action}'. It is not in the configured action whitelist.";
                return false;
            }

            if (!ActionDefinitions.IsExecutableAction(response.Action))
            {
                error = $"Unsupported action '{response.Action}'. The game executor has no implementation for it.";
                return false;
            }

            if (playerAskedForReply
                && string.IsNullOrWhiteSpace(response.Dialogue)
                && (response.Ask == null || string.IsNullOrWhiteSpace(response.Ask.Question)))
            {
                error = "Player input received, but both 'dialogue' and 'ask.question' were empty.";
                return false;
            }

            return true;
        }

        private void NormalizeActionResponse(LLMActionResponse response, int maxDialogueLength)
        {
            if (response == null) return;

            if (string.IsNullOrEmpty(response.Action) || !ActionDefinitions.AvailableActions.ContainsKey(response.Action))
                response.Action = "idle";

            if (string.IsNullOrEmpty(response.Emotion) || !ActionDefinitions.EmotionMap.ContainsKey(response.Emotion))
                response.Emotion = "neutral";

            if (response.Dialogue != null && response.Dialogue.Length > maxDialogueLength)
            {
                int keep = maxDialogueLength > 3 ? maxDialogueLength - 3 : maxDialogueLength;
                // Do not cut a UTF-16 surrogate pair in half.
                if (keep > 0 && char.IsHighSurrogate(response.Dialogue[keep - 1])) keep--;
                response.Dialogue = response.Dialogue.Substring(0, keep) + (maxDialogueLength > 3 ? "..." : "");
            }
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
