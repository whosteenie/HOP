using System.Text.RegularExpressions;

namespace Game.Social {
    internal static class ChatProfanityFilter {
        // Intentionally lightweight local display filter. Meant to be improved over time.
        private static readonly string[] RootTerms = {
            "fuck",
            "shit",
            "bitch",
            "asshole",
            "dick",
            "cunt",
            "nigg",
            "fagg",
            "retard"
        };

        private static readonly Regex NonLetterRegex = new("[^a-z]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex MultiRepeatRegex = new("(.)\\1{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string Censor(string message) {
            if(string.IsNullOrWhiteSpace(message)) return message;

            var filtered = message;
            foreach(var root in RootTerms) {
                // Match common word forms: plural, past, present participle, agent nouns, adverbs.
                var pattern = $@"\b{Regex.Escape(root)}(?:s|es|ed|er|ers|ing|y|ies)?\b";
                filtered = Regex.Replace(
                    filtered,
                    pattern,
                    m => new string('*', m.Value.Length),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            // Basic normalized pass for common obfuscation ("fuuuck", punctuation-split words, etc.).
            var normalized = NormalizeForDetection(message);
            foreach(var root in RootTerms) {
                if(normalized.Contains(root) == false) continue;

                // If normalized text includes a root term, do a permissive raw pass for that root.
                var permissivePattern = Regex.Escape(root[0].ToString()) + @"[\W_]*" + string.Join(@"[\W_]*", root[1..].ToCharArray());
                filtered = Regex.Replace(
                    filtered,
                    permissivePattern,
                    m => new string('*', m.Value.Length),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            return filtered;
        }

        private static string NormalizeForDetection(string value) {
            var lowered = value.ToLowerInvariant()
                .Replace("0", "o")
                .Replace("1", "i")
                .Replace("3", "e")
                .Replace("4", "a")
                .Replace("5", "s")
                .Replace("7", "t")
                .Replace("@", "a")
                .Replace("$", "s");

            lowered = NonLetterRegex.Replace(lowered, "");
            lowered = MultiRepeatRegex.Replace(lowered, "$1$1");
            return lowered;
        }
    }
}
