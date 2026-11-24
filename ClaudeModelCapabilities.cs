using System;
using System.Text.RegularExpressions;

namespace MetaDataIAPlugin
{
    /// <summary>
    /// Request-shape rules for Claude models, derived from the model id
    /// (e.g. "claude-opus-5-5", "claude-haiku-4-5-20251001", "anthropic/claude-sonnet-5.5").
    /// </summary>
    internal static class ClaudeModelCapabilities
    {
        // Effort used for metadata work: grounded summarising and classification, so
        // "low" keeps thinking short (cheaper, faster) on models that think by default.
        public const string MetadataEffort = "low";

        // Extra max_tokens room for models whose thinking counts toward max_tokens.
        // Only tokens actually produced are billed, so this costs nothing unless used.
        public const int ThinkingHeadroomTokens = 8192;

        private static readonly Regex ModelPattern = new Regex(
            @"claude-(opus|sonnet|haiku|fable|mythos)-(\d+)(?:[.-](\d{1,2}))?(?=$|[^0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Claude Opus 4.7 and later, every Claude 5.x model, and Fable/Mythos reject
        /// temperature, top_p and top_k with a 400 error.
        /// </summary>
        public static bool RejectsSamplingParameters(string model)
        {
            string family;
            double version;
            if (!TryParse(model, out family, out version))
            {
                return false;
            }

            if (family == "fable" || family == "mythos" || version >= 5.0)
            {
                return true;
            }

            return family == "opus" && version >= 4.7;
        }

        /// <summary>
        /// Models that accept output_config.effort: Opus 4.5+, Sonnet 4.6+, Haiku 5.5+,
        /// every Claude 5.x model, and Fable/Mythos. These are also the models that may
        /// think before answering, so they get the max_tokens headroom.
        /// </summary>
        public static bool SupportsEffort(string model)
        {
            string family;
            double version;
            if (!TryParse(model, out family, out version))
            {
                return false;
            }

            if (family == "fable" || family == "mythos" || version >= 5.0)
            {
                return true;
            }

            if (family == "opus")
            {
                return version >= 4.5;
            }

            if (family == "sonnet")
            {
                return version >= 4.6;
            }

            return false;
        }

        /// <summary>
        /// True when a 400 response body complains about a sampling parameter.
        /// </summary>
        public static bool IsSamplingRejection(string responseText)
        {
            return ContainsAny(responseText, "temperature", "top_p", "top_k");
        }

        /// <summary>
        /// True when a 400 response body complains about the effort setting.
        /// </summary>
        public static bool IsEffortRejection(string responseText)
        {
            return ContainsAny(responseText, "effort", "output_config");
        }

        private static bool TryParse(string model, out string family, out double version)
        {
            family = null;
            version = 0;
            if (string.IsNullOrWhiteSpace(model))
            {
                return false;
            }

            var match = ModelPattern.Match(model);
            if (!match.Success)
            {
                return false;
            }

            family = match.Groups[1].Value.ToLowerInvariant();
            var major = int.Parse(match.Groups[2].Value);
            var minor = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;

            // Date suffixes ("-20250514") never match the minor group: the lookahead
            // requires a non-digit (or the end) right after one or two digits.
            version = major + (minor / 10.0);
            return true;
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (var needle in needles)
            {
                if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
