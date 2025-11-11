namespace Network.Core {
    /// <summary>
    /// Encapsulates rules for detecting "localhost testing" (editor clones, multiple Unity instances, IP == 127.0.0.1).
    /// Also provides a stable local editor display name (clone index or process id).
    /// </summary>
    public interface ILocalhostPolicy {
        /// <summary>True if this environment should use direct IP instead of Relay.</summary>
        bool IsLocalhostTesting();
        /// <summary>Human-readable label for local editor instances ("Editor 1", "Editor 2", or "Editor {pid}").</summary>
        string GetLocalEditorName();
    }
}