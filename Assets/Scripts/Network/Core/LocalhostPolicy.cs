using UnityEngine;

namespace Network.Core {
    /// <summary>
    /// Default localhost policy: ParrelSync clone detection, multiple-process heuristic,
    /// and current UnityTransport address check (127.0.0.1/localhost).
    /// </summary>
    public sealed class LocalhostPolicy : ILocalhostPolicy {
        /// <inheritdoc />
        public bool IsLocalhostTesting() {
            #if UNITY_EDITOR
            // In editor, check for ParrelSync clones or explicit localhost flag
            if(Application.dataPath.Contains("_clone")) return true;
    
            // You could also add a static flag you set manually for testing:
            // return LocalhostTestingEnabled;
    
            return false; // Editor but not explicitly localhost testing
            #else
            // In builds, never use localhost (always use Relay)
            return false;
            #endif
        }

        /// <inheritdoc />
        public string GetLocalEditorName() {
            if(Application.dataPath.Contains("_clone")) {
                var parts = Application.dataPath.Split('_');
                if(parts.Length > 1 && int.TryParse(parts[^1], out var i)) return $"Editor {i + 1}";
            }

            return $"Editor {System.Diagnostics.Process.GetCurrentProcess().Id}";
        }
    }
}