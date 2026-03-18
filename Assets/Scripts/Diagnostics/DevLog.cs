using System.Diagnostics;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Diagnostics {
    /// <summary>
    /// Lightweight diagnostic logging wrapper for editor and development builds.
    /// Helps keep Unity performance inspections focused on gameplay code instead of debug output.
    /// </summary>
    public static class DevLog {
        // ReSharper disable Unity.PerformanceAnalysis
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public static void Log(object message, Object context = null) {
            Debug.Log(message, context);
        }

        // ReSharper disable Unity.PerformanceAnalysis
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public static void LogWarning(object message, Object context = null) {
            Debug.LogWarning(message, context);
        }

        // ReSharper disable Unity.PerformanceAnalysis
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public static void LogError(object message, Object context = null) {
            Debug.LogError(message, context);
        }
    }
}
