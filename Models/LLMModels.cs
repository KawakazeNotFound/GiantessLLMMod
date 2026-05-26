using System.Collections.Generic;
using Newtonsoft.Json;

namespace GiantessLLMMod.Models
{
    // ──────────────── OpenAI-compatible API models ────────────────

    public class ChatCompletionRequest
    {
        [JsonProperty("model")] public string Model;
        [JsonProperty("messages")] public List<ChatMessage> Messages;
        [JsonProperty("temperature")] public float Temperature;
        [JsonProperty("max_tokens")] public int MaxTokens;
    }

    public class ChatMessage
    {
        [JsonProperty("role")] public string Role;
        [JsonProperty("content")] public string Content;

        public ChatMessage() { }
        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }

    public class ChatCompletionResponse
    {
        [JsonProperty("choices")] public List<ChatChoice> Choices;
        [JsonProperty("usage")] public UsageInfo Usage;
    }

    public class ChatChoice
    {
        [JsonProperty("message")] public ChatMessage Message;
        [JsonProperty("finish_reason")] public string FinishReason;
    }

    public class UsageInfo
    {
        [JsonProperty("prompt_tokens")] public int PromptTokens;
        [JsonProperty("completion_tokens")] public int CompletionTokens;
        [JsonProperty("total_tokens")] public int TotalTokens;
    }

    // ──────────────── LLM structured response ────────────────

    /// <summary>
    /// The structured JSON response we expect the LLM to produce.
    /// </summary>
    public class LLMActionResponse
    {
        [JsonProperty("action")] public string Action = "idle";
        [JsonProperty("emotion")] public string Emotion = "neutral";
        [JsonProperty("dialogue")] public string Dialogue;
        [JsonProperty("ask")] public AskData Ask;
        [JsonProperty("parameters")] public Dictionary<string, object> Parameters;
    }

    public class AskData
    {
        [JsonProperty("question")] public string Question;
        [JsonProperty("choice1")] public string Choice1;
        [JsonProperty("choice2")] public string Choice2;
    }
}
