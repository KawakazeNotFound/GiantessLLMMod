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
        [JsonProperty("tools")] public List<ToolDefinition> Tools;
        [JsonProperty("tool_choice")] public object ToolChoice;
    }

    public class ChatMessage
    {
        [JsonProperty("role")] public string Role;
        [JsonProperty("content")] public string Content;
        [JsonProperty("tool_calls")] public List<ToolCall> ToolCalls;
        [JsonProperty("tool_call_id")] public string ToolCallId;

        public ChatMessage() { }
        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }

    public class ToolDefinition
    {
        [JsonProperty("type")] public string Type = "function";
        [JsonProperty("function")] public FunctionDefinition Function;
    }

    public class FunctionDefinition
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("description")] public string Description;
        [JsonProperty("parameters")] public object Parameters; // Use a JSON Schema object
    }

    public class ToolCall
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("type")] public string Type;
        [JsonProperty("function")] public FunctionCall Function;
    }

    public class FunctionCall
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("arguments")] public string Arguments;
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

    // ──────────────── Ollama API models ────────────────

    public class OllamaGenerateRequest
    {
        [JsonProperty("model")] public string Model;
        [JsonProperty("prompt")] public string Prompt;
        [JsonProperty("stream")] public bool Stream;
        [JsonProperty("options")] public OllamaOptions Options;
    }

    public class OllamaOptions
    {
        [JsonProperty("temperature")] public float Temperature;
        [JsonProperty("num_predict")] public int NumPredict;
    }

    public class OllamaGenerateResponse
    {
        [JsonProperty("response")] public string Response;
        [JsonProperty("done")] public bool Done;
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
