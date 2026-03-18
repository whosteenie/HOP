using System.Diagnostics;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Events {
    /// <summary>
    /// Editor and development-build logging wrapper for the Events assembly.
    /// Keeps EventBus-related performance inspections from drowning out more useful warnings.
    /// </summary>
    internal static class EventDevLog {
        // ReSharper disable Unity.PerformanceAnalysis
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        internal static void Log(object message, Object context = null) {
            Debug.Log(message, context);
        }

        // ReSharper disable Unity.PerformanceAnalysis
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        internal static void LogWarning(object message, Object context = null) {
            Debug.LogWarning(message, context);
        }

        // ReSharper disable Unity.PerformanceAnalysis
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        internal static void LogError(object message, Object context = null) {
            Debug.LogError(message, context);
        }
    }
}
