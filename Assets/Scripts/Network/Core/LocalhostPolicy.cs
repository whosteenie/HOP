using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Network.Core {
    /// <summary>
    /// Default localhost policy: ParrelSync clone detection, multiple-process heuristic,
    /// and current UnityTransport address check (127.0.0.1/localhost).
    /// </summary>
    public sealed class LocalhostPolicy : ILocalhostPolicy {
        /// <inheritdoc />
        public bool IsLocalhostTesting() {
            if(Application.dataPath.Contains("_clone")) return true;
            var name = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            if(System.Diagnostics.Process.GetProcessesByName(name).Length > 1) return true;

            if(Unity.Netcode.NetworkManager.Singleton.NetworkConfig.NetworkTransport is not UnityTransport utp)
                return false;

            return utp.ConnectionData.Address is "127.0.0.1" or "localhost";
        }

        /// <inheritdoc />
        public string GetLocalEditorName() {
            if(Application.dataPath.Contains("_clone")) {
                var parts = Application.dataPath.Split('_');
                if(parts.Length > 1 && int.TryParse(parts[^1], out int i)) return $"Editor {i + 1}";
            }

            return $"Editor {System.Diagnostics.Process.GetCurrentProcess().Id}";
        }
    }
}