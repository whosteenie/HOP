using UnityEngine;
using UnityEngine.UIElements;

namespace Network.Services {
    /// <summary>
    /// Centralized service for join code display and clipboard operations.
    /// Provides consistent join code handling across menu managers.
    /// </summary>
    public static class JoinCodeService {
        /// <summary>
        /// Formats a join code for display in a label.
        /// </summary>
        /// <param name="code">The join code to format.</param>
        /// <returns>Formatted string: "Join Code: ABC123" or "Join Code: - - - - - -" if code is empty.</returns>
        public static string FormatJoinCodeLabel(string code) {
            if(string.IsNullOrEmpty(code)) {
                return "Join Code: - - - - - -";
            }
            return $"Join Code: {code}";
        }

        /// <summary>
        /// Extracts the join code from a formatted label text.
        /// </summary>
        /// <param name="labelText">The label text (e.g., "Join Code: ABC123").</param>
        /// <returns>The extracted code (e.g., "ABC123") or null if invalid.</returns>
        public static string ExtractCodeFromLabel(string labelText) {
            if(string.IsNullOrEmpty(labelText)) return null;
            
            var code = labelText.Replace("Join Code: ", "").Trim();
            
            // Return null if it's the placeholder
            if(code == "- - - - - -") return null;
            
            return string.IsNullOrEmpty(code) ? null : code;
        }

        /// <summary>
        /// Copies a join code to the system clipboard.
        /// </summary>
        /// <param name="code">The join code to copy.</param>
        /// <returns>True if code was copied, false if code was invalid.</returns>
        public static bool CopyToClipboard(string code) {
            if(string.IsNullOrEmpty(code) || code == "- - - - - -") {
                return false;
            }

            GUIUtility.systemCopyBuffer = code;
            return true;
        }

        /// <summary>
        /// Updates join code display UI elements with a code.
        /// </summary>
        /// <param name="label">The label to update with formatted code.</param>
        /// <param name="copyButton">The copy button to enable/disable based on code validity.</param>
        /// <param name="code">The join code to display.</param>
        public static void UpdateJoinCodeDisplay(Label label, Button copyButton, string code) {
            if(label != null) {
                label.text = FormatJoinCodeLabel(code);
            }

            if(copyButton != null) {
                copyButton.SetEnabled(!string.IsNullOrEmpty(code) && code != "- - - - - -");
            }
        }

        /// <summary>
        /// Copies join code from a label to clipboard.
        /// </summary>
        /// <param name="label">The label containing the formatted join code.</param>
        /// <returns>True if code was copied, false if no valid code was found.</returns>
        public static bool CopyFromLabel(Label label) {
            if(label == null) return false;
            
            var code = ExtractCodeFromLabel(label.text);
            return CopyToClipboard(code);
        }
    }
}
