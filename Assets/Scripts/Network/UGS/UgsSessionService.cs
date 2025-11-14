using System;
using Cysharp.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;

namespace Network.UGS {
    /// <summary>
    /// Default implementation: initializes UGS/Auth, wraps create/join/leave,
    /// and provides safe event hook/unhook helpers.
    /// </summary>
    public sealed class UgsSessionService : IUgsSessionService {
        /// <inheritdoc />
        public ISession Active { get; set; }

        /// <inheritdoc />
        public async UniTask InitializeAsync() {
            if(UnityServices.State != ServicesInitializationState.Initialized) {
                await UnityServices.InitializeAsync();
            }

            if(!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        /// <inheritdoc />
        public async UniTask<ISession> CreateAsync(SessionOptions options) {
            Active = await MultiplayerService.Instance.CreateSessionAsync(options);
            return Active;
        }

        /// <inheritdoc />
        public async UniTask<ISession> JoinByCodeAsync(string code, JoinSessionOptions options) {
            Active = await MultiplayerService.Instance.JoinSessionByCodeAsync(code, options);
            return Active;
        }

        /// <inheritdoc />
        public async UniTask LeaveOrDeleteAsync(ISession session) {
            if(session == null) return;
            try {
                if(session.IsHost) await session.AsHost().DeleteAsync();
                else await session.LeaveAsync();
            } catch { /* ignore transient */
            }
        }

        /// <inheritdoc />
        public void HookEvents(ISession s, Action onChanged, Action<string> onJoined, Action<string> onLeaving, Action onPropsChanged) {
            if(s == null) return;
            s.Changed += onChanged;
            s.PlayerJoined += onJoined;
            s.PlayerLeaving += onLeaving;
            s.SessionPropertiesChanged += onPropsChanged;
        }

        /// <inheritdoc />
        public void UnhookEvents(ISession s, Action onChanged, Action<string> onJoined, Action<string> onLeaving, Action onPropsChanged) {
            if(s == null) return;
            s.Changed -= onChanged;
            s.PlayerJoined -= onJoined;
            s.PlayerLeaving -= onLeaving;
            s.SessionPropertiesChanged -= onPropsChanged;
        }
        
        public async UniTask<ISession> ReconnectToSessionAsync(string sessionId) {
            Active = await MultiplayerService.Instance.ReconnectToSessionAsync(sessionId);
            return Active;
        }
    }
}