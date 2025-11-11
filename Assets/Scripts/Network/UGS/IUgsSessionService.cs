using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Services.Multiplayer;

namespace Network.UGS {
    /// <summary>
    /// Thin wrapper around Unity Gaming Services session APIs.
    /// Keeps all UGS-specific calls centralized and mockable for tests.
    /// </summary>
    public interface IUgsSessionService {
        /// <summary>Ensures UGS is initialized and the player is signed in (anonymously if needed).</summary>
        UniTask InitializeAsync();
        /// <summary>Holds the last created/joined session (optional convenience).</summary>
        ISession Active { get; set; }

        /// <summary>Create a new session with given options.</summary>
        UniTask<ISession> CreateAsync(SessionOptions options);
        /// <summary>Join an existing session by code.</summary>
        UniTask<ISession> JoinByCodeAsync(string code, JoinSessionOptions options);
        /// <summary>Leave session (client) or delete (host). Swallows transient errors.</summary>
        UniTask LeaveOrDeleteAsync(ISession session);

        /// <summary>Attach session event handlers for changes and property updates.</summary>
        void HookEvents(ISession s,
            System.Action onChanged,
            System.Action<string> onJoined,
            System.Action<string> onLeaving,
            System.Action onPropsChanged);

        /// <summary>Detach session event handlers.</summary>
        void UnhookEvents(ISession s,
            System.Action onChanged,
            System.Action<string> onJoined,
            System.Action<string> onLeaving,
            System.Action onPropsChanged);
    }
}