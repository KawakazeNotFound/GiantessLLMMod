using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GiantessLLMMod.Models;

namespace GiantessLLMMod.Core
{
    /// <summary>Provider parameter policy, separate from transport and game logic.</summary>
    public static class ChatRequestBuilder
    {
        public static ChatCompletionRequest Build(string url, string model, List<ChatMessage> messages,
            float temperature, int maxTokens, List<ToolDefinition> tools, string tokenLimitParameter)
        {
            model = (model ?? "").Trim();
            if (model.Length == 0) throw new ArgumentException("LLM model name is empty.");
            if (maxTokens <= 0) throw new ArgumentException("LLM MaxTokens must be greater than zero.");

            string mode = (tokenLimitParameter ?? "Auto").Trim();
            bool modernGpt = Regex.IsMatch(model, @"^gpt-(?:5|6)(?:$|[.-])", RegexOptions.IgnoreCase);
            bool oSeries = Regex.IsMatch(model, @"^o[1-9]\d*(?:$|-)", RegexOptions.IgnoreCase);
            bool official = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);
            bool completionBudget;
            if (mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                completionBudget = official || modernGpt || oSeries;
            else if (mode.Equals("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
                completionBudget = true;
            else if (mode.Equals("max_tokens", StringComparison.OrdinalIgnoreCase))
                completionBudget = false;
            else
                throw new ArgumentException("TokenLimitParameter must be Auto, max_tokens, or max_completion_tokens.");

            // GPT-6 Sol/Luna chat function calling requires reasoning_effort=none.
            // Other reasoning models keep their server default, without sampling parameters.
            bool hasTools = tools != null && tools.Count > 0;
            bool nonReasoningChatTools = hasTools && Regex.IsMatch(model,
                @"^gpt-6-(?:luna|sol)(?:$|-\d{4}-\d{2}-\d{2}$)", RegexOptions.IgnoreCase);
            var request = new ChatCompletionRequest
            {
                Model = model,
                Messages = messages,
                Temperature = modernGpt || oSeries ? (float?)null : temperature,
                ReasoningEffort = nonReasoningChatTools ? "none" : null,
                Tools = hasTools ? tools : null
            };
            if (completionBudget) request.MaxCompletionTokens = maxTokens;
            else request.MaxTokens = maxTokens;
            return request;
        }
    }
}
