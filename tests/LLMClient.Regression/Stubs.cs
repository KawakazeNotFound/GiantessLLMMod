using System;

// Only host adapters are stubbed: the real client, serializers and action
// validation are linked by the test project. No game or external API is started.
namespace BepInEx.Logging
{
    public class ManualLogSource
    {
        public void LogError(object message) { }
        public void LogWarning(object message) { }
        public void LogInfo(object message) { }
    }
}

namespace GiantessLLMMod.Core
{
    public class Entry<T>
    {
        public T Value;
        public Entry(T value) { Value = value; }
    }

    public class ConfigManager
    {
        public Entry<string> ApiBaseUrl = new Entry<string>("");
        public Entry<string> ApiKey = new Entry<string>("");
        public Entry<string> ModelName = new Entry<string>("local-model");
        public Entry<float> Temperature = new Entry<float>(0.7f);
        public Entry<int> MaxTokens = new Entry<int>(700);
        public Entry<string> TokenLimitParameter = new Entry<string>("Auto");
        public Entry<bool> DryRunMode = new Entry<bool>(false);
        public Entry<bool> DebugLogging = new Entry<bool>(false);
        public Entry<int> ApiTimeoutMs = new Entry<int>(3000);
        public Entry<int> MaxConversationHistory = new Entry<int>(20);
        public Entry<int> MaxDialogueLength = new Entry<int>(200);
        public Exception PromptError;
        public string GetSystemPrompt()
        {
            if (PromptError != null) throw PromptError;
            return "Return a JSON object with action=idle and dialogue=hello.";
        }
    }

    public static class ToolBridge
    {
        public static int Calls;
        public static int LastThreadId;
        public static string ExecuteTool(string name, string arguments)
        {
            Calls++;
            LastThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            return "{\"success\":true}";
        }
    }
}
