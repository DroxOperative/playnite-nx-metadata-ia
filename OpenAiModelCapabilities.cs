using System;
using System.Text.RegularExpressions;

namespace MetaDataIAPlugin
{
    /// <summary>
    /// Request-shape rules for the OpenAI-compatible chat completions path. Applies to
    /// OpenAI itself and to any gateway (OpenRouter, custom endpoints) serving the same
    /// models, e.g. "gpt-5.2", "o4-mini", "openai/gpt-5-mini".
    /// </summary>
    internal static class OpenAiModelCapabilities
    {
        // Reasoning effort for metadata work on OpenAI reasoning models.
        public const string MetadataReasoningEffort = "low";

        // max_completion_tokens counts reasoning tokens too, so reasoning models get extra room.
        // Only tokens actually produced are billed.
        public const int ReasoningHeadroomTokens = 8192;

        // OpenAI reasoning models: the GPT-5 family (gpt-5, gpt-5.x, -mini, -nano, -pro, -codex)
        // and the o-series (o1, o3, o3-mini, o4-mini...). gpt-oss is a different, open model.
        private static readonly Regex ReasoningModelPattern = new Regex(
            @"^(?:openai/)?(?:gpt-5(?:$|[.\-])|o\d+(?:$|-))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// OpenAI reasoning models reject temperature/top_p and max_tokens
        /// (they need max_completion_tokens), and accept reasoning_effort.
        /// </summary>
        public static bool IsReasoningModel(string model)
        {
            return !string.IsNullOrWhiteSpace(model) && ReasoningModelPattern.IsMatch(model.Trim());
        }

        /// <summary>
        /// Whether temperature should be left out for this model on the OpenAI-compatible path,
        /// covering OpenAI reasoning models and newer Claude models served through a gateway.
        /// </summary>
        public static bool RejectsSamplingParameters(string model)
        {
            return IsReasoningModel(model) || ClaudeModelCapabilities.RejectsSamplingParameters(model);
        }

        /// <summary>
        /// True when a 400 response body complains about a sampling parameter.
        /// </summary>
        public static bool IsSamplingRejection(string responseText)
        {
            return ClaudeModelCapabilities.IsSamplingRejection(responseText);
        }

        /// <summary>
        /// True when a 400 response body asks for max_completion_tokens instead of max_tokens.
        /// </summary>
        public static bool IsMaxTokensRejection(string responseText)
        {
            return !string.IsNullOrEmpty(responseText) &&
                   responseText.IndexOf("max_completion_tokens", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
