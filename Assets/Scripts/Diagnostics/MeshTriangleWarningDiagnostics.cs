using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Diagnostics {
    /// <summary>
    /// Event-driven diagnostics for low-repro mesh triangle warnings.
    /// Activates when Unity emits the warning and logs candidate scene objects using that mesh.
    /// </summary>
    public static class MeshTriangleWarningDiagnostics {
        private static readonly HashSet<string> SeenWarnings = new();
        private static readonly Regex QuotedTokenRegex = new("\"([^\"]+)\"|'([^']+)'", RegexOptions.Compiled);
        private static bool registered;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() {
            if(registered) return;
            registered = true;
            Application.logMessageReceived += OnLogMessageReceived;
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type) {
            if(type != LogType.Warning) return;
            if(string.IsNullOrEmpty(condition)) return;
            if(IsTargetWarning(condition) == false) return;
            if(SeenWarnings.Add(condition) == false) return;

            var meshHints = ExtractMeshHints(condition);
            var report = BuildReport(condition, meshHints);
            Debug.LogWarning(report);
        }

        private static bool IsTargetWarning(string message) {
            var lower = message.ToLowerInvariant();
            if(lower.Contains("triangle") == false) return false;
            if(lower.Contains("mesh") == false) return false;

            // Focus specifically on problematic physics-mesh warnings.
            if(lower.Contains("large") || lower.Contains("area") || lower.Contains("degenerate")) return true;

            // Keep the known offender searchable even if warning phrasing varies.
            return lower.Contains("pb_mesh");
        }

        private static List<string> ExtractMeshHints(string message) {
            var hints = new List<string>();

            var matches = QuotedTokenRegex.Matches(message);
            for(var i = 0; i < matches.Count; i++) {
                var token = matches[i].Groups[1].Success ? matches[i].Groups[1].Value : matches[i].Groups[2].Value;
                if(string.IsNullOrWhiteSpace(token)) continue;
                if(token.IndexOf("mesh", StringComparison.OrdinalIgnoreCase) < 0 &&
                   token.IndexOf("pb_", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if(hints.Contains(token)) continue;
                hints.Add(token);
            }

            if(hints.Count == 0 && message.IndexOf("pb_Mesh", StringComparison.OrdinalIgnoreCase) >= 0) {
                hints.Add("pb_Mesh");
            }

            return hints;
        }

        private static string BuildReport(string warning, List<string> meshHints) {
            var sb = new StringBuilder();
            sb.AppendLine("[MeshTriangleWarningDiagnostics] Captured mesh triangle warning.");
            sb.AppendLine($"[MeshTriangleWarningDiagnostics] Scene: {SceneManager.GetActiveScene().name}");
            sb.AppendLine($"[MeshTriangleWarningDiagnostics] Warning: {warning}");

            sb.AppendLine(meshHints.Count > 0
                ? $"[MeshTriangleWarningDiagnostics] Parsed mesh hints: {string.Join(", ", meshHints)}"
                : "[MeshTriangleWarningDiagnostics] Parsed mesh hints: <none>");

            var candidates = CollectCandidates(meshHints);
            if(candidates.Count == 0) {
                sb.AppendLine("[MeshTriangleWarningDiagnostics] Candidate objects: <none found>");
                sb.AppendLine("[MeshTriangleWarningDiagnostics] Next step: rerun with Development Build and capture this warning block.");
                return sb.ToString();
            }

            sb.AppendLine($"[MeshTriangleWarningDiagnostics] Candidate objects ({candidates.Count}):");
            foreach(var t in candidates) {
                sb.AppendLine(t);
            }

            return sb.ToString();
        }

        private static List<string> CollectCandidates(List<string> meshHints) {
            var candidates = new List<string>();
            var seenPaths = new HashSet<string>();

            AddColliderCandidates(meshHints, candidates, seenPaths);
            AddFilterCandidates(meshHints, candidates, seenPaths);
            AddSkinnedCandidates(meshHints, candidates, seenPaths);

            return candidates;
        }

        private static void AddColliderCandidates(List<string> meshHints, List<string> candidates, HashSet<string> seenPaths) {
            var colliders = CollectInLoadedScenes<MeshCollider>();
            foreach(var c in colliders) {
                if(c == null) continue;
                var mesh = c.sharedMesh;
                if(mesh == null) continue;
                if(IsMeshMatch(mesh.name, meshHints) == false) continue;
                AddCandidate(c.gameObject, "MeshCollider", mesh.name, candidates, seenPaths);
            }
        }

        private static void AddFilterCandidates(List<string> meshHints, List<string> candidates, HashSet<string> seenPaths) {
            var filters = CollectInLoadedScenes<MeshFilter>();
            foreach(var f in filters) {
                if(f == null) continue;
                var mesh = f.sharedMesh;
                if(mesh == null) continue;
                if(IsMeshMatch(mesh.name, meshHints) == false) continue;
                AddCandidate(f.gameObject, "MeshFilter", mesh.name, candidates, seenPaths);
            }
        }

        private static void AddSkinnedCandidates(List<string> meshHints, List<string> candidates, HashSet<string> seenPaths) {
            var skinned = CollectInLoadedScenes<SkinnedMeshRenderer>();
            foreach(var r in skinned) {
                if(r == null) continue;
                var mesh = r.sharedMesh;
                if(mesh == null) continue;
                if(IsMeshMatch(mesh.name, meshHints) == false) continue;
                AddCandidate(r.gameObject, "SkinnedMeshRenderer", mesh.name, candidates, seenPaths);
            }
        }

        private static bool IsMeshMatch(string meshName, List<string> hints) {
            if(string.IsNullOrEmpty(meshName)) return false;
            if(hints == null || hints.Count == 0) return meshName.IndexOf("pb_Mesh", StringComparison.OrdinalIgnoreCase) >= 0;

            foreach(var hint in hints) {
                if(string.IsNullOrEmpty(hint)) continue;
                if(meshName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if(hint.IndexOf(meshName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return false;
        }

        private static void AddCandidate(GameObject go, string componentType, string meshName, List<string> candidates, HashSet<string> seenPaths) {
            var path = GetHierarchyPath(go != null ? go.transform : null);
            if(string.IsNullOrEmpty(path)) return;
            if(seenPaths.Add(path) == false) return;
            candidates.Add($"- {path} [{componentType}] mesh='{meshName}' active={go.activeInHierarchy}");
        }

        private static string GetHierarchyPath(Transform t) {
            if(t == null) return "";
            var path = t.name;
            while(t.parent != null) {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        private static List<T> CollectInLoadedScenes<T>() where T : Component {
            var result = new List<T>();
            var sceneCount = SceneManager.sceneCount;
            for(var i = 0; i < sceneCount; i++) {
                var scene = SceneManager.GetSceneAt(i);
                if(!scene.IsValid() || !scene.isLoaded) continue;
                var roots = scene.GetRootGameObjects();
                foreach(var root in roots) {
                    if(root == null) continue;
                    result.AddRange(root.GetComponentsInChildren<T>(true));
                }
            }

            return result;
        }
    }
}
